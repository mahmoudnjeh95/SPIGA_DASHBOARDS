using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels;

public enum DashboardPeriod
{
    Day,
    Week,
    Month,
}

public partial class MachineDashboardViewModel : ObservableObject
{
    private readonly ProductionRepository _repository;

    public MachineDashboardViewModel(ProductionRepository repository)
    {
        _repository = repository;
    }

    [ObservableProperty] private string machineId = "";
    [ObservableProperty] private string machineName = "";
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private bool beltRunning;
    [ObservableProperty] private bool alarmActive;
    [ObservableProperty] private KpiSummary kpis = new();
    [ObservableProperty] private DashboardPeriod period = DashboardPeriod.Day;

    // Current Stiavelli production on this line (one article run, since its start).
    [ObservableProperty] private ProductionInfo? currentProduction;
    [ObservableProperty] private string currentProductionLabel = "";

    // On/off timeline: today's belt-running status, hour by hour.
    [ObservableProperty] private ISeries[] onOffSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] onOffXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] onOffYAxes = Array.Empty<Axis>();

    // Weight drift: average recorded weight vs target, per hour (Day) or per day (Week/Month).
    [ObservableProperty] private ISeries[] weightSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] weightXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] weightYAxes = Array.Empty<Axis>();

    // This machine's current-shift team: daily total bags over the last 14 days.
    [ObservableProperty] private ISeries[] teamHistorySeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] teamHistoryXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] teamHistoryYAxes = Array.Empty<Axis>();
    [ObservableProperty] private string currentShiftName = "";

    // Production by shift, for the selected period.
    [ObservableProperty] private ISeries[] shiftSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] shiftXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] shiftYAxes = Array.Empty<Axis>();

    public async Task LoadAsync(Machine machine, DashboardPeriod selectedPeriod)
    {
        MachineId = machine.MachineId;
        MachineName = machine.MachineName;
        Period = selectedPeriod;

        var status = await _repository.GetLatestStatusAsync(machine.MachineId);
        IsConnected = status?.IsConnected ?? false;
        BeltRunning = status?.BeltRunning ?? false;
        AlarmActive = status?.AlarmActive ?? false;
        CurrentProduction = status?.CurrentProduction;
        CurrentProductionLabel = FormatProduction(CurrentProduction);

        var (from, to) = GetRange(selectedPeriod);
        var lots = await _repository.GetLotsAsync(machine.MachineId, from, to);

        // Disponibilite input for TRS: fraction of this period's snapshot
        // intervals in which the line produced (separate from BuildOnOffChartAsync's
        // chart, which always shows today regardless of the selected period).
        Kpis = KpiCalculator.Summarize(lots, KpiCalculator.AvailabilityPercent(lots));

        var buckets = BucketLots(lots, selectedPeriod);
        BuildWeightChart(buckets);
        BuildShiftChart(KpiCalculator.ByShift(lots));

        await BuildOnOffChartAsync(machine.MachineId);
        await BuildTeamHistoryChartAsync(machine.MachineId);
    }

    private static string FormatProduction(ProductionInfo? production)
    {
        if (production == null)
            return "Aucune production enregistree ces 6 dernieres heures";

        var state = production.IsTerminated ? "Production terminee" : "Production en cours";
        var weight = production.AvgWeight is { } avg ? $" - moyen {avg:0.0} g / cible {production.TargetNetWeight:0} g" : "";
        return $"{state} : {production.ProductName} depuis le {production.StartTime:dd/MM HH:mm} - " +
               $"{production.TotalBags:N0} sacs, {production.QualityPercent:0.0} % conformes{weight}";
    }

    private sealed record ChartBucket(string Label, double? AvgWeight, double TargetWeight);

    // Repository returns hour buckets: one point per hour for "Aujourd'hui",
    // regrouped per day for week/month so the charts stay readable.
    private static List<ChartBucket> BucketLots(List<ProductionBucket> lots, DashboardPeriod period)
    {
        var byDay = period != DashboardPeriod.Day;
        return lots
            .GroupBy(l => byDay ? l.StartTime.Date : l.StartTime.Date.AddHours(l.StartTime.Hour))
            .OrderBy(g => g.Key)
            .Select(g => new ChartBucket(
                g.Key.ToString(byDay ? "dd" : "HH"),
                KpiCalculator.WeightedAverageWeight(g) is { } avg ? Math.Round(avg, 1) : null,
                g.Max(l => l.TargetNetWeight)))
            .ToList();
    }

    private void BuildWeightChart(List<ChartBucket> buckets)
    {
        // Buckets without trustworthy weight data are skipped, not drawn as 0 g.
        var weighed = buckets.Where(b => b.AvgWeight.HasValue).ToList();
        WeightSeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = weighed.Select(b => b.AvgWeight!.Value).ToArray(),
                Name = "Poids moyen",
                Stroke = new SolidColorPaint(new SKColor(0x2E, 0xCC, 0x71), 3),
                Fill = null,
                GeometrySize = 10,
            },
            new LineSeries<double>
            {
                Values = weighed.Select(b => b.TargetWeight).ToArray(),
                Name = "Cible",
                Stroke = new SolidColorPaint(new SKColor(0x9A, 0xA4, 0xB2), 2),
                Fill = null,
                GeometrySize = 0,
            },
        };
        WeightXAxes = new[] { ChartHelpers.BuildXAxis(weighed.Select(b => b.Label)) };
        // Fixed range around the data with 3 labels: auto-scaling packed 5+
        // labels into the short tile.
        var weights = weighed.SelectMany(b => new[] { b.AvgWeight!.Value, b.TargetWeight }).DefaultIfEmpty(0).ToList();
        var pad = Math.Max(5, (weights.Max() - weights.Min()) * 0.2);
        var low = Math.Max(0, Math.Floor(weights.Min() - pad));
        var high = Math.Ceiling(weights.Max() + pad);
        WeightYAxes = new[] { ChartHelpers.BuildYAxis(low, high, minStep: (high - low) / 2, labeler: v => $"{v:0} g") };
    }

    private void BuildShiftChart(List<ShiftKpi> shiftBreakdown)
    {
        ShiftSeries = new ISeries[] { BuildColumnSeries(shiftBreakdown.Select(s => s.TotalBags)) };
        ShiftXAxes = new[] { ChartHelpers.BuildXAxis(shiftBreakdown.Select(s => s.ShiftName)) };
        ShiftYAxes = new[] { BarValueAxis(shiftBreakdown.Select(s => s.TotalBags)) };
    }

    private async Task BuildOnOffChartAsync(string machineId)
    {
        var today = DateTime.Now.Date;
        var hours = await _repository.GetLotsAsync(machineId, today, today.AddDays(1));

        // One column per hour: share of the hour the line was producing
        // (1 = ran the whole observed hour, 0 = stopped).
        OnOffSeries = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Values = hours.Select(h => h.ObservedMinutes > 0 ? Math.Round(h.RunMinutes / h.ObservedMinutes, 2) : 0).ToArray(),
                Name = "Bande en marche",
                Fill = new SolidColorPaint(new SKColor(0x2E, 0xCC, 0x71)),
                Padding = 2,
                MaxBarWidth = 40,
            },
        };
        OnOffXAxes = new[] { ChartHelpers.BuildXAxis(hours.Select(h => h.StartTime.ToString("HH"))) };
        OnOffYAxes = new[] { ChartHelpers.BuildYAxis(minLimit: 0, maxLimit: 1, minStep: 0.5, labeler: v => $"{v:P0}") };
    }

    private async Task BuildTeamHistoryChartAsync(string machineId)
    {
        var currentShiftId = KpiCalculator.CurrentShiftId(DateTime.Now);
        CurrentShiftName = KpiCalculator.ShiftName(currentShiftId);

        var today = DateTime.Now.Date;
        var windowStart = today.AddDays(-13);
        var lots = await _repository.GetLotsAsync(machineId, windowStart, today.AddDays(1));

        var daily = KpiCalculator.ByDayForShift(lots, currentShiftId);

        TeamHistorySeries = new ISeries[] { BuildColumnSeries(daily.Select(d => d.TotalBags)) };
        // Day number only: 14 "dd/MM" labels don't fit side by side at TV text size.
        TeamHistoryXAxes = new[] { ChartHelpers.BuildXAxis(daily.Select(d => d.Date.ToString("dd"))) };
        TeamHistoryYAxes = new[] { BarValueAxis(daily.Select(d => d.TotalBags)) };
    }

    // Bar charts print each value on its bar, so their value axis is hidden
    // (its labels only crowded the short TV tiles); the max leaves room above
    // the tallest bar so its label isn't clipped.
    private static Axis BarValueAxis(IEnumerable<long> values)
    {
        var max = values.DefaultIfEmpty(0).Max();
        var axis = ChartHelpers.BuildYAxis(minLimit: 0, maxLimit: max > 0 ? max * 1.35 : null);
        axis.IsVisible = false;
        return axis;
    }

    private static ColumnSeries<long> BuildColumnSeries(IEnumerable<long> values) => new()
    {
        Values = values.ToArray(),
        Name = "Sacs",
        Fill = new SolidColorPaint(new SKColor(0x3B, 0x82, 0xF6)),
        DataLabelsPaint = new SolidColorPaint(ThemeService.ChartTextColor),
        DataLabelsPosition = DataLabelsPosition.Top,
        DataLabelsSize = ChartHelpers.DataLabelTextSize,
        DataLabelsFormatter = point => ChartHelpers.Compact(point.Coordinate.PrimaryValue),
    };

    private static (DateTime from, DateTime to) GetRange(DashboardPeriod period)
    {
        var now = DateTime.Now;
        var today = now.Date;
        return period switch
        {
            DashboardPeriod.Day => (today, today.AddDays(1)),
            DashboardPeriod.Week => (today.AddDays(-(int)today.DayOfWeek + (today.DayOfWeek == DayOfWeek.Sunday ? -6 : 1)), today.AddDays(1)),
            DashboardPeriod.Month => (new DateTime(today.Year, today.Month, 1), today.AddDays(1)),
            _ => (today, today.AddDays(1)),
        };
    }
}
