using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using Dapper;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.Data;

public enum BucketSize
{
    Hour,
    Day,
}

// Reads Stiavelli's dbo.produzione directly. Each row there is a SNAPSHOT of
// the production running on a line (ID_produzione), with counters cumulative
// since that production started. Stiavelli's snapshot cadence is
// configurable (30 min by default, can go under 1 min), so every query here:
//   - only reads the snapshots of its own time range (+ SnapshotMargin around
//     it, to find the counters each interval starts from) -- seeks on the
//     IX_produzione_linea_fine index created by stiavelli_compat.sql;
//   - turns consecutive snapshots into per-interval deltas and sums them
//     into hour/day buckets ON THE SERVER, so what reaches the app doesn't
//     grow with the snapshot frequency.
// Each interval runs from the LINE's previous snapshot to this one and is
// attributed to the bucket containing its midpoint. Deltas are computed per
// ID_produzione (one article run on one line; a new one is opened at each
// changeover, so a new production simply starts again from zero), against
// the running max of each counter, so StatsViewer glitches (all-zero rows,
// counters stepping back) never count twice.
//
// Weights come from Stiavelli's lotto table (real lot averages) when the
// line has lots in the range, otherwise from produzione.media_totale when it
// looks sane (see WeightParts).
//
// Line status (connected / belt running / availability) is derived from the
// same snapshots -- Stiavelli doesn't store it anywhere.
//
// Date bounds are always DateTime parameters, never strings: a 'yyyy-MM-dd'
// string compared to a SQL Server DATETIME is parsed with the login's
// language (French = DMY on this site) and silently swaps day/month.
public partial class ProductionRepository
{
    // How far before/after the requested range snapshots are read. Covers the
    // interval crossing each boundary as long as Stiavelli wasn't silent for
    // longer than this; beyond that, the first interval after the gap is
    // dropped rather than guessed.
    private static readonly TimeSpan SnapshotMargin = TimeSpan.FromHours(6);

    // Longest snapshot interval that is counted. The cadence is 20 s - 2 min
    // today (30 min historically), so a longer interval means StatsViewer was
    // silent and nobody knows when its bags were made: the whole interval
    // (bags and time) is dropped. Keeping it put e.g. CC01's 6.9 M bags of
    // 08/09 -> 06/10 or PL03's 1.7 M of 14/03 -> 20/10 into a single hour.
    private const int MaxIntervalSeconds = 7200;

    // Faster than this over an interval's real elapsed time (at least a
    // minute) is a counter glitch, not production: the interval's deltas are
    // dropped. No line here comes anywhere near 300 bags/min.
    private const int MaxBagsPerMinute = 300;

    // Max speed = best rate over this many consecutive snapshot intervals of
    // the line (5 minutes at the lines' 60 s snapshots), not over one: a
    // snapshot taken before a few bags are counted makes a short minute then a
    // long one (CC01 31/10: 35, 57, 34 bags while running at 46/min).
    private const int MaxSpeedWindowIntervals = 5;

    // Shortest window used for the max speed (start of the read range, or
    // faster snapshots): too short for the rate to mean anything.
    private const int MinSpeedWindowSeconds = 270;

    // Longest snapshot interval used for the average speed: past this the
    // line may have stopped and restarted between two snapshots.
    private const int MaxSteadyIntervalSeconds = 300;

    // Latest snapshots read for the line status: enough to reach the current
    // production's previous snapshot past duplicates and stray rows.
    private const int StatusRows = 60;

    // The production database ("stats"): only read.
    private readonly IDbConnectionFactory _connectionFactory;
    // The application database (spiga_suivi): planning, note, machine names.
    private readonly IDbConnectionFactory _appData;

    // Note and machine names stay in stats while PCs still run 2.0.
    private readonly PlantDataLocation _location;
    private readonly AppSettings _settings;

    // Planned stops (maintenance slots, "Planifié" stops) left out of the TRS.
    private readonly PlanningRepository _planning;

    // Whether Stiavelli's lotto table is readable (null = not checked yet).
    // The SQLite dev DB has none: weights then come from media_totale.
    private bool? _hasLotTable;

    // live: the production database ("stats"), never written. appData: the
    // application database (spiga_suivi), where everything the dashboard
    // writes goes. access: the machines the signed-in account may see (SPIGA
    // Suivi Production); every machine when not given (kiosk, TV screen,
    // services that filter themselves).
    public ProductionRepository(IDbConnectionFactory live, IDbConnectionFactory appData, AppSettings settings, MachineAccess? access = null)
    {
        _connectionFactory = live;
        _appData = appData;
        _settings = settings;
        // Planned stops count in every machine's TRS: read unrestricted, then
        // filtered on the way out.
        _planning = new PlanningRepository(appData, live);
        _location = new PlantDataLocation(appData, live);
        Access = access ?? MachineAccess.Everything();
    }

    // Applied by every method below: machines outside it are never listed,
    // read or written.
    public MachineAccess Access { get; }

    // Drops pooled connections, which are all dead after a network/server drop
    // and would otherwise make the first queries after a reconnect fail again.
    // Settings may also point at another database now: look for lotto again.
    public void ResetConnections()
    {
        _hasLotTable = null;
        ClearDayCache();
        ResetDayStore();
        _connectionFactory.ResetPool();
    }

    public async Task<List<Machine>> GetMachinesAsync()
    {
        await OffUiThread.Switch();
        using var connection = _connectionFactory.CreateConnection();
        var machines = (await connection.QueryAsync<Machine>(
            "SELECT MachineId, MachineName, MachineType, Sector, IPAddress, IsActive FROM Machines ORDER BY MachineId")).ToList();
        Access.Remember(machines);
        await ApplyDisplayNamesAsync(machines);
        return machines.Where(m => Access.Allows(m.MachineId, m.Sector)).ToList();
    }

    private sealed class SnapshotRow
    {
        public string ProductionId { get; set; } = "";
        public long Progressivo { get; set; }
        public DateTime SnapshotTime { get; set; }
        public DateTime? ProductionStart { get; set; }
        public bool Terminated { get; set; }
        public string? ArticleCode { get; set; }
        public string? ProductName { get; set; }
        public long TotalBags { get; set; }
        public long BagsInTolerance { get; set; }
        public long BagsUnderTolerance { get; set; }
        public long BagsOverTolerance { get; set; }
        public double? AvgWeight { get; set; }
        public double? TargetNetWeight { get; set; }
        public DateTime ServerNow { get; set; }

        // ID_produzione = line + article + start: fallback for a blank tempo_inizio.
        public DateTime Start => ProductionStart ?? ProductionIdParts.Parse(ProductionId).StartTime ?? SnapshotTime;
    }

    // Connected = the line's latest valid snapshot is recent, judged against
    // the line's own cadence (2.5x the latest non-zero gap between its
    // snapshots, 2 min to 2 h) and the SERVER clock, so a drifting kiosk clock
    // can't flag lines as disconnected when snapshots are seconds apart. Belt
    // running = the counter moved since the production's previous snapshot.
    // Alarm: no source, always false. Also returns the line's current
    // production (its latest valid snapshot).
    public async Task<MachineStatusSnapshot?> GetLatestStatusAsync(string machineId)
    {
        await OffUiThread.Switch();
        if (!(await Access.FilterAsync(_connectionFactory))(machineId))
            return null;
        var d = Dialect.For(_connectionFactory.Provider);
        using var connection = _connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<SnapshotRow>(
            $@"SELECT {d.Top(StatusRows)} RTRIM(p.ID_produzione) AS ProductionId, p.progressivo AS Progressivo,
                      p.tempo_fine AS SnapshotTime, p.tempo_inizio AS ProductionStart, COALESCE(p.terminata, 0) AS Terminated,
                      RTRIM(p.codice_articolo) AS ArticleCode, pr.ProductName,
                      COALESCE(p.totale_confezioni, 0)     AS TotalBags,
                      COALESCE(p.confezioni_ok, 0)         AS BagsInTolerance,
                      COALESCE(p.confezioni_sotto_toll, 0) AS BagsUnderTolerance,
                      COALESCE(p.confezioni_sopra_toll, 0) AS BagsOverTolerance,
                      CAST(p.media_totale AS {d.Float})         AS AvgWeight,
                      CAST(p.peso_netto_impostato AS {d.Float}) AS TargetNetWeight,
                      {d.Now} AS ServerNow
               FROM produzione p
               LEFT JOIN Products pr ON pr.Code = RTRIM(p.codice_articolo)
               WHERE p.numero_linea = @machineId AND p.tempo_fine >= {d.NowMinus6Hours}
               ORDER BY p.tempo_fine DESC {d.Limit(StatusRows)}",
            new { machineId = Line(machineId) }))
            // Ties on tempo_fine (duplicate rows happen) are broken here, so SQL
            // Server reads the index backwards instead of sorting the 6 h range.
            .OrderByDescending(r => r.SnapshotTime)
            .ThenByDescending(r => r.Progressivo)
            .ToList();

        if (rows.Count == 0)
            return new MachineStatusSnapshot { MachineId = machineId, IsConnected = false };

        // Never empty in practice: the production with the latest start always keeps a row.
        var valid = rows.Where(r => !IsStray(r, rows)).ToList();
        var latest = valid.FirstOrDefault() ?? rows[0];

        TimeSpan window;
        if (_settings.ConnectedWindowMinutes > 0)
        {
            window = TimeSpan.FromMinutes(_settings.ConnectedWindowMinutes);
        }
        else
        {
            // Duplicate rows share a tempo_fine: skip zero gaps.
            var gap = valid.Zip(valid.Skip(1), (a, b) => a.SnapshotTime - b.SnapshotTime)
                .FirstOrDefault(g => g > TimeSpan.Zero);
            window = gap > TimeSpan.Zero
                ? TimeSpan.FromSeconds(Math.Clamp(2.5 * gap.TotalSeconds, 120, 7200))
                : TimeSpan.FromHours(1);
        }

        var connected = latest.ServerNow - latest.SnapshotTime <= window;
        // Counter moved since the same production's previous snapshot. A
        // production with a single snapshot so far only counts as running if
        // it started within the window (otherwise its bags are old news).
        var previous = valid.FirstOrDefault(r => r.ProductionId == latest.ProductionId && r.SnapshotTime < latest.SnapshotTime);
        var producing = previous != null
            ? latest.TotalBags > previous.TotalBags
            : latest.TotalBags > 0 && latest.ServerNow - latest.Start <= window;

        return new MachineStatusSnapshot
        {
            MachineId = machineId,
            Timestamp = latest.SnapshotTime,
            IsConnected = connected,
            BeltRunning = connected && producing,
            AlarmActive = false,
            CurrentProduction = ToProductionInfo(latest, _settings.FindTare(machineId, latest.TargetNetWeight ?? 0)),
        };
    }

    // StatsViewer glitches: on reconnect it writes one row of a PREVIOUS
    // production of the line with every counter at 0 (tempo_inizio months
    // old, tempo_fine = now), and it has written all-zero rows for current
    // runs too. A zero row is ignored when its production shows bags in
    // another row, or when a production started later exists; a run that
    // really just started (still at 0, latest start) is kept.
    private static bool IsStray(SnapshotRow row, List<SnapshotRow> rows)
        => row.TotalBags == 0
           && rows.Any(o => o.ProductionId == row.ProductionId ? o.TotalBags > 0 : o.Start > row.Start);

    // tare = empty bag weight (Parametres), deducted from the recorded average.
    private static ProductionInfo ToProductionInfo(SnapshotRow row, double tare)
    {
        // ID_produzione = line + article + start: fallback for blank columns.
        var parts = ProductionIdParts.Parse(row.ProductionId);
        var article = string.IsNullOrWhiteSpace(row.ArticleCode) ? parts.ArticleCode : row.ArticleCode;
        var target = row.TargetNetWeight ?? 0;
        return new ProductionInfo
        {
            ProductionId = row.ProductionId,
            ProductName = string.IsNullOrWhiteSpace(row.ProductName) ? ProductionIdParts.DisplayArticle(article) : row.ProductName,
            StartTime = row.Start,
            LastSnapshot = row.SnapshotTime,
            IsTerminated = row.Terminated,
            TotalBags = row.TotalBags,
            BagsInTolerance = row.BagsInTolerance,
            BagsUnderTolerance = row.BagsUnderTolerance,
            BagsOverTolerance = row.BagsOverTolerance,
            TargetNetWeight = target,
            AvgWeight = IsPlausibleRunAverage(row.AvgWeight, target)
                ? row.AvgWeight - tare
                : null,
        };
    }

    // media_totale is a running average since production START. It is only
    // used within 50 % of the target (same bounds as MediaBags in
    // QueryBucketsAsync); otherwise it is thrown away. The run's lifetime
    // min/max are no longer checked: one odd bag in a run of several weeks
    // (CC03: 2946 g on a 1000 g target) hid a sound average on most lines.
    // Known cost: CC01's average (968 g on an 800 g target) passes.
    private static bool IsPlausibleRunAverage(double? avg, double target)
        => avg is > 0 && target > 0 && Math.Abs(avg.Value - target) <= 0.5 * target;

    // Hourly buckets for one machine (dashboard slides, on/off chart, team
    // history). includeWeights = false skips the weight reads (lotto) for the
    // charts that don't show weights.
    public async Task<List<ProductionBucket>> GetLotsAsync(string machineId, DateTime from, DateTime to, bool includeWeights = true)
    {
        await OffUiThread.Switch();
        if (!(await Access.FilterAsync(_connectionFactory))(machineId))
            return new List<ProductionBucket>();
        return await QueryBucketsAsync(new[] { machineId }, BucketSize.Hour, from, to, includeWeights);
    }

    // Buckets for a set of machines (synthesis slides, history, exports). Day
    // buckets are lighter for long ranges but carry no poste (ShiftId 0):
    // callers filtering by poste / team need hour buckets.
    public async Task<List<ProductionBucket>> GetLotsForMachinesAsync(
        IReadOnlyCollection<string> machineIds, DateTime from, DateTime to, BucketSize size = BucketSize.Hour)
    {
        await OffUiThread.Switch();
        var allowed = await Access.FilterAsync(_connectionFactory);
        machineIds = machineIds.Where(allowed).ToList();
        if (machineIds.Count == 0)
            return new List<ProductionBucket>();
        return await QueryBucketsAsync(machineIds.ToList(), size, from, to, includeWeights: true);
    }

    // One production's share of one bucket, as aggregated by the server.
    private sealed class BucketPartRow
    {
        public string MachineId { get; set; } = "";
        public DateTime StartTime { get; set; }
        public string ProductionId { get; set; } = "";
        public string ProductCode { get; set; } = "";
        public string? ProductName { get; set; }
        public double ArticleRate { get; set; }
        public double TargetNetWeight { get; set; }
        public long TotalBags { get; set; }
        public long BagsUnderTolerance { get; set; }
        public long BagsInTolerance { get; set; }
        public long BagsOverTolerance { get; set; }
        public long BagsIndeterminate { get; set; }
        public long MetalRejects { get; set; }
        public double ObservedMinutes { get; set; }
        public double RunMinutes { get; set; }
        public double PlannedStopMinutes { get; set; }
        public double MaxSpeed { get; set; }
        public long SteadyBags { get; set; }
        public double SteadyMinutes { get; set; }
        public long MediaBags { get; set; }
        public double MediaWeightSum { get; set; }
    }

    private static readonly string[] Counters = { "tot", "sotto", "ok", "sopra", "indet", "metal" };

    private const string ByProduction = "PARTITION BY r.numero_linea, r.ID_produzione ORDER BY r.tempo_fine, r.progressivo";

    // CTEs src .. rz: the snapshots of the lines matching lineFilter read in
    // [@readFrom, @readTo), cleaned of StatsViewer's glitch rows.
    private static string CleanSnapshotsSql(string lineFilter, Dialect d) => $@"src AS (
    SELECT p.numero_linea, p.ID_produzione, p.progressivo, p.tempo_inizio, p.tempo_fine,
           -- ID_produzione = line(4) + article(20) + start: fallback for a blank article column.
           COALESCE(NULLIF(RTRIM(p.codice_articolo), ''), SUBSTRING(p.ID_produzione, 5, 20)) AS codice_articolo,
           p.peso_netto_impostato AS target, CAST(p.media_totale AS {d.Float}) AS media,
           COALESCE(p.totale_confezioni, 0)        AS tot,
           COALESCE(p.confezioni_sotto_toll, 0)    AS sotto,
           COALESCE(p.confezioni_ok, 0)            AS ok,
           COALESCE(p.confezioni_sopra_toll, 0)    AS sopra,
           COALESCE(p.confezioni_indeterminate, 0) AS indet,
           COALESCE(p.espulsi_metal, 0)            AS metal
    FROM produzione p
    WHERE {string.Format(lineFilter, "p.numero_linea")}
      AND p.tempo_fine >= @readFrom AND p.tempo_fine < @readTo
),
rng AS (
    -- Stray rows: on reconnect StatsViewer writes one row of a PREVIOUS
    -- production with every counter at 0. Dropped when that production
    -- started before the read window and a later one exists on the line; a
    -- run that really starts at 0 inside the window is kept.
    SELECT s.*
    FROM (SELECT src.*, MAX(src.tempo_inizio) OVER (PARTITION BY src.numero_linea) AS line_last_start FROM src) s
    WHERE CASE WHEN s.tot = 0 AND s.tempo_inizio < @readFrom AND s.tempo_inizio < s.line_last_start THEN 1 ELSE 0 END = 0
),
nz AS (
    SELECT r.*, MAX(r.tot) OVER ({ByProduction} ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS seen_max
    FROM rng r
),
rz AS (
    -- All-zero glitch rows of a production that already showed bags are
    -- dropped, so the next real snapshot is compared with the last real one.
    SELECT * FROM nz WHERE NOT (nz.tot = 0 AND nz.seen_max > 0)
)";

    // Seconds of an interval (ending at second endSecond of its day, lasting
    // duration seconds) inside a planned break. Intervals last at most
    // MaxIntervalSeconds, so each break is checked on the interval's own day
    // and the day before (an interval or a break across midnight).
    private string BreakOverlapSql(string endSecond, string duration)
    {
        static string Min(string a, string b) => $"CASE WHEN {a} < {b} THEN {a} ELSE {b} END";
        static string Max(string a, string b) => $"CASE WHEN {a} > {b} THEN {a} ELSE {b} END";

        var start = $"({endSecond} - {duration})";
        var terms = BreakSchedule.DayWindows(_settings.PlannedBreaks)
            .SelectMany(w => new[] { w, (Start: w.Start - BreakSchedule.SecondsPerDay, End: w.End - BreakSchedule.SecondsPerDay) })
            .Select(w =>
            {
                var overlap = $"{Min(endSecond, w.End.ToString())} - {Max(start, w.Start.ToString())}";
                return $"CASE WHEN {overlap} > 0 THEN {overlap} ELSE 0 END";
            })
            .ToList();
        return terms.Count == 0 ? "0" : "(" + string.Join("\n             + ", terms) + ")";
    }

    // Seconds of an interval (ending endRel seconds after @readFrom, lasting
    // duration seconds) inside a stop planned in the Planification window for
    // its line: maintenance slots and "Planifié" stops. The windows are merged
    // per line and cut out of the daily breaks first (those are in brk), so
    // no second is deducted twice.
    private string PlannedOverlapSql(
        IEnumerable<(string MachineId, DateTime Start, DateTime End)> windows, DateTime readFrom, DateTime readTo,
        string line, string endRel, string duration)
    {
        static string Min(string a, string b) => $"CASE WHEN {a} < {b} THEN {a} ELSE {b} END";
        static string Max(string a, string b) => $"CASE WHEN {a} > {b} THEN {a} ELSE {b} END";

        var breaks = BreakSchedule.DayWindows(_settings.PlannedBreaks);
        var start = $"({endRel} - {duration})";
        var cases = new List<string>();
        foreach (var g in windows.GroupBy(w => w.MachineId.Trim()))
        {
            // Codes go into the SQL text: Machines ids only (CC01, PL03, ...).
            if (g.Key.Length == 0 || !g.Key.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' '))
                continue;
            var merged = new List<(DateTime Start, DateTime End)>();
            foreach (var (_, s, e) in g.OrderBy(w => w.Start))
            {
                var (cs, ce) = (s < readFrom ? readFrom : s, e > readTo ? readTo : e);
                if (ce <= cs)
                    continue;
                if (merged.Count > 0 && cs <= merged[^1].End)
                    merged[^1] = (merged[^1].Start, ce > merged[^1].End ? ce : merged[^1].End);
                else
                    merged.Add((cs, ce));
            }
            var terms = merged
                .SelectMany(w => BreakSchedule.Outside(breaks, w.Start, w.End))
                .Select(w =>
                {
                    var (s, e) = ((long)(w.Start - readFrom).TotalSeconds, (long)(w.End - readFrom).TotalSeconds);
                    var overlap = $"{Min(endRel, e.ToString())} - {Max(start, s.ToString())}";
                    return $"CASE WHEN {overlap} > 0 THEN {overlap} ELSE 0 END";
                })
                .ToList();
            if (terms.Count > 0)
                cases.Add($"WHEN '{g.Key}' THEN (" + string.Join("\n               + ", terms) + ")");
        }
        return cases.Count == 0 ? "0" : $"CASE {line} " + string.Join("\n             ", cases) + " ELSE 0 END";
    }

    // Causes given to stops in the Planification window (Arrets window and sheet).
    public async Task<List<StopQualification>> GetStopQualificationsAsync(DateTime from, DateTime to)
    {
        await OffUiThread.Switch();
        var allowed = await Access.FilterAsync(_connectionFactory);
        return (await _planning.FindQualificationsAsync(from, to)).Where(q => allowed(q.MachineId)).ToList();
    }

    // Maintenance slots and planned stops running now (Planification), whole;
    // none if they can't be read.
    public async Task<List<MaintenanceEvent>> GetActiveSlotsAsync()
    {
        await OffUiThread.Switch();
        try
        {
            var allowed = await Access.FilterAsync(_connectionFactory);
            return (await _planning.FindActiveSlotsAsync(DateTime.Now)).Where(e => allowed(e.MachineId)).ToList();
        }
        catch (Exception ex)
        {
            AppLogger.Log("Créneaux planifiés (Planification) illisibles : " + ex.Message);
            return new();
        }
    }

    // Lines in a maintenance slot or a planned stop right now (Planification),
    // for the run-state photos; a planned stop wins over a maintenance. None
    // if they can't be read, so a slide never fails on them.
    public async Task<Dictionary<string, SlotKind>> GetActiveSlotKindsAsync()
    {
        await OffUiThread.Switch();
        try
        {
            var allowed = await Access.FilterAsync(_connectionFactory);
            return (await _planning.FindActiveSlotsAsync(DateTime.Now))
                .Where(e => allowed(e.MachineId))
                .GroupBy(e => e.MachineId)
                .ToDictionary(g => g.Key, g => g.Max(e => e.Kind));
        }
        catch (Exception ex)
        {
            AppLogger.Log("Créneaux planifiés (Planification) illisibles : " + ex.Message);
            return new();
        }
    }

    // Planned stops of the Planification window over the read range; none
    // (and a log line) if they can't be read, so the KPIs never fail on them.
    private async Task<List<(string MachineId, DateTime Start, DateTime End)>> LoadPlannedStopsAsync(DateTime from, DateTime to)
    {
        try
        {
            return await _planning.GetPlannedStopWindowsAsync(from, to);
        }
        catch (Exception ex)
        {
            AppLogger.Log("Arrêts planifiés (Planification) illisibles, non déduits du TRS : " + ex.Message);
            return new();
        }
    }

    // A snapshot interval longer than this with the counter frozen means
    // StatsViewer wasn't writing (every line at once when the server PC is
    // off): "no data" rather than a machine stop. The cadence is 1-2 min.
    private const int NoDataSeconds = 600;

    // Each line's timeline in [from, to) cut into segments of consecutive
    // snapshot intervals in the same state: producing (counter moved),
    // stopped (counter frozen), no data (frozen across a gap longer than
    // NoDataSeconds) or unplaced (moved across a gap too long to place). Same cleaned snapshots as the KPIs; an interval runs
    // from the line's previous snapshot to this one. Segments that start
    // before from or end after to are returned whole.
    // cancel: a search the page no longer waits for (SQL Server stops the
    // query; SQLite stops between rows).
    public async Task<List<LineSegment>> GetLineSegmentsAsync(IReadOnlyCollection<string> machineIds, DateTime from, DateTime to,
        CancellationToken cancel = default)
    {
        await OffUiThread.Switch();
        if (from < _settings.DataStartDate)
            from = _settings.DataStartDate;
        var allowed = await Access.FilterAsync(_connectionFactory);
        machineIds = machineIds.Where(allowed).ToList();
        if (machineIds.Count == 0 || to <= from)
            return new List<LineSegment>();

        var d = Dialect.For(_connectionFactory.Provider);
        const string lineFilter = "{0} IN (SELECT MachineId FROM Machines WHERE MachineId IN @machineIds)";
        var sql = $@"
WITH {CleanSnapshotsSql(lineFilter, d)},
iv AS (
    SELECT r.numero_linea, r.progressivo, r.tempo_inizio, r.tempo_fine AS t1, r.tot,
           LAG(r.tempo_fine) OVER (PARTITION BY r.numero_linea ORDER BY r.tempo_fine, r.progressivo) AS t0,
           LAG(r.tot) OVER ({ByProduction}) AS prev_tot
    FROM rz r
),
st AS (
    -- Moved: counter up since the production's previous snapshot, a machine
    -- reset (total under half the previous one), or a production started
    -- inside the interval that already shows bags.
    SELECT iv.numero_linea, iv.progressivo, iv.t0, iv.t1,
           CASE WHEN iv.tot > iv.prev_tot
                  OR (iv.tot > 0 AND 2 * iv.tot < iv.prev_tot)
                  OR (iv.prev_tot IS NULL AND iv.tot > 0 AND iv.tempo_inizio >= iv.t0)
                THEN CASE WHEN {d.Seconds("iv.t0", "iv.t1")} > {MaxIntervalSeconds}
                          THEN {(int)LineState.Unplaced} ELSE {(int)LineState.Producing} END
                WHEN {d.Seconds("iv.t0", "iv.t1")} > {NoDataSeconds} THEN {(int)LineState.NoData}
                ELSE {(int)LineState.Stopped} END AS state
    FROM iv
    WHERE iv.t0 IS NOT NULL AND iv.t1 > iv.t0
),
isl AS (
    SELECT st.*,
           ROW_NUMBER() OVER (PARTITION BY st.numero_linea ORDER BY st.t1, st.progressivo)
           - ROW_NUMBER() OVER (PARTITION BY st.numero_linea, st.state ORDER BY st.t1, st.progressivo) AS grp
    FROM st
)
SELECT isl.numero_linea AS MachineId, isl.state AS State, MIN(isl.t0) AS StartTime, MAX(isl.t1) AS EndTime
FROM isl
GROUP BY isl.numero_linea, isl.state, isl.grp
HAVING MAX(isl.t1) > @from AND MIN(isl.t0) < @to";

        var parameters = new DynamicParameters();
        parameters.Add("machineIds", machineIds.ToList());
        parameters.Add("from", from);
        parameters.Add("to", to);
        parameters.Add("readFrom", from - SnapshotMargin < _settings.DataStartDate ? _settings.DataStartDate : from - SnapshotMargin);
        parameters.Add("readTo", to + SnapshotMargin);

        using var connection = _connectionFactory.CreateConnection();
        var segments = (await connection.QueryAsync<LineSegment>(new CommandDefinition(sql, parameters, cancellationToken: cancel))).ToList();
        cancel.ThrowIfCancellationRequested();
        foreach (var s in segments)
            s.MachineId = s.MachineId.TrimEnd();

        // A line StatsViewer no longer reaches (switched off for a panne:
        // PL01 since 27/10) writes no snapshot, so the query has nothing past
        // its last one: no data from there to the end of the range (or now),
        // so the stop shows while it lasts, not only once the line is back.
        // A line that never wrote a snapshot since DataStartDate gets nothing.
        var until = to < DateTime.Now ? to : DateTime.Now;
        var lastSnapshots = (await connection.QueryAsync<(string MachineId, DateTime? Last)>(new CommandDefinition($@"
SELECT m.MachineId,
       (SELECT MAX(p.tempo_fine) FROM produzione p
        WHERE p.numero_linea = m.MachineId AND p.tempo_fine >= @dataStart AND p.tempo_fine < @until) AS Last
FROM Machines m WHERE m.MachineId IN @machineIds",
            new { machineIds = machineIds.ToList(), dataStart = _settings.DataStartDate, until }, cancellationToken: cancel)))
            .ToList();
        foreach (var (machineId, last) in lastSnapshots)
        {
            if (last is not { } lastSnapshot)
                continue;
            var id = machineId.TrimEnd();
            var lastEnd = segments.Where(s => s.MachineId == id).Select(s => s.EndTime).DefaultIfEmpty(lastSnapshot).Max();
            if (lastEnd < lastSnapshot)
                lastEnd = lastSnapshot;
            if ((until - lastEnd).TotalSeconds > NoDataSeconds && until > from)
                segments.Add(new LineSegment { MachineId = id, State = LineState.NoData, StartTime = lastEnd, EndTime = until });
        }
        return segments.OrderBy(s => s.MachineId).ThenBy(s => s.StartTime).ToList();
    }

    // Ranges over a day are read one calendar day at a time: SQL Server's
    // cost grows faster than the range (the month Cumul of 10 lines passed
    // the 60 s timeout on 29/10 while each day takes 2-4 s). Buckets never
    // cross midnight and each day reads its own SnapshotMargin, so the result
    // is the same. The days are read ParallelDays at a time, each on its own
    // connection (a month was 30 queries one after the other).
    private const int ParallelDays = 4;

    private async Task<List<ProductionBucket>> QueryBucketsAsync(
        IReadOnlyList<string> machines, BucketSize size, DateTime from, DateTime to, bool includeWeights)
    {
        if (from < _settings.DataStartDate)
            from = _settings.DataStartDate;
        if (to - from <= TimeSpan.FromDays(1))
            return await QueryDayAsync(machines, size, from, to, includeWeights);
        var days = new List<(DateTime Start, DateTime End)>();
        for (var start = from; start < to;)
        {
            var end = start.Date.AddDays(1) < to ? start.Date.AddDays(1) : to;
            days.Add((start, end));
            start = end;
        }
        using var gate = new SemaphoreSlim(ParallelDays);
        var reads = days.Select(async day =>
        {
            await gate.WaitAsync();
            try
            {
                // A first day started on the hour (a poste minus its early
                // margin) is read whole, so it comes from the day cache like
                // the others, and cut: an hour bucket holds the intervals
                // whose midpoint falls in it, so the result is the same.
                var whole = size == BucketSize.Hour && day.Start != day.Start.Date
                            && day.Start.Minute == 0 && day.Start.Second == 0 && day.Start.Millisecond == 0
                            && day.End == day.Start.Date.AddDays(1) && day.Start.Date >= _settings.DataStartDate;
                if (!whole)
                    return await QueryDayAsync(machines, size, day.Start, day.End, includeWeights);
                var all = await QueryDayAsync(machines, size, day.Start.Date, day.End, includeWeights);
                return all.Where(b => b.StartTime >= day.Start).ToList();
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        var buckets = new List<ProductionBucket>();
        foreach (var day in await Task.WhenAll(reads))
            buckets.AddRange(day);
        return buckets;
    }

    // Closed days, kept for DayCacheFor: Mois / 3 mois / Année, the history
    // and the exports read the same past days again and again (a year was
    // 365 queries of 2-4 s on SQLEXPRESS). A day is closed once its
    // SnapshotMargin has passed too, as its last intervals are read up to
    // then. Planned stops given afterwards in Planification show within
    // DayCacheFor, like the synthesis cache. Buckets are shared, never
    // changed by their readers. Past this memory, closed days are also kept
    // in the application database for every PC (StoredDayAsync).
    private static readonly TimeSpan DayCacheFor = TimeSpan.FromMinutes(30);
    private const int DayCacheMax = 20000;
    private static readonly ConcurrentDictionary<string, (DateTime ReadAt, List<ProductionBucket> Buckets)> DayCache = new();

    // Settings changed (nominal rates, planned breaks, another base): read again.
    public static void ClearDayCache() => DayCache.Clear();

    private async Task<List<ProductionBucket>> QueryDayAsync(
        IReadOnlyList<string> machines, BucketSize size, DateTime from, DateTime to, bool includeWeights)
    {
        var closed = from == from.Date && to == from.AddDays(1) && to + SnapshotMargin <= DateTime.Now;
        if (!closed)
            return BuildBuckets(await QueryRawAsync(machines, size, from, to, includeWeights), size);

        var key = string.Join("|", _connectionFactory.Provider, size, from.ToString("yyyyMMdd"), includeWeights,
            string.Join(",", machines.Select(m => m.Trim().ToUpperInvariant()).Order(StringComparer.Ordinal)));
        var now = DateTime.UtcNow;
        if (DayCache.TryGetValue(key, out var hit) && now - hit.ReadAt < DayCacheFor)
            return new List<ProductionBucket>(hit.Buckets);

        var buckets = BuildBuckets(await StoredDayAsync(machines, size, from, includeWeights)
                                   ?? await QueryRawAsync(machines, size, from, to, includeWeights), size);
        if (DayCache.Count >= DayCacheMax)
        {
            foreach (var old in DayCache.Where(e => now - e.Value.ReadAt >= DayCacheFor).Select(e => e.Key).ToList())
                DayCache.TryRemove(old, out _);
            if (DayCache.Count >= DayCacheMax)
                DayCache.Clear();
        }
        DayCache[key] = (now, buckets);
        return new List<ProductionBucket>(buckets);
    }

    // SQL condition on the line code of these machines ({0} = the column or
    // expression holding it) and its parameters. Several are compared
    // through Machines (small table), so the produzione side still seeks its
    // index instead of converting numero_linea.
    private static (string Filter, object Params) LineFilter(IReadOnlyList<string> machines)
        => machines.Count == 1
            ? ("{0} = @machineId", new { machineId = Line(machines[0]) })
            : ("{0} IN (SELECT MachineId FROM Machines WHERE MachineId IN @machineIds)", new { machineIds = machines.ToList() });

    // What the bucket query returned for a range: its rows and, with weights,
    // the lots (target found, empty bag weight not deducted yet). The
    // settings of Paramètres (nominal rates, empty bag weights) are applied
    // after (BuildBuckets), so a stored day follows them.
    private sealed class RawBuckets
    {
        public bool Weights { get; set; }
        public List<BucketPartRow> Parts { get; set; } = new();
        public List<LotRow> Lots { get; set; } = new();
    }

    private async Task<RawBuckets> QueryRawAsync(
        IReadOnlyList<string> machines, BucketSize size, DateTime from, DateTime to, bool includeWeights)
    {
        var raw = new RawBuckets { Weights = includeWeights };
        // Nothing before DataStartDate is read, not even as the baseline of the
        // first interval: that interval is then dropped (unknown baseline).
        var dataStart = _settings.DataStartDate;
        if (from < dataStart)
            from = dataStart;
        if (to <= from || machines.Count == 0)
            return raw;
        var (lineFilter, filterParams) = LineFilter(machines);

        var d = Dialect.For(_connectionFactory.Provider);
        var bucket = size == BucketSize.Hour ? d.Hour("x.mid_t") : d.Day("x.mid_t");
        const string byProduction = ByProduction;

        var previous = string.Join(",\n", Counters.Select(c =>
            $"           LAG(r.{c}) OVER ({byProduction}) AS prev_{c}"));
        var deltas = string.Join(",\n", Counters.Select(c =>
            $"           g.known * CASE WHEN g.prev_tot IS NULL OR g.restart = 1 THEN g.{c}"
            + $" WHEN g.{c} > g.prev_{c} THEN g.{c} - g.prev_{c} ELSE 0 END AS d_{c}"));
        var saneDeltas = string.Join(",\n", Counters.Select(c => $"           sc.sane * sc.d_{c} AS d_{c}"));

        var readFrom = from - SnapshotMargin < dataStart ? dataStart : from - SnapshotMargin;
        var readTo = to + SnapshotMargin;
        var planned = PlannedOverlapSql(await LoadPlannedStopsAsync(readFrom, readTo), readFrom, readTo,
            "sc.numero_linea", "sc.e_rel", "sc.dur");

        var sql = $@"
WITH {CleanSnapshotsSql(lineFilter, d)},
win AS (
    -- Baseline of each counter = the production's previous snapshot (ordered
    -- by tempo_fine, then progressivo: duplicate rows happen). A counter that
    -- steps back contributes 0 and counting resumes from the new value: PL03
    -- once fell 3 375 bags (1 710 281 -> 1 706 906) after a StatsViewer
    -- reconnect, then the machine kept counting up from 1 706 906.
    SELECT r.*,
{previous},
           -- Interval = since the LINE's previous snapshot, whatever its
           -- production: one timeline per line, so observed time is never
           -- counted twice when two productions overlap.
           LAG(r.tempo_fine) OVER (PARTITION BY r.numero_linea ORDER BY r.tempo_fine, r.progressivo) AS line_prev_fine
    FROM rz r
),
flg AS (
    SELECT w.*,
           -- No earlier snapshot read: fine if the production started inside the
           -- read window (its counters ARE the delta), unknown baseline otherwise.
           CASE WHEN w.prev_tot IS NULL AND (w.tempo_inizio IS NULL OR w.tempo_inizio < @readFrom) THEN 0 ELSE 1 END AS known,
           -- Machine reset = the total falls under half the previous one: the new
           -- values are the delta. A smaller step back is a glitch (counts 0).
           CASE WHEN 2 * w.tot < w.prev_tot THEN 1 ELSE 0 END AS restart,
           COALESCE(w.line_prev_fine,
                    CASE WHEN w.prev_tot IS NULL AND w.tempo_inizio >= @readFrom THEN w.tempo_inizio END) AS start_t
    FROM win w
),
g AS (
    SELECT f.*,
           CASE WHEN f.start_t IS NOT NULL AND f.tempo_fine > f.start_t THEN {d.Seconds("f.start_t", "f.tempo_fine")} ELSE 0 END AS gap
    FROM flg f
),
dl AS (
    SELECT g.numero_linea, g.ID_produzione, g.progressivo, g.codice_articolo, g.target, g.media, g.tempo_fine, g.gap,
           {d.SecondOfDay("g.tempo_fine")} AS e_sec,
           {(planned == "0" ? "0" : d.Seconds("@readFrom", "g.tempo_fine"))} AS e_rel,
           -- Observed time: 0 without a known baseline or past MaxIntervalSeconds.
           g.known * CASE WHEN g.gap > {MaxIntervalSeconds} THEN 0 ELSE g.gap END AS dur,
{deltas}
    FROM g
),
sc AS (
    -- Interval too long to place in time (MaxIntervalSeconds), or faster than
    -- MaxBagsPerMinute over its real elapsed time (at least a minute): dropped.
    SELECT dl.*,
           CASE WHEN dl.gap > {MaxIntervalSeconds} THEN 0
                WHEN CAST(dl.d_tot AS {d.Float}) * 60
                     > {MaxBagsPerMinute} * CAST(CASE WHEN dl.gap < 60 THEN 60 ELSE dl.gap END AS {d.Float})
                THEN 0 ELSE 1 END AS sane
    FROM dl
),
nb AS (
    -- Bags of the line's previous and next intervals, whatever their production.
    SELECT sc.*,
           LAG(sc.sane * sc.d_tot) OVER (PARTITION BY sc.numero_linea ORDER BY sc.tempo_fine, sc.progressivo) AS prev_d,
           LEAD(sc.sane * sc.d_tot) OVER (PARTITION BY sc.numero_linea ORDER BY sc.tempo_fine, sc.progressivo) AS next_d,
           -- Bags, seconds and sanity of the window ending with this interval.
           SUM(sc.sane * sc.d_tot) OVER (PARTITION BY sc.numero_linea ORDER BY sc.tempo_fine, sc.progressivo
               ROWS BETWEEN {MaxSpeedWindowIntervals - 1} PRECEDING AND CURRENT ROW) AS win_d,
           SUM(sc.dur) OVER (PARTITION BY sc.numero_linea ORDER BY sc.tempo_fine, sc.progressivo
               ROWS BETWEEN {MaxSpeedWindowIntervals - 1} PRECEDING AND CURRENT ROW) AS win_dur,
           MIN(sc.sane) OVER (PARTITION BY sc.numero_linea ORDER BY sc.tempo_fine, sc.progressivo
               ROWS BETWEEN {MaxSpeedWindowIntervals - 1} PRECEDING AND CURRENT ROW) AS win_sane
    FROM sc
),
x AS (
    -- Attributed to the bucket containing the midpoint of the interval.
    SELECT sc.numero_linea, sc.ID_produzione, sc.codice_articolo, sc.target, sc.media, sc.dur,
           {d.MinusSeconds("sc.tempo_fine", "sc.dur / 2")} AS mid_t,
           -- Window speed (bags/min), for the max speed of the bucket.
           CASE WHEN sc.win_sane = 1 AND sc.win_dur >= {MinSpeedWindowSeconds}
                THEN CAST(sc.win_d AS {d.Float}) * 60 / sc.win_dur ELSE 0 END AS spd,
           -- Steady running (average speed): the line produced in this interval
           -- and in the ones before and after it, so the interval in which it
           -- stops or restarts (partly stopped) is left out.
           CASE WHEN sc.sane = 1 AND sc.d_tot > 0 AND sc.prev_d > 0 AND sc.next_d > 0
                     AND sc.dur > 0 AND sc.dur <= {MaxSteadyIntervalSeconds}
                THEN 1 ELSE 0 END AS stdy,
           -- Seconds of the interval inside a planned break.
           {BreakOverlapSql("sc.e_sec", "sc.dur")} AS brk,
           -- Seconds of the interval inside a stop planned in Planification.
           {planned} AS pln,
{saneDeltas}
    FROM nb sc
)
SELECT a.*, pr.ProductName, CAST(COALESCE(pr.NominalRatePerMinute, 0) AS {d.Float}) AS ArticleRate
FROM (
    -- One row per line, bucket, production AND target weight: rate and
    -- weight figures must use each bag's own target. The target can change
    -- inside a production (operator sets 400 g instead of 500 g without
    -- opening a new one, counters keep running): each interval goes to the
    -- target of the snapshot that ends it.
    SELECT x.numero_linea                       AS MachineId,
           {bucket}                             AS StartTime,
           RTRIM(x.ID_produzione)               AS ProductionId,
           RTRIM(MAX(x.codice_articolo))        AS ProductCode,
           CAST(COALESCE(MAX(x.target), 0) AS {d.Float}) AS TargetNetWeight,
           CAST(SUM(x.d_tot)   AS BIGINT)       AS TotalBags,
           CAST(SUM(x.d_sotto) AS BIGINT)       AS BagsUnderTolerance,
           CAST(SUM(x.d_ok)    AS BIGINT)       AS BagsInTolerance,
           CAST(SUM(x.d_sopra) AS BIGINT)       AS BagsOverTolerance,
           CAST(SUM(x.d_indet) AS BIGINT)       AS BagsIndeterminate,
           CAST(SUM(x.d_metal) AS BIGINT)       AS MetalRejects,
           CAST(SUM(x.dur) AS {d.Float}) / 60   AS ObservedMinutes,
           CAST(SUM(CASE WHEN x.d_tot > 0 THEN x.dur ELSE 0 END) AS {d.Float}) / 60 AS RunMinutes,
           -- Only stopped time is planned: a line that runs through a break
           -- or a maintenance slot keeps that time as run time.
           CAST(SUM(CASE WHEN x.d_tot > 0 THEN 0 WHEN x.brk + x.pln > x.dur THEN x.dur ELSE x.brk + x.pln END) AS {d.Float}) / 60 AS PlannedStopMinutes,
           CAST(MAX(x.spd) AS {d.Float})        AS MaxSpeed,
           CAST(SUM(x.stdy * x.d_tot) AS BIGINT) AS SteadyBags,
           CAST(SUM(x.stdy * x.dur) AS {d.Float}) / 60 AS SteadyMinutes,
           -- Fallback weight data (used only when the line has no lot): bags
           -- whose running average is within 50 % of the target.
           CAST(SUM(CASE WHEN x.target > 0 AND x.media >= 0.5 * x.target AND x.media <= 1.5 * x.target
                         THEN x.d_tot ELSE 0 END) AS BIGINT) AS MediaBags,
           CAST(SUM(CASE WHEN x.target > 0 AND x.media >= 0.5 * x.target AND x.media <= 1.5 * x.target
                         THEN x.media * x.d_tot ELSE 0 END) AS {d.Float}) AS MediaWeightSum
    FROM x
    WHERE x.mid_t >= @from AND x.mid_t < @to
    GROUP BY x.numero_linea, {bucket}, x.ID_produzione, x.target
) a
LEFT JOIN Products pr ON pr.Code = a.ProductCode";

        var parameters = new DynamicParameters(filterParams);
        parameters.Add("from", from);
        parameters.Add("to", to);
        parameters.Add("readFrom", readFrom);
        parameters.Add("readTo", readTo);

        using var connection = _connectionFactory.CreateConnection();
        raw.Parts = (await connection.QueryAsync<BucketPartRow>(sql, parameters)).ToList();
        foreach (var p in raw.Parts)
            // CHAR(4) numero_linea comes back padded if a code is ever shorter.
            p.MachineId = p.MachineId.TrimEnd();
        if (includeWeights)
            raw.Lots = await LoadLotsAsync(connection, d, lineFilter, size, parameters, raw.Parts);
        return raw;
    }

    private List<ProductionBucket> BuildBuckets(RawBuckets raw, BucketSize size)
    {
        var parts = raw.Parts;
        var weights = raw.Weights
            ? WeightParts(raw)
            : new List<(string MachineId, DateTime StartTime, WeightPart Part)>();

        var length = size == BucketSize.Hour ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        var partsByKey = parts.ToDictionary(p => (p.MachineId, p.StartTime, p.ProductionId, TargetNetWeight: p.TargetNetWeight));
        var weightsByKey = weights.ToLookup(w => (w.MachineId, w.StartTime, w.Part.ProductionId, TargetNetWeight: w.Part.Target), w => w.Part);
        var keys = partsByKey.Keys
            .Union(weightsByKey.Select(g => g.Key))
            .OrderBy(k => k.StartTime)
            .ThenBy(k => k.MachineId)
            .ThenBy(k => k.ProductionId)
            .ThenBy(k => k.TargetNetWeight);

        var buckets = new List<ProductionBucket>();
        foreach (var key in keys)
        {
            var weightParts = weightsByKey[key].ToList();
            // A lot can close in a bucket where its production's counters
            // didn't move: weights only, no counters.
            var p = partsByKey.GetValueOrDefault(key) ?? new BucketPartRow
            {
                MachineId = key.MachineId,
                StartTime = key.StartTime,
                ProductionId = key.ProductionId,
                ProductCode = ProductionIdParts.Parse(key.ProductionId).ArticleCode,
                TargetNetWeight = key.TargetNetWeight,
            };
            var b = new ProductionBucket
            {
                MachineId = key.MachineId,
                ProductionId = key.ProductionId,
                StartTime = key.StartTime,
                EndTime = key.StartTime + length,
                // Day buckets span all three postes: no poste.
                ShiftId = size == BucketSize.Hour ? KpiCalculator.CurrentShiftId(key.StartTime) : 0,
                ProductCode = p.ProductCode,
                // Products row missing or unnamed: show the article code without
                // its zero padding (00000000NOUVELLEPROD -> NOUVELLEPROD).
                ProductName = string.IsNullOrWhiteSpace(p.ProductName) ? ProductionIdParts.DisplayArticle(p.ProductCode) : p.ProductName,
                TargetNetWeight = p.TargetNetWeight,
                TotalBags = p.TotalBags,
                BagsUnderTolerance = p.BagsUnderTolerance,
                BagsInTolerance = p.BagsInTolerance,
                BagsOverTolerance = p.BagsOverTolerance,
                BagsIndeterminate = p.BagsIndeterminate,
                MetalRejects = p.MetalRejects,
                ObservedMinutes = p.ObservedMinutes,
                RunMinutes = p.RunMinutes,
                PlannedStopMinutes = p.PlannedStopMinutes,
                MaxSpeedBagsPerMinute = p.MaxSpeed,
                SteadyBags = p.SteadyBags,
                SteadyMinutes = p.SteadyMinutes,
            };

            // Performance inputs at the production's own nominal rate: a
            // per-article rate in Products wins, otherwise the machine/weight
            // table from Parametres. Unknown rate (0) = left out of both sides.
            var rate = p.ArticleRate > 0 ? p.ArticleRate : _settings.FindNominalRate(p.MachineId, p.TargetNetWeight);
            b.NominalRate = rate > 0 ? rate : 0;
            if (p.TotalBags > 0 && rate > 0)
            {
                b.IdealMinutes = p.TotalBags / rate;
                b.RatedRunMinutes = p.RunMinutes;
            }

            b.WeightParts.AddRange(weightParts);
            buckets.Add(b);
        }
        return buckets;
    }

    // Stiavelli lots of one line overlapping [from, to), each with its own
    // time span (ora_inizio -> ora_fine), for the weight-over-time chart.
    // Net averages (empty bag deducted, as WeightParts). Target =
    // peso_netto_impostato of the production's first snapshot at or after
    // the lot end, else its last one before (lot closed after the latest
    // snapshot). null = no lotto table (SQLite dev DB): caller falls back.
    public async Task<List<LotSpan>?> GetLotSpansAsync(string machineId, DateTime from, DateTime to)
    {
        await OffUiThread.Switch();
        if (!(await Access.FilterAsync(_connectionFactory))(machineId))
            return new List<LotSpan>();
        var d = Dialect.For(_connectionFactory.Provider);
        using var connection = _connectionFactory.CreateConnection();
        if (!await HasLotTableAsync(connection, d))
            return null;

        // The dev SQLite lotto has no ora_inizio (Stiavelli's has): hourly chart.
        var hasStart = await connection.ExecuteScalarAsync<int>(_connectionFactory.Provider == DatabaseProvider.SqlServer
            ? "SELECT CASE WHEN COL_LENGTH('lotto', 'ora_inizio') IS NULL THEN 0 ELSE 1 END"
            : "SELECT COUNT(*) FROM pragma_table_info('lotto') WHERE name = 'ora_inizio'");
        if (hasStart == 0)
            return null;

        const string target = "CAST(p.peso_netto_impostato AS {0})";
        var rows = await connection.QueryAsync<LotSpan>($@"
SELECT RTRIM(l.ID_produzione) AS ProductionId, l.ora_inizio AS StartTime, l.ora_fine AS EndTime,
       CAST(l.dimensione AS BIGINT) AS Bags, CAST(l.media_effettiva AS {d.Float}) AS Average,
       COALESCE(
           (SELECT {d.Top(1)} {string.Format(target, d.Float)} FROM produzione p
            WHERE p.numero_linea = @machineId AND p.ID_produzione = l.ID_produzione AND p.tempo_fine >= l.ora_fine
            ORDER BY p.tempo_fine {d.Limit(1)}),
           (SELECT {d.Top(1)} {string.Format(target, d.Float)} FROM produzione p
            WHERE p.numero_linea = @machineId AND p.ID_produzione = l.ID_produzione AND p.tempo_fine < l.ora_fine
            ORDER BY p.tempo_fine DESC {d.Limit(1)}), 0) AS Target
FROM lotto l
WHERE l.ora_fine > @from AND l.ora_fine < @readTo AND l.ora_inizio < @to
  AND SUBSTRING(l.ID_produzione, 1, 4) = @machineId
  AND l.dimensione > 0 AND l.media_effettiva > 0 AND l.ora_inizio IS NOT NULL
ORDER BY l.ora_fine",
            // A lot ends within a day of its start (longest seen: 21 h):
            // bounds ora_fine for IX_lotto_ora_fine.
            new { machineId = Line(machineId), from, to, readTo = to.AddDays(1) });

        var lots = rows.Where(l => l.Target > 0 && l.EndTime > l.StartTime).ToList();
        foreach (var lot in lots)
            lot.Average -= _settings.FindTare(machineId, lot.Target);
        return lots;
    }

    private sealed class LotRow
    {
        public string MachineId { get; set; } = "";
        public string ProductionId { get; set; } = "";
        public DateTime StartTime { get; set; }
        public double? Target { get; set; }
        public long Bags { get; set; }
        public double WeightSum { get; set; }
    }

    // Weight data per (line, bucket, production):
    //  - Stiavelli lots (lotto): media_effettiva weighted by dimensione,
    //    bucketed by ora_fine (lots can be uploaded late, ora_fine is the real
    //    lot time), for every line that has lots in the range;
    //  - otherwise media_totale, only where it is within 50 % of the target
    //    (MediaBags).
    // A lot's target is the peso_netto_impostato of its production's first
    // snapshot at or after the lot (the target can change inside a
    // production); lots of a production with no snapshot in the read range
    // are left out. Line = first 4 characters of ID_produzione (odd codes
    // like "PL 3" match no machine).
    // Read here: the lots, each with its target. The empty bag weight set in
    // Parametres for the line and target is deducted after, in WeightParts
    // (Stiavelli weighs the full bag).
    private async Task<List<LotRow>> LoadLotsAsync(
        IDbConnection connection, Dialect d, string lineFilter, BucketSize size, DynamicParameters parameters, List<BucketPartRow> parts)
    {
        var result = new List<LotRow>();
        var targets = parts.Where(p => p.TargetNetWeight > 0)
            .GroupBy(p => p.ProductionId)
            .ToDictionary(g => g.Key, g => g.Max(p => p.TargetNetWeight));

        if (await HasLotTableAsync(connection, d))
        {
            const string lineOfLot = "SUBSTRING(l.ID_produzione, 1, 4)";
            var bucket = size == BucketSize.Hour ? d.Hour("l.ora_fine") : d.Day("l.ora_fine");
            // Target subquery seeks IX_produzione_linea_fine (line + time).
            var lots = await connection.QueryAsync<LotRow>($@"
SELECT t.MachineId, t.ProductionId, t.StartTime, t.Target,
       CAST(SUM(t.dimensione) AS BIGINT) AS Bags,
       CAST(SUM(t.media * t.dimensione) AS {d.Float}) AS WeightSum
FROM (
    SELECT {lineOfLot} AS MachineId, RTRIM(l.ID_produzione) AS ProductionId, {bucket} AS StartTime,
           l.dimensione, CAST(l.media_effettiva AS {d.Float}) AS media,
           (SELECT {d.Top(1)} CAST(p.peso_netto_impostato AS {d.Float}) FROM produzione p
            WHERE p.numero_linea = {lineOfLot} AND p.ID_produzione = l.ID_produzione
              AND p.tempo_fine >= l.ora_fine AND p.tempo_fine < @readTo
            ORDER BY p.tempo_fine {d.Limit(1)}) AS Target
    FROM lotto l
    WHERE l.ora_fine >= @from AND l.ora_fine < @to
      AND l.dimensione > 0 AND l.media_effettiva > 0
      AND {string.Format(lineFilter, lineOfLot)}
) t
GROUP BY t.MachineId, t.ProductionId, t.StartTime, t.Target", parameters);

            foreach (var lot in lots)
            {
                var machineId = lot.MachineId.TrimEnd();
                // No later snapshot read: the production's own target.
                if (!targets.TryGetValue(lot.ProductionId, out var productionTarget))
                    continue;
                lot.MachineId = machineId;
                lot.Target = lot.Target is > 0 ? lot.Target.Value : productionTarget;
                result.Add(lot);
            }
        }
        return result;
    }

    // The weight data of the buckets, empty bag weight (Parametres) deducted:
    // the lots, or the sane running averages on the lines with none.
    private List<(string MachineId, DateTime StartTime, WeightPart Part)> WeightParts(RawBuckets raw)
    {
        var result = new List<(string MachineId, DateTime StartTime, WeightPart Part)>();
        var linesWithLots = new HashSet<string>();
        foreach (var lot in raw.Lots)
        {
            var target = lot.Target ?? 0;
            linesWithLots.Add(lot.MachineId);
            var tare = _settings.FindTare(lot.MachineId, target);
            result.Add((lot.MachineId, lot.StartTime, new WeightPart(lot.ProductionId, target, lot.Bags, lot.WeightSum - tare * lot.Bags)));
        }

        var parts = raw.Parts;
        var fallback = parts.Where(p => p.MediaBags > 0 && !linesWithLots.Contains(p.MachineId)).ToList();
        if (fallback.Count == 0)
            return result;

        foreach (var p in fallback)
        {
            var tare = _settings.FindTare(p.MachineId, p.TargetNetWeight);
            result.Add((p.MachineId, p.StartTime,
                new WeightPart(p.ProductionId, p.TargetNetWeight, p.MediaBags, p.MediaWeightSum - tare * p.MediaBags)));
        }
        return result;
    }

    // Checked once per connection setup (ResetConnections clears it): the
    // SQLite dev DB has no lotto, and a login without rights on it sees none.
    private async Task<bool> HasLotTableAsync(IDbConnection connection, Dialect d)
    {
        if (_hasLotTable is { } known)
            return known;
        var exists = await connection.ExecuteScalarAsync<int>(d.LotTableExists) > 0;
        if (!exists)
            AppLogger.Log("Table lotto introuvable : poids calculés depuis media_totale (filtre).");
        _hasLotTable = exists;
        return exists;
    }

    // numero_linea is CHAR(4): send the id as varchar so SQL Server can seek
    // the index instead of converting every row to nvarchar.
    private static DbString Line(string machineId) => new() { Value = machineId, IsAnsi = true, Length = 10 };

    private sealed record Dialect(
        Func<string, string, string> Seconds,
        Func<string, string, string> MinusSeconds,
        Func<string, string> Hour,
        Func<string, string> Day,
        Func<string, string> SecondOfDay,
        string Float,
        string Now,
        string NowMinus6Hours,
        Func<int, string> Top,
        Func<int, string> Limit,
        string LotTableExists)
    {
        private static readonly Dialect SqlServer = new(
            (a, b) => $"DATEDIFF(SECOND, {a}, {b})",
            (x, s) => $"DATEADD(SECOND, -({s}), {x})",
            x => $"DATEADD(HOUR, DATEDIFF(HOUR, 0, {x}), 0)",
            x => $"DATEADD(DAY, DATEDIFF(DAY, 0, {x}), 0)",
            x => $"DATEDIFF(SECOND, DATEADD(DAY, DATEDIFF(DAY, 0, {x}), 0), {x})",
            "FLOAT",
            "GETDATE()",
            "DATEADD(HOUR, -6, GETDATE())",
            n => $"TOP {n}",
            _ => "",
            "SELECT CASE WHEN OBJECT_ID('lotto') IS NULL THEN 0 ELSE 1 END");

        // Dev DB: timestamps stored as 'yyyy-MM-dd HH:mm:ss' text.
        private static readonly Dialect Sqlite = new(
            (a, b) => $"CAST(ROUND((julianday({b}) - julianday({a})) * 86400) AS INTEGER)",
            (x, s) => $"datetime(julianday({x}) - ({s}) / 86400.0)",
            x => $"strftime('%Y-%m-%d %H:00:00', {x})",
            x => $"strftime('%Y-%m-%d 00:00:00', {x})",
            // Wall-clock text read as UTC: modulo a day gives its time of day.
            x => $"(CAST(strftime('%s', {x}) AS INTEGER) % 86400)",
            "REAL",
            "datetime('now', 'localtime')",
            "datetime('now', 'localtime', '-6 hours')",
            _ => "",
            n => $"LIMIT {n}",
            "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table', 'view') AND name = 'lotto'");

        public static Dialect For(DatabaseProvider provider)
            => provider == DatabaseProvider.SqlServer ? SqlServer : Sqlite;
    }

    // ---- Application data: sticky note, machine names ----
    // In spiga_suivi; in stats, in their 2.0 shape, while PCs still run 2.0
    // (PlantDataLocation). The audit trail is always in spiga_suivi.

    // Throws when the database cannot be read: the caller keeps what it shows.
    public async Task<NoteEntry?> GetActiveNoteAsync()
    {
        await OffUiThread.Switch();
        if (await _location.ModeAsync() != PlantDataMode.Application)
        {
            using var stats = _connectionFactory.CreateConnection();
            return await stats.QueryFirstOrDefaultAsync<NoteEntry>(
                @"SELECT NoteId, Content, CreatedAt, IsActive FROM Notes
                  WHERE IsActive = 1 ORDER BY CreatedAt DESC, NoteId DESC");
        }
        using var connection = _appData.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<NoteEntry>(
            @"SELECT NoteId, Content, CreatedBy, CreatedByName, Workstation, CreatedAt, IsActive FROM Notes
              WHERE IsActive = 1 ORDER BY CreatedAt DESC, NoteId DESC");
    }

    // The new note replaces the previous one; who wrote it, and the change,
    // go to the audit trail (in the same transaction once in spiga_suivi).
    public async Task SetActiveNoteAsync(string content, ChangeAuthor author)
    {
        await OffUiThread.Switch();
        var target = await _location.WriteTargetAsync();
        var legacy = !ReferenceEquals(target, _appData);
        string? previous;
        using (var connection = target.CreateConnection())
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            previous = await connection.QueryFirstOrDefaultAsync<string?>(
                "SELECT Content FROM Notes WHERE IsActive = 1 ORDER BY CreatedAt DESC, NoteId DESC", transaction: transaction);
            if (previous == content)
                return;
            await connection.ExecuteAsync("UPDATE Notes SET IsActive = 0 WHERE IsActive = 1", transaction: transaction);
            if (legacy)
            {
                // 2.0 shape: no author column (it is in the audit trail).
                var employeeId = await connection.ExecuteScalarAsync<int?>("SELECT MIN(EmployeeId) FROM Employees", transaction: transaction) ?? 1;
                await connection.ExecuteAsync(
                    "INSERT INTO Notes (Content, CreatedByEmployeeId, CreatedAt, IsActive) VALUES (@content, @employeeId, @createdAt, 1)",
                    new { content, employeeId, createdAt = DateTime.Now }, transaction);
            }
            else
            {
                await connection.ExecuteAsync(
                    @"INSERT INTO Notes (Content, CreatedBy, CreatedByName, Workstation, CreatedAt, IsActive)
                      VALUES (@content, @login, @name, @workstation, @createdAt, 1)",
                    new { content, login = author.Login, name = author.Name, workstation = author.Workstation, createdAt = DateTime.Now },
                    transaction);
                await AuditRows.WriteAsync(connection, transaction, author, AuditRows.ModuleConditionnement, AuditRows.NoteUpdated,
                    "Note du tableau de bord", previous, content);
            }
            transaction.Commit();
        }
        if (legacy)
            await AuditRows.WriteApartAsync(_appData, author, AuditRows.ModuleConditionnement, AuditRows.NoteUpdated,
                "Note du tableau de bord", previous, content);
    }

    // The name shown for a machine (Paramètres): in spiga_suivi over
    // stats.Machines, which is then never written; in stats.Machines itself
    // while PCs still run 2.0. Audited.
    public async Task UpdateMachineNameAsync(string machineId, string machineName, ChangeAuthor author)
    {
        await OffUiThread.Switch();
        await Access.DemandAsync(_connectionFactory, machineId);
        machineName = (machineName ?? "").Trim();
        if (machineName.Length == 0)
            throw new ArgumentException("Le nom de la machine ne peut pas être vide.", nameof(machineName));

        string? statsName;
        using (var live = _connectionFactory.CreateConnection())
            statsName = await live.ExecuteScalarAsync<string?>("SELECT MachineName FROM Machines WHERE MachineId = @machineId", new { machineId });

        if (!ReferenceEquals(await _location.WriteTargetAsync(), _appData))
        {
            if (statsName == machineName)
                return;
            using (var live = _connectionFactory.CreateConnection())
                await live.ExecuteAsync("UPDATE Machines SET MachineName = @machineName WHERE MachineId = @machineId", new { machineId, machineName });
            await AuditRows.WriteApartAsync(_appData, author, AuditRows.ModuleParametres, AuditRows.MachineRenamed, machineId, statsName, machineName);
            return;
        }

        using var connection = _appData.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var previous = await connection.ExecuteScalarAsync<string?>(
            "SELECT Name FROM MachineDisplayNames WHERE MachineId = @machineId", new { machineId }, transaction) ?? statsName;
        if (previous == machineName)
            return;
        var args = new { machineId, machineName, now = DateTime.Now, login = author.Login };
        var updated = await connection.ExecuteAsync(
            "UPDATE MachineDisplayNames SET Name = @machineName, UpdatedAt = @now, UpdatedBy = @login WHERE MachineId = @machineId",
            args, transaction);
        if (updated == 0)
            await connection.ExecuteAsync(
                "INSERT INTO MachineDisplayNames (MachineId, Name, UpdatedAt, UpdatedBy) VALUES (@machineId, @machineName, @now, @login)",
                args, transaction);
        await AuditRows.WriteAsync(connection, transaction, author, AuditRows.ModuleParametres, AuditRows.MachineRenamed,
            machineId, previous, machineName);
        transaction.Commit();
    }

    private static int _displayNameFailures;

    // The application database unreachable: the names of stats.Machines
    // (logged once, the dashboard carries on).
    private async Task ApplyDisplayNamesAsync(List<Machine> machines)
    {
        try
        {
            // While PCs still run 2.0 the names are those of stats.Machines.
            if (await _location.ModeAsync() != PlantDataMode.Application)
                return;
            using var connection = _appData.CreateConnection();
            var names = (await connection.QueryAsync<(string MachineId, string Name)>("SELECT MachineId, Name FROM MachineDisplayNames"))
                .ToDictionary(n => n.MachineId, n => n.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var machine in machines)
                if (names.TryGetValue(machine.MachineId, out var name))
                    machine.MachineName = name;
            _displayNameFailures = 0;
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _displayNameFailures) == 1)
                AppLogger.Log("Noms des machines (base applicative) illisibles, noms de la base de production affichés : " + ex.Message);
        }
    }
}
