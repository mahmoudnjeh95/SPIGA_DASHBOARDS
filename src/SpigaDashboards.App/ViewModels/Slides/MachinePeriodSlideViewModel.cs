using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels.Slides;

// One team's figures over the period (a row of the comparison table).
public class TeamPeriodRow
{
    public string TeamName { get; init; } = "";
    public KpiSummary Kpis { get; init; } = new();
    public KpiSeverity TrsSeverity { get; init; }
    public KpiSeverity WeightDeviationSeverity { get; init; }
    public string AvgWeightText => KpiCalculator.FormatAverages(Kpis.Weights);
}

// Period slide (hier, semaine, mois, 3 mois, dates choisies): one machine,
// period totals, a comparison of the teams that worked on it, the production
// per weight, and the trend per hour / day / week (TRS, speed, production,
// weight deviation).
public class MachinePeriodSlideViewModel
{
    public string SectorLabel { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string PeriodLabel { get; init; } = "";
    public string FilterLabel { get; init; } = "";
    public bool IsConnected { get; init; }
    public bool BeltRunning { get; init; }
    public SlotKind? PlannedSlot { get; init; }
    public bool IsPlannedStop => PlannedSlot == SlotKind.PlannedStop;
    public bool IsMaintenance => PlannedSlot == SlotKind.Maintenance;
    public string? PlannedLabel => RunStateText.Planned(PlannedSlot);
    // The line's stop in progress, if any (header banner, whatever the period).
    public OngoingStop? OngoingStop { get; init; }
    public string RunStateLabel => PlannedLabel ?? (BeltRunning ? "En marche" : "À l'arrêt");
    public ProductionInfo? CurrentProduction { get; init; }
    public string CurrentProductionLabel { get; init; } = "";
    public KpiSummary Kpis { get; init; } = new();
    public double TrsTarget { get; init; }
    public KpiSeverity TrsSeverity { get; init; }
    public KpiSeverity WeightDeviationSeverity { get; init; }
    public string AvgWeightText => KpiCalculator.FormatAverages(Kpis.Weights);
    public string TargetWeightsText => Kpis.Weights.Count == 0
        ? "Pas de données de poids"
        : "Cible " + string.Join(" · ", Kpis.Weights.Select(w => $"{w.TargetWeight:0} g"));
    public IReadOnlyList<TeamPeriodRow> Teams { get; init; } = Array.Empty<TeamPeriodRow>();

    // "par jour", "par semaine"... (chart titles).
    public string GrainLabel { get; init; } = "";
    public ChartParts TrsChart { get; init; } = ChartParts.Empty();
    public ChartParts SpeedChart { get; init; } = ChartParts.Empty();
    public ChartParts ProductionChart { get; init; } = ChartParts.Empty();
    public ChartParts DeviationChart { get; init; } = ChartParts.Empty();

    // Several weeks of sub-minute snapshots are heavy to re-aggregate at every
    // rotation: ranges over a week are kept CacheDuration per machine.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly Dictionary<(string, DateTime, DateTime), (DateTime LoadedAt, List<ProductionBucket> Lots)> Cache = new();

    public static void ClearCache() => Cache.Clear();

    public static async Task<MachinePeriodSlideViewModel> LoadAsync(
        ProductionRepository repository,
        TeamScheduleService teamSchedule,
        AppSettings settings,
        string sector,
        Machine machine,
        DateTime from,
        DateTime to,
        string periodLabel,
        DashboardFilter filter)
    {
        var now = DateTime.Now;
        var status = await repository.GetLatestStatusAsync(machine.MachineId);
        var allLots = await LoadLotsAsync(repository, machine.MachineId, from, to, now);
        var lots = filter.Apply(allLots, teamSchedule);
        var trsBand = settings.TrsBandFor(machine.MachineId);
        var trsTarget = trsBand.Target;

        var kpis = KpiCalculator.Summarize(lots, KpiCalculator.AvailabilityPercent(lots), trsBand);

        // Teams, best TRS first; teams that did not produce go last.
        var teams = lots
            .GroupBy(teamSchedule.TeamOf)
            .Select(g =>
            {
                var teamLots = g.ToList();
                var k = KpiCalculator.Summarize(teamLots, KpiCalculator.AvailabilityPercent(teamLots), trsBand);
                return new TeamPeriodRow
                {
                    TeamName = g.Key,
                    Kpis = k,
                    TrsSeverity = KpiCalculator.TrsSeverity(k.TrsPercent, trsBand),
                    WeightDeviationSeverity = KpiCalculator.WeightDeviationSeverity(k.WeightDeviationPercent),
                };
            })
            .OrderByDescending(t => t.Kpis.TrsPercent ?? -1)
            .ThenByDescending(t => t.Kpis.TotalBags)
            .ToList();

        var grain = PeriodRanges.GrainFor(from, to);
        var steps = DashboardCharts.Steps(PeriodRanges.Group(lots, from, to, grain, now));

        return new MachinePeriodSlideViewModel
        {
            SectorLabel = Machine.SectorLabel(sector),
            MachineName = machine.MachineName,
            PeriodLabel = periodLabel,
            FilterLabel = filter.Describe(),
            IsConnected = status?.IsConnected ?? false,
            BeltRunning = status?.BeltRunning ?? false,
            PlannedSlot = (await repository.GetActiveSlotKindsAsync()).TryGetValue(machine.MachineId, out var slot) ? slot : null,
            OngoingStop = (await OngoingStopService.GetAsync(repository, settings)).GetValueOrDefault(machine.MachineId),
            CurrentProduction = status?.CurrentProduction,
            CurrentProductionLabel = MachineDaySlideViewModel.FormatProduction(status?.CurrentProduction, allLots),
            Kpis = kpis,
            TrsTarget = trsTarget,
            TrsSeverity = KpiCalculator.TrsSeverity(kpis.TrsPercent, trsBand),
            WeightDeviationSeverity = KpiCalculator.WeightDeviationSeverity(kpis.WeightDeviationPercent),
            Teams = teams,
            GrainLabel = "par " + PeriodRanges.GrainName(grain),
            TrsChart = DashboardCharts.Trs(steps, trsTarget),
            SpeedChart = DashboardCharts.Speed(steps),
            ProductionChart = DashboardCharts.ProductionByWeight(steps),
            DeviationChart = DashboardCharts.Deviation(steps),
        };
    }

    private static async Task<List<ProductionBucket>> LoadLotsAsync(
        ProductionRepository repository, string machineId, DateTime from, DateTime to, DateTime now)
    {
        if ((to - from).TotalDays <= 8)
            return await repository.GetLotsAsync(machineId, from, to);

        var key = (machineId, from, to);
        if (Cache.TryGetValue(key, out var cached) && now - cached.LoadedAt <= CacheDuration)
            return cached.Lots;
        var lots = await repository.GetLotsAsync(machineId, from, to);
        Cache[key] = (now, lots);
        return lots;
    }
}
