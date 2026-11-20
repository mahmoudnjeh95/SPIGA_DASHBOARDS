using System.Globalization;
using Spiga.Core.Security;
using Spiga.Data;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace Spiga.UI.Services;

// Maintenance > Statistiques des arrêts (web and desktop): the qualified
// stops of today / the last 7, 30 or 90 days, by cause, component, line,
// day and hour, poste and team, plus the components that keep failing.
// Only stops given a cause count (Qualification des arrêts). Same figures
// as the former WPF tab (StatsViewModel), read through repositories limited
// to the account's machines for the page's right.
public sealed class StopStatsService(
    IDbConnectionFactory production, AppDbOptions appDb, AppSettings settings, TeamScheduleService teams, TimeProvider clock)
{
    public static readonly string Permission = NativePages.Permission(NativePage.StopStatistics);

    public static readonly int[] Periods = { 1, 7, 30, 90 };

    private DateTime Now => clock.GetLocalNow().DateTime;

    // The account's lines for this page: active, not hidden, couscous first.
    public async Task<List<Machine>> MachinesAsync(Actor actor)
    {
        var (repository, _) = Repositories(actor);
        return (await repository.GetMachinesAsync())
            .Where(m => m.IsActive && !settings.HiddenMachineIds.Contains(m.MachineId))
            .OrderBy(m => SectorOrder(m.Sector)).ThenBy(m => m.MachineId)
            .ToList();
    }

    public IReadOnlyList<string> Teams => teams.TeamNames;

    // A team-limited account sees its team's stops only.
    public static string? TeamFor(Actor actor, string? chosen) => actor.Team ?? chosen;

    public (DateTime From, DateTime PrevFrom) Range(int days)
    {
        var now = Now;
        var from = days == 1
            ? (now.Hour < 6 ? now.Date.AddDays(-1) : now.Date).AddHours(6)
            : now.Date.AddDays(-days + 1);
        return (from, from.AddDays(-days));
    }

    public async Task<StopStats> LoadAsync(Actor actor, StatsFilter filter)
    {
        var now = Now;
        var today = filter.PeriodDays == 1;
        var (from, prevFrom) = Range(filter.PeriodDays);
        var (productionRepo, planning) = Repositories(actor);
        var all = await MachinesAsync(actor);
        var machines = filter.MachineId == null ? all : all.Where(m => m.MachineId == filter.MachineId).ToList();
        var ids = machines.Select(m => m.MachineId).ToHashSet();
        var team = TeamFor(actor, filter.Team);
        bool Keep(StopQualification q) => ids.Contains(q.MachineId) && (!filter.PannesOnly || q.IsPanne) && (team == null || q.Team == team);

        var quals = (await planning.GetQualificationsAsync(prevFrom, now.AddDays(1))).Where(Keep).ToList();
        var cur = quals.Where(q => q.StopStart >= from).ToList();
        var prev = quals.Where(q => q.StopStart < from).ToList();

        var minutes = cur.Sum(q => q.Minutes);
        var prevMinutes = prev.Sum(q => q.Minutes);
        var stats = new StopStats { From = from, To = now, Minutes = minutes, Count = cur.Count };
        stats.Tiles.Add(new StatTile(filter.PannesOnly ? "Pannes" : "Arrêts qualifiés", cur.Count.ToString(CultureInfo.InvariantCulture),
            today ? $"vs {prev.Count} hier" : $"vs {prev.Count} sur les {filter.PeriodDays} j précédents", Delta(cur.Count, prev.Count), DeltaCss(cur.Count, prev.Count)));
        stats.Tiles.Add(new StatTile("Temps d'arrêt", Duration(minutes), $"vs {Duration(prevMinutes)} avant",
            Delta(minutes, prevMinutes), DeltaCss(minutes, prevMinutes)));
        // MTBF and lost availability need the lines' run hours: AddRunHoursAsync.
        stats.Tiles.Add(new StatTile("MTBF · temps moyen entre pannes", "…", "calcul du temps de marche…"));
        stats.Tiles.Add(new StatTile("MTTR · temps moyen de réparation", cur.Count == 0 ? "—" : Duration(minutes / cur.Count),
            prev.Count == 0 ? "par arrêt" : $"vs {Duration(prevMinutes / prev.Count)} avant"));
        stats.Tiles.Add(new StatTile("Disponibilité perdue", "…", "du temps de marche + arrêts"));

        // Pareto by cause and by component
        stats.ByCause = Pareto(cur.GroupBy(q => StopCauses.Label(q.CauseCode)), minutes, 8);
        stats.ByComponent = Pareto(cur.Where(q => !string.IsNullOrEmpty(q.Component))
            .GroupBy(q => q.Component!, StringComparer.OrdinalIgnoreCase), minutes, 8);
        var top3 = cur.GroupBy(q => q.Component ?? StopCauses.Label(q.CauseCode)).Select(g => g.Sum(q => q.Minutes))
            .OrderByDescending(m => m).Take(3).Sum();
        stats.ParetoCaption = minutes <= 0 ? "Aucun arrêt qualifié sur la période"
            : $"Les 3 premiers composants représentent {top3 / minutes * 100:0} % du temps d'arrêt";

        // By line
        var perMachine = machines.Select(m => (m, list: cur.Where(q => q.MachineId == m.MachineId).ToList()))
            .OrderByDescending(x => x.list.Sum(q => q.Minutes)).ToList();
        stats.ByMachine = perMachine.Select(x =>
        {
            var mins = x.list.Sum(q => q.Minutes);
            return new StatGroup(x.m.MachineName, mins, x.list.Count,
                Tooltip: $"{x.m.MachineName} : {Duration(mins)} · {Stops(x.list.Count)}" + (x.list.Count > 0 ? $" · MTTR {Duration(mins / x.list.Count)}" : ""));
        }).ToList();
        var top2 = perMachine.Take(2).Sum(x => x.list.Sum(q => q.Minutes));
        stats.MachineCaption = minutes <= 0 || perMachine.Count < 2 ? ""
            : $"{perMachine[0].m.MachineName} et {perMachine[1].m.MachineName} : {top2 / minutes * 100:0} % du temps perdu";

        // Day of week x hour of the start
        foreach (var q in cur)
        {
            var d = ((int)q.StopStart.DayOfWeek + 6) % 7;
            stats.HeatMax = Math.Max(stats.HeatMax, ++stats.Heat[d, q.StopStart.Hour]);
        }
        var peak = Enumerable.Range(0, 24).Select(h => (h, n: Enumerable.Range(0, 7).Sum(d => stats.Heat[d, h]))).OrderByDescending(x => x.n).First();
        stats.HeatCaption = cur.Count == 0 ? "Heure de début de chaque arrêt"
            : $"Heure de début de chaque arrêt · heure la plus chargée : {peak.h:00}h ({Stops(peak.n)})";

        // By poste and team
        stats.ByPoste = Groups(cur.GroupBy(q => KpiCalculator.ShiftName(q.ShiftId)), new[] { "Matin", "Après-midi", "Nuit" });
        stats.ByTeam = Groups(cur.GroupBy(q => q.Team), team == null ? teams.TeamNames.ToArray() : new[] { team });
        var night = cur.Where(q => q.ShiftId == 3).Sum(q => q.Minutes);
        stats.GroupCaption = minutes <= 0 ? "" : $"Le poste de nuit concentre {night / minutes * 100:0} % du temps d'arrêt";

        // 12-week trend (whatever the period)
        var weekStart = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        var trend = (await planning.GetQualificationsAsync(weekStart.AddDays(-77), now.AddDays(1))).Where(Keep).ToList();
        stats.Weeks = Enumerable.Range(0, 12).Select(i =>
        {
            var ws = weekStart.AddDays(-7 * (11 - i));
            var inWeek = trend.Where(q => q.StopStart >= ws && q.StopStart < ws.AddDays(7)).ToList();
            var week = ISOWeek.GetWeekOfYear(ws);
            var mins = inWeek.Sum(q => q.Minutes);
            return new StatGroup("S" + week, mins, inWeek.Count,
                Tooltip: $"Semaine {week} : {Duration(mins)}" + (ws == weekStart ? " (en cours)" : ""));
        }).ToList();

        // Components that keep failing on the same line
        var plans = await planning.GetPlansAsync();
        var names = all.ToDictionary(m => m.MachineId, m => m.MachineName);
        foreach (var g in cur.Where(q => q.IsPanne && !string.IsNullOrEmpty(q.Component))
                     .GroupBy(q => (q.MachineId, Component: q.Component!.ToLowerInvariant()))
                     .Where(g => g.Count() >= 3).OrderByDescending(g => g.Count()))
        {
            var component = g.First().Component!;
            var plan = plans.FirstOrDefault(p => p.MachineId == g.Key.MachineId && p.Task.Contains(component, StringComparison.OrdinalIgnoreCase));
            stats.Repeats.Add(new RepeatFailure(g.Key.MachineId, names.GetValueOrDefault(g.Key.MachineId, g.Key.MachineId), component, g.Count(),
                Duration(g.Sum(q => q.Minutes)),
                today ? $"{(now - from).TotalHours / g.Count():0.#} h" : $"{(double)filter.PeriodDays / g.Count():0.#} j",
                plan == null ? null : Rule(plan)));
        }
        return stats;
    }

    // MTBF and lost availability: the run hours of the lines (production
    // database, the slow part), read after the rest is shown. Not readable
    // (base slow or down): "—".
    public async Task AddRunHoursAsync(Actor actor, StatsFilter filter, StopStats stats)
    {
        var (productionRepo, _) = Repositories(actor);
        var all = await MachinesAsync(actor);
        var ids = (filter.MachineId == null ? all : all.Where(m => m.MachineId == filter.MachineId)).Select(m => m.MachineId).ToList();
        var team = TeamFor(actor, filter.Team);
        double runHours = 0;
        try
        {
            // Day buckets are light; one team needs hour buckets (a day mixes
            // the three postes), and so does today (day buckets start at
            // midnight, the day here at 06:00).
            var buckets = ids.Count == 0 ? new List<ProductionBucket>()
                : await productionRepo.GetLotsForMachinesAsync(ids, stats.From, stats.To,
                    team == null && filter.PeriodDays != 1 ? BucketSize.Day : BucketSize.Hour);
            runHours = buckets.Where(b => team == null || teams.TeamOf(b) == team).Sum(b => b.RunMinutes) / 60;
        }
        catch (Exception ex)
        {
            AppLogger.Log("Statistiques des arrêts : temps de marche non lu : " + ex.Message);
        }
        var minutes = stats.Minutes;
        stats.Tiles[2] = new StatTile("MTBF · temps moyen entre pannes", stats.Count == 0 || runHours <= 0 ? "—" : $"{runHours / stats.Count:0} h",
            $"sur {runHours.ToString("N0", Fmt.French)} h de marche des lignes");
        stats.Tiles[4] = new StatTile("Disponibilité perdue",
            runHours <= 0 ? "—" : (minutes / 60 / (runHours + minutes / 60) * 100).ToString("0.0", Fmt.French) + " %",
            "du temps de marche + arrêts");
    }

    // Stops of the period still without a cause, as the qualification page
    // would count them once opened (a stop following one already qualified,
    // or covered by a planned slot, gets its cause there): the statistics
    // leave them out. Read only.
    public async Task<(int Count, double Minutes)> UnqualifiedAsync(Actor actor, StatsFilter filter)
    {
        var now = Now;
        var (from, _) = Range(filter.PeriodDays);
        var (productionRepo, planning) = Repositories(actor);
        var all = await MachinesAsync(actor);
        var machines = filter.MachineId == null ? all : all.Where(m => m.MachineId == filter.MachineId).ToList();
        if (machines.Count == 0)
            return (0, 0);
        var team = TeamFor(actor, filter.Team);
        var names = machines.ToDictionary(m => m.MachineId, m => m.MachineName);
        var segments = await productionRepo.GetLineSegmentsAsync(names.Keys.ToList(), from, now);
        var stops = StopDetector.Detect(segments, from, now, settings, teams, names);
        var quals = (await planning.GetQualificationsAsync(from.AddHours(-1), now.AddHours(1))).Where(q => names.ContainsKey(q.MachineId)).ToList();
        var slots = await planning.GetEventsAsync(from, now);

        // Matched as the qualification page matches them (same line, start within 3 min).
        var used = new HashSet<int>();
        var matched = new Dictionary<ProductionStop, StopQualification>();
        foreach (var s in stops)
        {
            var match = quals.FirstOrDefault(q => !used.Contains(q.Id) && q.MachineId == s.MachineId && Math.Abs((q.StopStart - s.Start).TotalMinutes) <= 3);
            if (match == null)
                continue;
            used.Add(match.Id);
            matched[s] = match;
        }

        bool HasCause(ProductionStop s)
        {
            if (matched.ContainsKey(s))
                return true;
            // The rest of a long stop qualified on an earlier poste.
            if (matched.Any(m => !m.Value.IsManual && m.Key.MachineId == s.MachineId && m.Key.RunStart == s.RunStart))
                return true;
            // Covered by a planned stop or maintenance slot.
            var length = (s.End - s.Start).TotalMinutes;
            return !s.IsLive && length > 0 && slots.Any(e => e.MachineId == s.MachineId
                && (Min(e.ActualEnd, s.End) - Max(e.PlannedStart, s.Start)).TotalMinutes >= 0.8 * length);
        }

        var open = stops.Where(s => (team == null || s.Team == team) && !HasCause(s)).ToList();
        return (open.Count, open.Sum(s => s.NetMinutes));
    }

    private (ProductionRepository Production, PlanningRepository Planning) Repositories(Actor actor)
        => ScopedData.For(actor, Permission, production, appDb, settings);

    private static List<StatGroup> Pareto(IEnumerable<IGrouping<string, StopQualification>> groups, double total, int take)
    {
        var list = groups.Select(g => (g.Key, mins: g.Sum(q => q.Minutes), n: g.Count()))
            .OrderByDescending(x => x.mins).ToList();
        if (list.Count > take)
        {
            var rest = list.Skip(take - 1).ToList();
            list = list.Take(take - 1).Append(("Autres", rest.Sum(x => x.mins), rest.Sum(x => x.n))).ToList();
        }
        double cum = 0;
        return list.Select(x =>
        {
            cum += x.mins;
            return new StatGroup(x.Key, x.mins, x.n, total > 0 ? cum / total * 100 : 0,
                $"{x.Key} : {Duration(x.mins)} · {Stops(x.n)} · {(total > 0 ? x.mins / total * 100 : 0):0} % du total");
        }).ToList();
    }

    private static List<StatGroup> Groups(IEnumerable<IGrouping<string, StopQualification>> groups, string[] order)
    {
        var map = groups.ToDictionary(g => g.Key, g => (mins: g.Sum(q => q.Minutes), n: g.Count()));
        return order.Concat(map.Keys.Where(k => !order.Contains(k))).Select(k =>
        {
            var (mins, n) = map.GetValueOrDefault(k);
            return new StatGroup(k, mins, n, Tooltip: $"{k} : {Duration(mins)} · {Stops(n)}");
        }).ToList();
    }

    public static string Rule(MaintenancePlan p) => p.Kind == MaintenanceKind.Calendar
        ? $"Calendaire · tous les {p.IntervalDays} j"
        : $"Usage · tous les {p.IntervalBags.ToString("N0", Fmt.French)} sacs";

    // "7 min", "1 h 05", and from a day on "2 j", "1 j 6 h", "2 j 4 h 30"
    public static string Duration(double minutes)
    {
        var total = (int)Math.Round(minutes);
        if (total < 24 * 60)
            return ProductionStop.FormatDuration(total);
        var (days, hours, mins) = (total / 1440, total % 1440 / 60, total % 60);
        return $"{days} j" + (hours > 0 || mins > 0 ? $" {hours} h" : "") + (mins > 0 ? $" {mins:00}" : "");
    }

    // Short bar label: "45 min", "6,5 h", "25 h".
    public static string Hours(double hours) => hours <= 0 ? ""
        : hours < 1 ? $"{hours * 60:0} min"
        : hours.ToString(hours < 10 ? "0.#" : "0", Fmt.French) + " h";

    private static string Stops(int n) => $"{n} arrêt{(n > 1 ? "s" : "")}";

    private static string Delta(double cur, double prev)
        => prev <= 0 ? "" : (cur >= prev ? "▲ " : "▼ ") + Math.Abs((cur - prev) / prev * 100).ToString("0", CultureInfo.InvariantCulture) + " %";

    // More stops is bad, fewer is good.
    private static string? DeltaCss(double cur, double prev) => prev <= 0 ? null : cur > prev ? "bad" : "ok";

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static int SectorOrder(string sector) => sector switch
    {
        "Couscous" => 0,
        "Pate courte" => 1,
        "Pate longue" => 2,
        _ => 3,
    };
}

public sealed record StatsFilter(int PeriodDays, string? MachineId, string? Team, bool PannesOnly);

public sealed record StatTile(string Label, string Value, string Hint, string Delta = "", string? DeltaCss = null);

// One bar of a chart: stop time, number of stops and, for a Pareto, the
// cumulated share of the total stop time.
public sealed record StatGroup(string Label, double Minutes, int Count, double? Cumul = null, string Tooltip = "");

// The same component failing again and again on one line. PlanRule: its
// preventive maintenance rule, null = none.
public sealed record RepeatFailure(string MachineId, string MachineName, string Component, int Count, string Lost, string Every, string? PlanRule);

public sealed class StopStats
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }

    // Stop time and number of stops of the period.
    public double Minutes { get; init; }
    public int Count { get; init; }
    public List<StatTile> Tiles { get; } = new();
    public List<StatGroup> ByCause { get; set; } = new();
    public List<StatGroup> ByComponent { get; set; } = new();
    public List<StatGroup> ByMachine { get; set; } = new();
    public List<StatGroup> ByPoste { get; set; } = new();
    public List<StatGroup> ByTeam { get; set; } = new();
    public List<StatGroup> Weeks { get; set; } = new();
    public string ParetoCaption { get; set; } = "";
    public string MachineCaption { get; set; } = "";
    public string GroupCaption { get; set; } = "";
    public string HeatCaption { get; set; } = "";

    // Stops started on each day of the week (Monday first) and hour.
    public int[,] Heat { get; } = new int[7, 24];
    public int HeatMax { get; set; }
    public List<RepeatFailure> Repeats { get; } = new();
}
