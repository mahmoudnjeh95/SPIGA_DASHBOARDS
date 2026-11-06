using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Services;

// Production stops from each line's timeline (ProductionRepository.GetLineSegmentsAsync).
// Stiavelli has no stop or alarm record, and keeps every production open
// (terminata is almost never set), so a stop is read from the counter: a
// run of snapshot intervals in which it didn't move. Each run is cut at the
// poste changes (one sheet per poste and team), the planned breaks are taken
// out of it, and what is left is a stop when it reaches the threshold set in
// Parametres. The lines run 24/7: every poste a stop covers gets its piece
// (one row per team), however long the stop (a panne can last days); a poste
// that wasn't to run is an "arrêt planifié" slot of the calendar instead.
// A stop only ends once the line has produced for MinRestartMinutes in a
// row: the few test bags of a repair don't cut a panne in two.
public static class StopDetector
{
    public const double MinRestartMinutes = 2;

    // thresholdMinutes: instead of the Parametres one (the Pannes tab counts
    // the micro-stops below it with 1).
    public static List<ProductionStop> Detect(
        IEnumerable<LineSegment> segments, DateTime from, DateTime to, AppSettings settings,
        TeamScheduleService teams, IReadOnlyDictionary<string, string> machineNames, int? thresholdMinutes = null)
    {
        var windows = BreakSchedule.DayWindows(settings.PlannedBreaks);
        var threshold = Math.Max(1, thresholdMinutes ?? settings.StopThresholdMinutes);
        var stops = new List<ProductionStop>();

        foreach (var line in segments.GroupBy(s => s.MachineId))
        {
            var list = line.OrderBy(s => s.StartTime).ToList();

            foreach (var run in StoppedRuns(list))
            {
                var start = run[0].StartTime;
                var end = run[^1].EndTime;
                // The threshold applies to the whole stop: once it is a stop,
                // each team gets its part, even a few minutes at a poste change.
                var runNet = (end - start).TotalMinutes - BreakSchedule.OverlapMinutes(windows, start, end);
                if (runNet < threshold)
                    continue;
                foreach (var poste in Postes(start, end))
                {
                    if (poste.Start < from || poste.Start >= to)
                        continue;
                    var pieceStart = start > poste.Start ? start : poste.Start;
                    var pieceEnd = end < poste.End ? end : poste.End;
                    var stop = new ProductionStop
                    {
                        MachineId = line.Key,
                        MachineName = machineNames.GetValueOrDefault(line.Key, line.Key),
                        ShiftDate = poste.Start.Date,
                        ShiftId = poste.ShiftId,
                        Team = Team(teams, poste.Start.Date, poste.ShiftId),
                        Start = pieceStart,
                        End = pieceEnd,
                        RunStart = start,
                        IsLive = pieceEnd == end && DateTime.Now - end < OngoingStopService.LiveWindow,
                        BreakMinutes = BreakSchedule.OverlapMinutes(windows, pieceStart, pieceEnd),
                        NoDataMinutes = run.Where(s => s.State == LineState.NoData)
                            .Sum(s => Overlap(s.StartTime, s.EndTime, pieceStart, pieceEnd)),
                    };
                    if (stop.NetMinutes >= 1)
                        stops.Add(stop);
                }
            }
        }

        return stops
            .OrderBy(s => s.ShiftDate).ThenBy(s => s.ShiftId).ThenBy(s => s.MachineId).ThenBy(s => s.Start)
            .ToList();
    }

    private static string Team(TeamScheduleService teams, DateTime shiftDate, int shiftId)
        => teams.GetTeamForShift(shiftDate, shiftId) is { Length: > 0 } team ? team : TeamScheduleService.NoTeam;

    // Consecutive segments in which the counter didn't move (stopped or no
    // data), with the producing stretches shorter than MinRestartMinutes
    // between them (test bags), and one at the very end (the line has only
    // just restarted: still stopped). A line's segments chain end to start.
    public static IEnumerable<List<LineSegment>> StoppedRuns(List<LineSegment> segments)
    {
        List<LineSegment>? current = null;
        List<LineSegment>? held = null; // short producing stretch inside current
        foreach (var s in segments)
        {
            var contiguous = current != null && s.StartTime <= (held?[^1] ?? current[^1]).EndTime;
            if (s.State == LineState.Unplaced || (current != null && !contiguous))
            {
                if (current != null)
                    yield return Close(current, held);
                current = null;
                held = null;
                if (s.State == LineState.Unplaced)
                    continue;
            }

            if (s.State == LineState.Producing)
            {
                if (current == null)
                    continue;
                held ??= new List<LineSegment>();
                held.Add(s);
                if (held.Sum(h => (h.EndTime - h.StartTime).TotalMinutes) >= MinRestartMinutes)
                {
                    yield return current;
                    current = null;
                    held = null;
                }
                continue;
            }

            if (current == null)
            {
                current = new List<LineSegment> { s };
                continue;
            }
            if (held != null)
            {
                current.AddRange(held);
                held = null;
            }
            current.Add(s);
        }
        if (current != null)
            yield return Close(current, held);
    }

    // A run that ends on a short producing stretch: the restart isn't
    // confirmed yet, the stop goes on through it.
    private static List<LineSegment> Close(List<LineSegment> run, List<LineSegment>? held)
    {
        if (held != null)
            run.AddRange(held);
        return run;
    }

    // The postes (06-14 Matin, 14-22 Après-midi, 22-06 Nuit) overlapping [start, end).
    private static IEnumerable<(DateTime Start, DateTime End, int ShiftId)> Postes(DateTime start, DateTime end)
    {
        var shiftId = KpiCalculator.CurrentShiftId(start);
        var posteStart = TeamScheduleService.GetShiftDate(start, shiftId).AddHours(KpiCalculator.ShiftStartHour(shiftId));
        for (; posteStart < end; posteStart = posteStart.AddHours(8))
            yield return (posteStart, posteStart.AddHours(8), KpiCalculator.CurrentShiftId(posteStart));
    }

    private static double Overlap(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd)
    {
        var minutes = ((aEnd < bEnd ? aEnd : bEnd) - (aStart > bStart ? aStart : bStart)).TotalMinutes;
        return minutes > 0 ? minutes : 0;
    }
}
