using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Services;

// Lines standing still right now, each from the start of its stop (same rules
// as the Arrêts window: StopDetector.StoppedRuns, Parametres threshold,
// planned breaks left out), with their qualification or calendar slot. Read
// once a minute at most and shared by the main and TV decks.
public static class OngoingStopService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(50);

    // First look back over a few hours; a stop already running at its start
    // is searched further back, for that line only.
    private static readonly TimeSpan[] Lookbacks = { TimeSpan.FromHours(3), TimeSpan.FromDays(3), TimeSpan.FromDays(15) };

    // GetLineSegmentsAsync reads snapshots from this long before `from`: a
    // stop starting that early may have started earlier still.
    private static readonly TimeSpan SnapshotMargin = TimeSpan.FromHours(6);

    // A stop is still going on while nothing came after it: its last snapshot
    // is recent, or older than this and GetLineSegmentsAsync has already
    // added the "no data" stretch up to now (StatsViewer silent over 10 min).
    // Shorter would drop every stopped line for a few minutes whenever
    // StatsViewer pauses, and bring them back as new stops (new pop-up).
    public static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(11);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _readAt = DateTime.MinValue;
    private static IReadOnlyDictionary<string, OngoingStop> _last = new Dictionary<string, OngoingStop>();

    // Start found by a deep look-back, kept while the line stays stopped so
    // the next reads only look back a few hours.
    private static readonly Dictionary<string, DateTime> KnownStarts = new();

    public static async Task<IReadOnlyDictionary<string, OngoingStop>> GetAsync(ProductionRepository repository, AppSettings settings)
    {
        await Gate.WaitAsync();
        try
        {
            if (DateTime.Now - _readAt < CacheFor)
                return _last;
            _last = await ReadAsync(repository, settings);
            _readAt = DateTime.Now;
            return _last;
        }
        catch (Exception ex)
        {
            AppLogger.Log("Arrêts en cours illisibles : " + ex.Message);
            return _last;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<IReadOnlyDictionary<string, OngoingStop>> ReadAsync(ProductionRepository repository, AppSettings settings)
    {
        var now = DateTime.Now;
        var machines = await repository.GetMachinesAsync();
        var names = machines.ToDictionary(m => m.MachineId, m => m.MachineName);
        var windows = BreakSchedule.DayWindows(settings.PlannedBreaks);
        var threshold = Math.Max(1, settings.StopThresholdMinutes);

        var starts = new Dictionary<string, (DateTime Start, bool NoData)>();
        var pending = machines.Select(m => m.MachineId).ToList();
        foreach (var lookback in Lookbacks)
        {
            if (pending.Count == 0)
                break;
            var from = now - lookback;
            var segments = await repository.GetLineSegmentsAsync(pending, from, now.AddMinutes(1));
            var byLine = segments.ToLookup(s => s.MachineId);
            var deeper = new List<string>();
            foreach (var id in pending)
            {
                var run = StopDetector.StoppedRuns(byLine[id].OrderBy(s => s.StartTime).ToList()).LastOrDefault();
                if (run == null || now - run[^1].EndTime > LiveWindow)
                {
                    KnownStarts.Remove(id);
                    continue;
                }
                var start = run[0].StartTime;
                var noData = run[^1].State == LineState.NoData;
                // Starts right where the read began: it may go further back.
                var clipped = start >= from - SnapshotMargin && start <= from - SnapshotMargin + TimeSpan.FromMinutes(5);
                if (clipped && KnownStarts.TryGetValue(id, out var known) && known <= start)
                {
                    starts[id] = (known, noData);
                    continue;
                }
                if (clipped && lookback != Lookbacks[^1])
                {
                    deeper.Add(id);
                    continue;
                }
                starts[id] = (start, noData);
                if (lookback != Lookbacks[0])
                    KnownStarts[id] = start;
            }
            pending = deeper;
        }

        // Too short (or only a planned break so far): not a stop yet.
        var stopped = starts
            .Where(s => (now - s.Value.Start).TotalMinutes - BreakSchedule.OverlapMinutes(windows, s.Value.Start, now) >= threshold)
            .ToList();
        if (stopped.Count == 0)
            return new Dictionary<string, OngoingStop>();

        var earliest = stopped.Min(s => s.Value.Start);
        var qualifications = await ReadQualificationsAsync(repository, earliest.AddMinutes(-5), now.AddMinutes(1));
        var slots = await repository.GetActiveSlotsAsync();

        var result = new Dictionary<string, OngoingStop>();
        foreach (var (id, (start, noData)) in stopped)
        {
            // A qualification saved for this stop (its start, or the start of
            // one of its later poste pieces); the latest wins.
            var qualification = qualifications
                .Where(q => q.MachineId == id && q.StopStart >= start.AddMinutes(-3) && q.StopStart <= now)
                .OrderByDescending(q => q.StopStart)
                .FirstOrDefault();
            // A planned stop wins over a maintenance, as on the run-state photos.
            var slot = slots.Where(e => e.MachineId == id).OrderByDescending(e => e.Kind).FirstOrDefault();
            result[id] = new OngoingStop
            {
                MachineId = id,
                MachineName = names.GetValueOrDefault(id, id),
                Start = start,
                IsNoData = noData,
                Qualification = qualification,
                Slot = slot,
            };
        }
        return result;
    }

    private static async Task<List<StopQualification>> ReadQualificationsAsync(ProductionRepository repository, DateTime from, DateTime to)
    {
        try
        {
            return await repository.GetStopQualificationsAsync(from, to);
        }
        catch (Exception ex)
        {
            AppLogger.Log("Causes des arrêts (Planification) illisibles : " + ex.Message);
            return new();
        }
    }
}
