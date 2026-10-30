using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels.Slides;

// One production line of a synthesis slide: its totals, then one row per
// weight it produced (TRS, production, average weight, deviation).
public class SynthesisLineRow
{
    public string MachineId { get; init; } = "";
    public string MachineName { get; init; } = "";
    public KpiSummary Kpis { get; init; } = new();
    public double TrsTarget { get; init; }
    public KpiSeverity TrsSeverity { get; init; }
    public KpiSeverity WeightDeviationSeverity { get; init; }
    public string ProductionText => KpiCalculator.FormatMass(Kpis.ProductionKg);
    public string GiveawayText => Kpis.GiveawayKg is { } kg ? KpiCalculator.FormatMass(kg, signed: true) : "—";
    public bool HasWeights => Kpis.Weights.Count > 0;
}

// Synthesis slides, one per sector: the lines of the sector (each with the
// weights it ran) over the current month, the last 3 months or the year,
// then the TRS and weight-deviation trends, one curve per line.
public class SynthesisSlideViewModel
{
    public string SectorLabel { get; init; } = "";
    public SynthesisPeriod Period { get; init; }
    public string PeriodLabel { get; init; } = "";
    public string FilterLabel { get; init; } = "";
    public KpiSummary SectorKpis { get; init; } = new();
    public string SectorProductionText => KpiCalculator.FormatMass(SectorKpis.ProductionKg);
    public IReadOnlyList<SynthesisLineRow> Lines { get; init; } = Array.Empty<SynthesisLineRow>();

    // 1 or 2 columns of line cards, so a 4-line sector still fits the screen.
    public int LineColumns => Lines.Count > 2 ? 2 : 1;

    public string GrainLabel { get; init; } = "";
    public ChartParts TrsChart { get; init; } = ChartParts.Empty();
    public ChartParts DeviationChart { get; init; } = ChartParts.Empty();

    // A year of sub-minute Stiavelli snapshots is heavy to re-aggregate on
    // SQLEXPRESS, and these figures barely move: keep each sector's buckets
    // for CacheDuration. Only the data is cached -- charts are rebuilt every
    // time so they follow the current theme.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
    private static readonly Dictionary<string, (DateTime LoadedAt, List<ProductionBucket> Lots)> Cache = new();

    public static void ClearCache() => Cache.Clear();

    public static async Task<SynthesisSlideViewModel> LoadAsync(
        ProductionRepository repository,
        TeamScheduleService teamSchedule,
        AppSettings settings,
        string sector,
        IReadOnlyList<Machine> machines,
        SynthesisPeriod period,
        DashboardFilter filter)
    {
        var now = DateTime.Now;
        var (from, to, label) = PeriodRanges.Synthesis(period, now);
        // Nothing before DataStartDate is read: start the charts there, and say so.
        if (from < settings.DataStartDate && to > settings.DataStartDate)
        {
            from = settings.DataStartDate.Date;
            label += $" - donnees depuis le {from:dd/MM/yyyy}";
        }

        // Day buckets are enough unless postes / teams are filtered.
        var size = filter.FiltersByPosteOrTeam ? BucketSize.Hour : BucketSize.Day;
        var key = $"{sector}|{period}|{from:yyyyMMdd}|{size}|{string.Join(",", machines.Select(m => m.MachineId))}";
        if (!Cache.TryGetValue(key, out var cached) || now - cached.LoadedAt > CacheDuration)
        {
            cached = (now, await repository.GetLotsForMachinesAsync(machines.Select(m => m.MachineId).ToList(), from, to, size));
            Cache[key] = cached;
        }
        var lots = filter.Apply(cached.Lots, teamSchedule);
        var byMachine = lots.ToLookup(l => l.MachineId);

        var lines = machines.Select(m =>
        {
            var machineLots = byMachine[m.MachineId].ToList();
            var target = settings.TrsTarget(m.MachineId);
            var k = KpiCalculator.Summarize(machineLots, KpiCalculator.AvailabilityPercent(machineLots), target);
            return new SynthesisLineRow
            {
                MachineId = m.MachineId,
                MachineName = m.MachineName,
                Kpis = k,
                TrsTarget = target,
                TrsSeverity = KpiCalculator.TrsSeverity(k.TrsPercent, target),
                WeightDeviationSeverity = KpiCalculator.WeightDeviationSeverity(k.WeightDeviationPercent),
            };
        }).ToList();

        var grain = period switch
        {
            SynthesisPeriod.Month => TimeGrain.Day,
            SynthesisPeriod.ThreeMonths => TimeGrain.Week,
            _ => TimeGrain.Month,
        };
        var labels = new List<string>();
        var trsLines = new List<(string, double?[])>();
        var deviationLines = new List<(string, double?[])>();
        foreach (var m in machines)
        {
            var groups = PeriodRanges.Group(byMachine[m.MachineId], from, to, grain, now);
            if (labels.Count == 0)
                labels = groups.Select(g => g.Label).ToList();
            var perStep = groups.Select(g => KpiCalculator.Summarize(g.Lots, KpiCalculator.AvailabilityPercent(g.Lots))).ToList();
            trsLines.Add((m.MachineName, perStep.Select(k => k.TrsPercent).ToArray()));
            deviationLines.Add((m.MachineName, perStep.Select(k => k.WeightDeviationPercent).ToArray()));
        }

        // Reference line only when every line shares the same TRS target.
        var targets = machines.Select(m => settings.TrsTarget(m.MachineId)).Distinct().ToList();

        return new SynthesisSlideViewModel
        {
            SectorLabel = sector,
            Period = period,
            PeriodLabel = label,
            FilterLabel = filter.Describe(),
            SectorKpis = KpiCalculator.Summarize(lots, KpiCalculator.AvailabilityPercent(lots)),
            Lines = lines,
            GrainLabel = "par " + PeriodRanges.GrainName(grain),
            TrsChart = DashboardCharts.Lines(labels, trsLines, v => $"{v:0}%", targets.Count == 1 ? targets[0] : null),
            DeviationChart = DashboardCharts.Lines(labels, deviationLines, v => $"{v:+0.00;-0.00;0}%", 0),
        };
    }
}
