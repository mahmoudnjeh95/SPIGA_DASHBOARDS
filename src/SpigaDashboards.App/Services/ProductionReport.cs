using SpigaDashboards.App.Models;
using SpigaDashboards.App.ViewModels;

namespace SpigaDashboards.App.Services;

public sealed record MachineReportRow(string MachineId, string MachineName, string Sector, double TrsTarget, KpiSummary Kpis);

public sealed record WeightReportRow(string MachineId, string MachineName, WeightKpi Weight, double TareGrams, double NominalRate);

public sealed record GroupReportRow(string Label, KpiSummary Kpis);

// Everything an export shows (history browser -> Excel / PDF): the period
// and filters, the totals, the breakdowns per machine / weight / team / time
// step, and the hour-by-hour detail. Built once, rendered by ExportService.
public sealed class ProductionReport
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string FilterLabel { get; init; } = "";
    public DateTime GeneratedAt { get; init; } = DateTime.Now;
    public KpiSummary Summary { get; init; } = new();

    // Average of the machines' TRS targets (they can differ per machine).
    public double TrsTarget { get; init; }
    public IReadOnlyList<MachineReportRow> Machines { get; init; } = Array.Empty<MachineReportRow>();
    public IReadOnlyList<WeightReportRow> Weights { get; init; } = Array.Empty<WeightReportRow>();
    public IReadOnlyList<GroupReportRow> Teams { get; init; } = Array.Empty<GroupReportRow>();
    public TimeGrain Grain { get; init; }
    public IReadOnlyList<GroupReportRow> Steps { get; init; } = Array.Empty<GroupReportRow>();
    public IReadOnlyList<DashboardCharts.Step> ChartSteps { get; init; } = Array.Empty<DashboardCharts.Step>();
    public IReadOnlyList<HistoryRow> Detail { get; init; } = Array.Empty<HistoryRow>();

    public string PeriodText => $"du {From:dd/MM/yyyy} au {To.AddDays(-1):dd/MM/yyyy}";

    public static ProductionReport Build(
        IReadOnlyCollection<ProductionBucket> lots,
        IReadOnlyCollection<Machine> machines,
        IReadOnlyList<HistoryRow> detail,
        TeamScheduleService teams,
        AppSettings settings,
        DateTime from,
        DateTime to,
        string filterLabel)
    {
        var byMachine = lots.ToLookup(l => l.MachineId);
        var machineRows = machines
            .Where(m => byMachine[m.MachineId].Any())
            .Select(m =>
            {
                var machineLots = byMachine[m.MachineId].ToList();
                var band = settings.TrsBandFor(m.MachineId);
                return new MachineReportRow(m.MachineId, m.MachineName, m.Sector, band.Target,
                    KpiCalculator.Summarize(machineLots, KpiCalculator.AvailabilityPercent(machineLots), band,
                        settings.WeightBandFor(m.MachineId)));
            })
            .ToList();

        var weightRows = machineRows
            .SelectMany(m => m.Kpis.Weights.Select(w => new WeightReportRow(m.MachineId, m.MachineName, w,
                settings.FindTare(m.MachineId, w.TargetWeight), settings.FindNominalRate(m.MachineId, w.TargetWeight))))
            .ToList();

        var teamRows = lots.GroupBy(teams.TeamOf)
            .OrderBy(g => g.Key)
            .Select(g => new GroupReportRow(g.Key, KpiCalculator.Summarize(g.ToList(), KpiCalculator.AvailabilityPercent(g.ToList()))))
            .ToList();

        var grain = PeriodRanges.GrainFor(from, to);
        // Hour steps don't read well over a single exported day: days at least.
        if (grain == TimeGrain.Hour)
            grain = TimeGrain.Day;
        var groups = PeriodRanges.Group(lots, from, to, grain, DateTime.Now);
        var stepRows = groups
            .Select(g => new GroupReportRow(StepLabel(g.Key, grain), KpiCalculator.Summarize(g.Lots, KpiCalculator.AvailabilityPercent(g.Lots))))
            .ToList();

        var trsBand = TrsBand.Average(machineRows.Select(m => settings.TrsBandFor(m.MachineId)).ToList());
        var trsTarget = trsBand.Target;
        return new ProductionReport
        {
            From = from,
            To = to,
            FilterLabel = filterLabel,
            Summary = KpiCalculator.Summarize(lots, KpiCalculator.AvailabilityPercent(lots), trsBand),
            TrsTarget = trsTarget,
            Machines = machineRows,
            Weights = weightRows,
            Teams = teamRows,
            Grain = grain,
            Steps = stepRows,
            ChartSteps = DashboardCharts.Steps(groups),
            Detail = detail,
        };
    }

    private static string StepLabel(DateTime key, TimeGrain grain) => grain switch
    {
        TimeGrain.Week => $"Semaine du {key:dd/MM/yyyy}",
        TimeGrain.Month => key.ToString("MM/yyyy"),
        _ => key.ToString("dd/MM/yyyy"),
    };
}
