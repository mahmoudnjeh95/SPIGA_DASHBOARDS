using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels.Slides;

// One cell of the machine x team table: net weight produced, bags and TRS
// (with its D / P / Q), banded against the machine's (or the group's) target.
public class PackagingCell
{
    public double ProductionKg { get; init; }
    public long TotalBags { get; init; }
    public KpiSummary Kpis { get; init; } = new();
    public KpiSeverity TrsSeverity { get; init; }
    public bool HasProduction => TotalBags > 0 || ProductionKg > 0;
    public string ProductionText => HasProduction ? KpiCalculator.FormatMass(ProductionKg) : "—";
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
// = CC03 + CC04, Conditionnement 2 = the others, Cumul = all of them) summed
// per machine and per team over the top-bar period, with the team and machine
// totals as bars. The TRS variant (IsTrs, the slide right after) shows the
// same table led by the TRS, and the team and machine TRS as bars against the
// target. Cumul always covers the current month, and its machine bars are
// labelled with the Parametres names (the run state has its own slide,
// RunStateSlideViewModel, just before).
public class PackagingSlideViewModel
{
    public string Title { get; init; } = "";
    public bool IsTrs { get; init; }
    public bool IsCumul { get; init; }
    public string MachinesLabel { get; init; } = "";
    public string PeriodLabel { get; init; } = "";
    public string FilterLabel { get; init; } = "";
    public string TotalProductionText { get; init; } = "";
    public PackagingCell Overall { get; init; } = new();

    // Target the group totals are banded against: the machines' own when
    // they share one, else their average.
    public double TrsTarget { get; init; }

    // Team names: the table's column headers (before "Total").
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();

    // One row per machine, then the Total row.
    public IReadOnlyList<PackagingRow> Rows { get; init; } = Array.Empty<PackagingRow>();

    public ChartParts TeamChart { get; init; } = ChartParts.Empty();
    public ChartParts MachineChart { get; init; } = ChartParts.Empty();
    public string TeamChartTitle => IsTrs ? "TRS par équipe" : "Production par équipe";
    public string MachineChartTitle => IsTrs ? "TRS par machine" : "Production par machine";

    // Ranges over a week are kept CacheDuration per group, as on the period slides.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly Dictionary<(string, DateTime, DateTime), (DateTime LoadedAt, List<ProductionBucket> Lots)> Cache = new();

    public static void ClearCache() => Cache.Clear();

    public static async Task<PackagingSlideViewModel> LoadAsync(
        ProductionRepository repository,
        TeamScheduleService teamSchedule,
        AppSettings settings,
        string title,
        bool trs,
        bool cumul,
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

        var targets = machines.Select(m => settings.TrsTarget(m.MachineId)).ToList();
        var groupTarget = targets.Count > 0 ? targets.Average() : AppSettings.DefaultTrsTarget;

        var rows = machines.Select(m =>
        {
            var machineLots = lots.Where(l => l.MachineId == m.MachineId).ToList();
            var target = settings.TrsTarget(m.MachineId);
            return new PackagingRow
            {
                Label = m.MachineName,
                Cells = teams.Select(t => Cell(machineLots.Where(l => teamSchedule.TeamOf(l) == t), target)).ToList(),
                Total = Cell(machineLots, target),
            };
        }).ToList();
        var teamTotals = teams.Select(t => Cell(byTeam[t], groupTarget)).ToList();
        var total = Cell(lots, groupTarget);
        rows.Add(new PackagingRow { Label = "Total", IsTotal = true, Cells = teamTotals, Total = total });
        var machineTotals = rows.Where(r => !r.IsTotal).Select(r => r.Total).ToList();

        // Target line only when every machine shares the same target.
        double? targetLine = targets.Distinct().Count() == 1 ? targets[0] : null;

        // Always the names from Parametres (never the line codes CC03...).
        var machineLabels = machines.Select(m => m.MachineName).ToList();

        return new PackagingSlideViewModel
        {
            Title = title,
            IsTrs = trs,
            IsCumul = cumul,
            MachinesLabel = cumul ? $"{ids.Count} machines" : string.Join(", ", machineLabels),
            PeriodLabel = periodLabel,
            FilterLabel = filter.Describe(),
            TotalProductionText = KpiCalculator.FormatMass(total.ProductionKg),
            Overall = total,
            TrsTarget = groupTarget,
            Columns = teams,
            Rows = rows,
            TeamChart = trs
                ? TrsBars(teams, teamTotals, targetLine)
                : Bars(teams, teamTotals.Select(c => c.ProductionKg).ToList()),
            MachineChart = trs
                ? TrsBars(machineLabels, machineTotals, targetLine)
                : Bars(machineLabels, machineTotals.Select(c => c.ProductionKg).ToList()),
        };
    }

    // Same figures as everywhere else: production = bags x net average weight
    // (or x target when the weight is unknown) summed over the weights run,
    // TRS from KpiCalculator.Summarize over the cell's buckets.
    private static PackagingCell Cell(IEnumerable<ProductionBucket> lots, double trsTarget)
    {
        var list = lots.ToList();
        var kpis = KpiCalculator.Summarize(list, KpiCalculator.AvailabilityPercent(list), trsTarget);
        return new PackagingCell
        {
            ProductionKg = kpis.ProductionKg,
            TotalBags = list.Sum(l => l.TotalBags),
            Kpis = kpis,
            TrsSeverity = KpiCalculator.TrsSeverity(kpis.TrsPercent, trsTarget),
        };
    }

    private static ChartParts TrsBars(IReadOnlyList<string> labels, IReadOnlyList<PackagingCell> cells, double? target)
        => cells.Any(c => c.Kpis.TrsPercent.HasValue)
            ? DashboardCharts.TrsBars(labels, cells.Select(c => c.Kpis.TrsPercent).ToList(), target)
            : ChartParts.Empty();

    private static ChartParts Bars(IReadOnlyList<string> labels, IReadOnlyList<double> values)
        => values.Any(v => v > 0)
            ? DashboardCharts.Bars(labels, values, "Production", v => KpiCalculator.FormatMass(v))
            : ChartParts.Empty();
}
