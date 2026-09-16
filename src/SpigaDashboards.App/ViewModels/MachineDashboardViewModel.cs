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

    // On/off timeline: today's belt-running status, hour by hour.
    [ObservableProperty] private ISeries[] onOffSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] onOffXAxes = Array.Empty<Axis>();

    // Speed trend: bags/min per lot across the selected period.
    [ObservableProperty] private ISeries[] speedSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] speedXAxes = Array.Empty<Axis>();

    // Weight drift: average recorded weight vs target, per lot.
    [ObservableProperty] private ISeries[] weightSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] weightXAxes = Array.Empty<Axis>();

    // TRS (OEE) breakdown -- Disponibilite / Performance / Qualite, horizontal bars.
    [ObservableProperty] private ISeries[] trsBreakdownSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] trsBreakdownYAxes = Array.Empty<Axis>();

    // This machine's current-shift team: daily total bags over the last 14 days.
    [ObservableProperty] private ISeries[] teamHistorySeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] teamHistoryXAxes = Array.Empty<Axis>();
    [ObservableProperty] private string currentShiftName = "";

    // Production by shift, for the selected period.
    [ObservableProperty] private ISeries[] shiftSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] shiftXAxes = Array.Empty<Axis>();

    public async Task LoadAsync(Machine machine, DashboardPeriod selectedPeriod)
    {
        MachineId = machine.MachineId;
        MachineName = machine.MachineName;
        Period = selectedPeriod;

        var status = await _repository.GetLatestStatusAsync(machine.MachineId);
        IsConnected = status?.IsConnected ?? false;
        BeltRunning = status?.BeltRunning ?? false;
        AlarmActive = status?.AlarmActive ?? false;

        var (from, to) = GetRange(selectedPeriod);
        var lots = await _repository.GetLotsAsync(machine.MachineId, from, to);

        // Disponibilite input for TRS: fraction of this period's status snapshots
        // where the belt was running (separate from BuildOnOffChartAsync's chart,
        // which always shows today regardless of the selected period).
        var periodStatusHistory = await _repository.GetStatusHistoryAsync(machine.MachineId, from, to);
        var availabilityPercent = periodStatusHistory.Count > 0
            ? 100.0 * periodStatusHistory.Count(h => h.BeltRunning) / periodStatusHistory.Count
            : 0;

        Kpis = KpiCalculator.Summarize(lots, availabilityPercent);

        BuildSpeedChart(lots);
        BuildWeightChart(lots);
        BuildTrsBreakdownChart(Kpis);
        BuildShiftChart(KpiCalculator.ByShift(lots));

        await BuildOnOffChartAsync(machine.MachineId);
        await BuildTeamHistoryChartAsync(machine.MachineId);
    }

    private void BuildSpeedChart(List<ProductionLot> lots)
    {
        var points = lots
            .Where(l => l.DurationMinutes > 0)
            .Select(l => new { l.StartTime, Speed = Math.Round(l.TotalBags / l.DurationMinutes, 1) })
            .ToList();

        SpeedSeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = points.Select(p => p.Speed).ToArray(),
                Name = "Sacs/min",
                Stroke = new SolidColorPaint(new SKColor(0x3B, 0x82, 0xF6), 3),
                Fill = null,
                GeometrySize = 6,
                GeometryStroke = new SolidColorPaint(new SKColor(0x3B, 0x82, 0xF6), 2),
            },
        };
        SpeedXAxes = new[] { ChartHelpers.BuildXAxis(points.Select(p => p.StartTime.ToString("HH:mm"))) };
    }

    private void BuildWeightChart(List<ProductionLot> lots)
    {
        WeightSeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = lots.Select(l => l.AvgWeight).ToArray(),
                Name = "Poids moyen",
                Stroke = new SolidColorPaint(new SKColor(0x2E, 0xCC, 0x71), 3),
                Fill = null,
                GeometrySize = 6,
            },
            new LineSeries<double>
            {
                Values = lots.Select(l => l.TargetNetWeight).ToArray(),
                Name = "Cible",
                Stroke = new SolidColorPaint(new SKColor(0x9A, 0xA4, 0xB2), 2),
                Fill = null,
                GeometrySize = 0,
            },
        };
        WeightXAxes = new[] { ChartHelpers.BuildXAxis(lots.Select(l => l.StartTime.ToString("HH:mm"))) };
    }

    private void BuildTrsBreakdownChart(KpiSummary kpis)
    {
        TrsBreakdownSeries = new ISeries[]
        {
            new RowSeries<double>
            {
                Values = new[] { kpis.AvailabilityPercent, kpis.PerformancePercent, kpis.QualityPercent },
                Fill = new SolidColorPaint(new SKColor(0x3B, 0x82, 0xF6)),
                DataLabelsPaint = new SolidColorPaint(ThemeService.ChartTextColor),
                DataLabelsPosition = DataLabelsPosition.End,
                DataLabelsFormatter = point => $"{point.Coordinate.PrimaryValue:0}%",
                MaxBarWidth = 22,
            },
        };
        TrsBreakdownYAxes = new[]
        {
            new Axis
            {
                Labels = new[] { "Disponibilite", "Performance", "Qualite" },
                LabelsPaint = new SolidColorPaint(ThemeService.ChartTextColor),
                TextSize = 12,
            },
        };
    }

    private void BuildShiftChart(List<ShiftKpi> shiftBreakdown)
    {
        ShiftSeries = new ISeries[] { BuildColumnSeries(shiftBreakdown.Select(s => s.TotalBags)) };
        ShiftXAxes = new[] { ChartHelpers.BuildXAxis(shiftBreakdown.Select(s => s.ShiftName)) };
    }

    private async Task BuildOnOffChartAsync(string machineId)
    {
        var today = DateTime.Now.Date;
        var history = await _repository.GetStatusHistoryAsync(machineId, today, today.AddDays(1));

        OnOffSeries = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Values = history.Select(h => h.BeltRunning ? 1.0 : 0.0).ToArray(),
                Name = "Bande en marche",
                Fill = new SolidColorPaint(new SKColor(0x2E, 0xCC, 0x71)),
                Padding = 1,
                MaxBarWidth = 20,
            },
        };
        OnOffXAxes = new[] { ChartHelpers.BuildXAxis(history.Select(h => h.Timestamp.ToString("HH"))) };
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
        TeamHistoryXAxes = new[] { ChartHelpers.BuildXAxis(daily.Select(d => d.Date.ToString("dd/MM"))) };
    }

    private static ColumnSeries<int> BuildColumnSeries(IEnumerable<int> values) => new()
    {
        Values = values.ToArray(),
        Name = "Sacs",
        Fill = new SolidColorPaint(new SKColor(0x3B, 0x82, 0xF6)),
        DataLabelsPaint = new SolidColorPaint(ThemeService.ChartTextColor),
        DataLabelsPosition = DataLabelsPosition.Top,
        DataLabelsSize = 13,
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
