using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels.Slides;

// One cell of the machine x team table: net weight produced and bags.
public class PackagingCell
{
    public double ProductionKg { get; init; }
    public long TotalBags { get; init; }
    public string ProductionText => TotalBags > 0 || ProductionKg > 0 ? KpiCalculator.FormatMass(ProductionKg) : "—";
}

// One row of the table: a machine (or the Total row), one cell per team, then its total.
public class PackagingRow
{
    public string Label { get; init; } = "";
    public bool IsTotal { get; init; }
    public IReadOnlyList<PackagingCell> Cells { get; init; } = Array.Empty<PackagingCell>();
    public PackagingCell Total { get; init; } = new();
}

// Conditionnement slides: the production of a group of lines (Conditionnement 1
// = CC03 + CC04, Conditionnement 2 = the others) summed per machine and per
// team over the top-bar period, with the team and machine totals as bars.
public class PackagingSlideViewModel
{
    public string Title { get; init; } = "";
    public string MachinesLabel { get; init; } = "";
    public string PeriodLabel { get; init; } = "";
    public string FilterLabel { get; init; } = "";
    public string TotalProductionText { get; init; } = "";

    // Team names: the table's column headers (before "Total").
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();

    // One row per machine, then the Total row.
    public IReadOnlyList<PackagingRow> Rows { get; init; } = Array.Empty<PackagingRow>();

    public ChartParts TeamChart { get; init; } = ChartParts.Empty();
    public ChartParts MachineChart { get; init; } = ChartParts.Empty();

    // Ranges over a week are kept CacheDuration per group, as on the period slides.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly Dictionary<(string, DateTime, DateTime), (DateTime LoadedAt, List<ProductionBucket> Lots)> Cache = new();

    public static void ClearCache() => Cache.Clear();

    public static async Task<PackagingSlideViewModel> LoadAsync(
        ProductionRepository repository,
        TeamScheduleService teamSchedule,
        string title,
        IReadOnlyList<Machine> machines,
        DateTime from,
        DateTime to,
        string periodLabel,
        DashboardFilter filter)
    {
        var ids = machines.Select(m => m.MachineId).ToList();
        // Hour buckets: teams are worked out from each bucket's poste.
        var key = (string.Join(",", ids), from, to);
        List<ProductionBucket> allLots;
        if ((to - from).TotalDays <= 8)
        {
            allLots = await repository.GetLotsForMachinesAsync(ids, from, to);
        }
        else if (Cache.TryGetValue(key, out var cached) && DateTime.Now - cached.LoadedAt <= CacheDuration)
        {
            allLots = cached.Lots;
        }
        else
        {
            allLots = await repository.GetLotsForMachinesAsync(ids, from, to);
            Cache[key] = (DateTime.Now, allLots);
        }
        var lots = filter.Apply(allLots, teamSchedule);

        // Every configured team, even at 0, so the columns compare like for
        // like; "Sans equipe" only when such hours produced something.
        var teams = filter.Team != null ? new List<string> { filter.Team } : teamSchedule.TeamNames.ToList();
        var byTeam = lots.ToLookup(teamSchedule.TeamOf);
        if (!teams.Contains(TeamScheduleService.NoTeam) && byTeam[TeamScheduleService.NoTeam].Any(l => l.TotalBags > 0))
            teams.Add(TeamScheduleService.NoTeam);

        var rows = machines.Select(m =>
        {
            var machineLots = lots.Where(l => l.MachineId == m.MachineId).ToList();
            return new PackagingRow
            {
                Label = m.MachineName,
                Cells = teams.Select(t => Cell(machineLots.Where(l => teamSchedule.TeamOf(l) == t))).ToList(),
                Total = Cell(machineLots),
            };
        }).ToList();
        var teamTotals = teams.Select(t => Cell(byTeam[t])).ToList();
        var total = Cell(lots);
        rows.Add(new PackagingRow { Label = "Total", IsTotal = true, Cells = teamTotals, Total = total });

        return new PackagingSlideViewModel
        {
            Title = title,
            MachinesLabel = string.Join(", ", ids),
            PeriodLabel = periodLabel,
            FilterLabel = filter.Describe(),
            TotalProductionText = KpiCalculator.FormatMass(total.ProductionKg),
            Columns = teams,
            Rows = rows,
            TeamChart = Bars(teams, teamTotals.Select(c => c.ProductionKg).ToList()),
            MachineChart = Bars(ids, rows.Where(r => !r.IsTotal).Select(r => r.Total.ProductionKg).ToList()),
        };
    }

    // Same production figure as everywhere else: bags x net average weight
    // (or x target when the weight is unknown), summed over the weights run.
    private static PackagingCell Cell(IEnumerable<ProductionBucket> lots)
    {
        var list = lots.ToList();
        return new PackagingCell
        {
            ProductionKg = Math.Round(KpiCalculator.ByWeight(list).Sum(w => w.ProductionKg), 1),
            TotalBags = list.Sum(l => l.TotalBags),
        };
    }

    private static ChartParts Bars(IReadOnlyList<string> labels, IReadOnlyList<double> values)
        => values.Any(v => v > 0)
            ? DashboardCharts.Bars(labels, values, "Production", v => KpiCalculator.FormatMass(v))
            : ChartParts.Empty();
}
