using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Services;

// The detected stops of the Arrêts page (WPF and Razor) with the causes
// given in Qualification des arrêts.
public static class StopMatching
{
    // Each detected stop gets its cause, matched as in the qualification page
    // (same machine, start within 3 min, each cause used once). Causes left
    // over (pannes declared by hand) are listed as stops of their own when
    // they start in [from, to) (read the causes 1 h wider for the matching).
    public static List<ProductionStop> WithQualifications(List<ProductionStop> stops, IReadOnlyList<StopQualification> quals,
        IReadOnlyDictionary<string, string> names, DateTime from, DateTime to)
    {
        var used = new HashSet<int>();
        foreach (var stop in stops)
        {
            stop.Qualification = quals.FirstOrDefault(q => !used.Contains(q.Id) && q.MachineId == stop.MachineId
                && Math.Abs((q.StopStart - stop.Start).TotalMinutes) <= 3);
            if (stop.Qualification != null)
                used.Add(stop.Qualification.Id);
        }
        // A long stop qualified on one of its postes: the others show the
        // same cause (the qualification page saves it for each of them).
        foreach (var stop in stops.Where(s => s.Qualification == null))
            stop.Qualification = stops.FirstOrDefault(o => o.Qualification != null && o.MachineId == stop.MachineId
                && o.RunStart == stop.RunStart)?.Qualification;

        var extra = quals.Where(q => !used.Contains(q.Id) && q.StopStart >= from && q.StopStart < to && q.StopEnd > q.StopStart)
            .Select(q => new ProductionStop
            {
                MachineId = q.MachineId,
                MachineName = names.GetValueOrDefault(q.MachineId, q.MachineId),
                ShiftDate = q.ShiftDate,
                ShiftId = q.ShiftId,
                Team = q.Team,
                Start = q.StopStart,
                End = q.StopEnd,
                RunStart = q.StopStart,
                Qualification = q,
            });
        return stops.Concat(extra)
            .OrderBy(s => s.ShiftDate).ThenBy(s => s.ShiftId).ThenBy(s => s.MachineId).ThenBy(s => s.Start)
            .ToList();
    }

    // The postes of these days: Matin of the first (06:00) to Nuit of the
    // last (06:00 the morning after); the two days in either order.
    public static (DateTime From, DateTime To) PostesOf(DateTime firstDay, DateTime lastDay)
    {
        var (a, b) = firstDay.Date <= lastDay.Date ? (firstDay.Date, lastDay.Date) : (lastDay.Date, firstDay.Date);
        return (a.AddHours(6), b.AddDays(1).AddHours(6));
    }
}

// Stops of one team over a search: count, total time, and the part that
// is real machine stops (not "Sans données").
public sealed record TeamStopTotal(string Team, int Count, double TotalMinutes, double StopMinutes)
{
    public string TotalText => ProductionStop.FormatDuration(TotalMinutes);
    public string StopText => ProductionStop.FormatDuration(StopMinutes);

    public static List<TeamStopTotal> Of(IEnumerable<ProductionStop> stops)
        => stops.GroupBy(s => s.Team).OrderBy(g => g.Key)
            .Select(g => new TeamStopTotal(g.Key, g.Count(), g.Sum(s => s.NetMinutes), g.Where(s => !s.IsMostlyNoData).Sum(s => s.NetMinutes)))
            .ToList();
}
