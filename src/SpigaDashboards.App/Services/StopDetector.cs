using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Services;

// Production stops from each line's timeline (ProductionRepository.GetLineSegmentsAsync).
// Stiavelli has no stop or alarm record, and keeps every production open
// (terminata is almost never set), so a stop is read from the counter: a
// run of snapshot intervals in which it didn't move. Each run is cut at the
// poste changes (one sheet per poste and team), the planned breaks are taken
// out of it, and what is left is a stop when it reaches the threshold set in
// Parametres. A poste in which the line made no bag at all is skipped: the
// line wasn't scheduled (PC02/PL02 sat frozen for days with StatsViewer
// still writing snapshots).
public static class StopDetector
{
    public static List<ProductionStop> Detect(
        IEnumerable<LineSegment> segments, DateTime from, DateTime to, AppSettings settings,
        TeamScheduleService teams, IReadOnlyDictionary<string, string> machineNames)
    {
        var windows = BreakSchedule.DayWindows(settings.PlannedBreaks);
        var threshold = Math.Max(1, settings.StopThresholdMinutes);
        var stops = new List<ProductionStop>();

        foreach (var line in segments.GroupBy(s => s.MachineId))
        {
            var list = line.OrderBy(s => s.StartTime).ToList();
            var producingPostes = list
                .Where(s => s.State == LineState.Producing)
                .SelectMany(s => Postes(s.StartTime, s.EndTime))
                .Select(p => p.Start)
                .ToHashSet();

            foreach (var run in StoppedRuns(list))
            {
                var start = run[0].StartTime;
                var end = run[^1].EndTime;
                foreach (var poste in Postes(start, end))
                {
                    if (poste.Start < from || poste.Start >= to || !producingPostes.Contains(poste.Start))
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
                        BreakMinutes = BreakSchedule.OverlapMinutes(windows, pieceStart, pieceEnd),
                        NoDataMinutes = run.Where(s => s.State == LineState.NoData)
                            .Sum(s => Overlap(s.StartTime, s.EndTime, pieceStart, pieceEnd)),
                    };
                    if (stop.NetMinutes >= threshold)
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
    // data). A line's segments chain end to start.
    private static IEnumerable<List<LineSegment>> StoppedRuns(List<LineSegment> segments)
    {
        List<LineSegment>? current = null;
        foreach (var s in segments)
        {
            if (s.State is LineState.Producing or LineState.Unplaced)
            {
                if (current != null)
                    yield return current;
                current = null;
            }
            else if (current != null && s.StartTime <= current[^1].EndTime)
            {
                current.Add(s);
            }
            else
            {
                if (current != null)
                    yield return current;
                current = new List<LineSegment> { s };
            }
        }
        if (current != null)
            yield return current;
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
