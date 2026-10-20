using System.Globalization;
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
    private readonly TeamScheduleService _teamSchedule;

    public MachineDashboardViewModel(ProductionRepository repository, TeamScheduleService teamSchedule)
    {
        _repository = repository;
        _teamSchedule = teamSchedule;
    }

    [ObservableProperty] private string machineId = "";
    [ObservableProperty] private string machineName = "";
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private bool beltRunning;
    [ObservableProperty] private bool alarmActive;
    [ObservableProperty] private KpiSummary kpis = new();
    [ObservableProperty] private DashboardPeriod period = DashboardPeriod.Day;

    // Caption of the KPI tiles / period charts (they follow the period selector).
    [ObservableProperty] private string periodLabel = "";

    // Ecart de poids colour band, relative to the target (see KpiCalculator).
    [ObservableProperty] private KpiSeverity weightDeviationSeverity;

    // Current Stiavelli production on this line (one article run, since its start).
    [ObservableProperty] private ProductionInfo? currentProduction;
    [ObservableProperty] private string currentProductionLabel = "";

    // On/off timeline: today's belt-running status, hour by hour.
    [ObservableProperty] private ISeries[] onOffSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] onOffXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] onOffYAxes = Array.Empty<Axis>();

    // Weight drift: average recorded weight vs target, per hour (Day) or per day (Week/Month).
    // HasWeightData = false shows a "Pas de donnees" placeholder instead of an empty chart.
    [ObservableProperty] private ISeries[] weightSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] weightXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] weightYAxes = Array.Empty<Axis>();
    [ObservableProperty] private bool hasWeightData;

    // This machine's current-shift team: daily total bags over the last 14 days.
    [ObservableProperty] private ISeries[] teamHistorySeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] teamHistoryXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] teamHistoryYAxes = Array.Empty<Axis>();
    [ObservableProperty] private string currentShiftName = "";

    // Production by team (roster in Parametres), for the selected period.
    [ObservableProperty] private ISeries[] teamSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] teamXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] teamYAxes = Array.Empty<Axis>();

    public async Task LoadAsync(Machine machine, DashboardPeriod selectedPeriod)
    {
        MachineId = machine.MachineId;
        MachineName = machine.MachineName;
        Period = selectedPeriod;
        PeriodLabel = selectedPeriod switch
        {
            DashboardPeriod.Week => "Cette semaine",
            DashboardPeriod.Month => "Ce mois",
            _ => "Aujourd'hui",
        };

        var status = await _repository.GetLatestStatusAsync(machine.MachineId);
        IsConnected = status?.IsConnected ?? false;
        BeltRunning = status?.BeltRunning ?? false;
        AlarmActive = status?.AlarmActive ?? false;
        CurrentProduction = status?.CurrentProduction;

        var (from, to) = GetRange(selectedPeriod);
        var lots = await _repository.GetLotsAsync(machine.MachineId, from, to);

        // Disponibilite input for TRS: fraction of this period's snapshot
        // intervals in which the line produced (separate from the on/off
        // chart, which always shows today regardless of the selected period).
        Kpis = KpiCalculator.Summarize(lots, KpiCalculator.AvailabilityPercent(lots));
        WeightDeviationSeverity = KpiCalculator.WeightDeviationSeverity(Kpis.WeightDeviationPercent);
        CurrentProductionLabel = FormatProduction(CurrentProduction, lots);

        var buckets = BucketLots(lots, selectedPeriod);
        BuildWeightChart(buckets);
        BuildTeamChart(KpiCalculator.ByTeam(lots, _teamSchedule));

        // "Aujourd'hui" already loaded today's hours: no second read.
        var today = DateTime.Now.Date;
        var todayLots = selectedPeriod == DashboardPeriod.Day
            ? lots
            : await _repository.GetLotsAsync(machine.MachineId, today, today.AddDays(1), includeWeights: false);
        BuildOnOffChart(todayLots);
        await BuildTeamHistoryChartAsync(machine.MachineId);
    }

    // Same French number format as the tiles (WPF bindings use fr-FR, see App).
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private static string FormatProduction(ProductionInfo? production, IReadOnlyCollection<ProductionBucket> lots)
    {
        if (production == null)
            return "Aucune donnee recue de StatsViewer depuis 6 h";

        var state = production.IsTerminated ? "Production terminee" : "Production en cours";
        // Lots of this production in the loaded period first (real lot
        // averages); otherwise Stiavelli's running average, if it looks sane.
        var avgWeight = KpiCalculator.ProductionAverageWeight(lots, production.ProductionId) ?? production.AvgWeight;
        var weight = avgWeight is { } avg ? string.Create(French, $" - moyen {avg:0.0} g / cible {production.TargetNetWeight:0} g") : "";
        var quality = production.TotalBags > 0 ? string.Create(French, $"{production.QualityPercent:0.0} %") : "— %";
        return string.Create(French, $"{state} : {production.ProductName} depuis le {production.StartTime:dd/MM/yyyy HH:mm} - ") +
               string.Create(French, $"{production.TotalBags:N0} sacs, {quality} conformes{weight}");
    }

    private sealed record ChartBucket(string Label, double? AvgWeight, double TargetWeight);

    // Repository returns hour buckets: one point per hour for "Aujourd'hui",
    // regrouped per day for week/month so the charts stay readable. The
    // target is bags-weighted like the average, so a changeover inside a
    // bucket compares each bag with its own target.
    private static List<ChartBucket> BucketLots(List<ProductionBucket> lots, DashboardPeriod period)
    {
        var byDay = period != DashboardPeriod.Day;
        return lots
            .GroupBy(l => byDay ? l.StartTime.Date : l.StartTime.Date.AddHours(l.StartTime.Hour))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var bags = g.Sum(l => l.WeighedBags);
                return new ChartBucket(
                    g.Key.ToString(byDay ? "dd" : "HH"),
                    bags > 0 ? Math.Round(g.Sum(l => l.WeighedWeightSum) / bags, 1) : null,
                    bags > 0 ? Math.Round(g.Sum(l => l.WeighedTargetSum) / bags, 1) : 0);
            })
            .ToList();
    }

    private void BuildWeightChart(List<ChartBucket> buckets)
    {
        // Buckets without trustworthy weight data are skipped, not drawn as 0 g.
        var weighed = buckets.Where(b => b.AvgWeight.HasValue).ToList();
        HasWeightData = weighed.Count > 0;
        if (!HasWeightData)
        {
            // No fake 0-5 g axis: the view shows "Pas de donnees" instead.
            WeightSeries = Array.Empty<ISeries>();
            WeightXAxes = new[] { ChartHelpers.BuildXAxis(Array.Empty<string>()) };
            WeightYAxes = new[] { ChartHelpers.BuildYAxis() };
            return;
        }

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
        var weights = weighed.SelectMany(b => new[] { b.AvgWeight!.Value, b.TargetWeight }).ToList();
        var pad = Math.Max(5, (weights.Max() - weights.Min()) * 0.2);
        var low = Math.Max(0, Math.Floor(weights.Min() - pad));
        var high = Math.Ceiling(weights.Max() + pad);
        WeightYAxes = new[] { ChartHelpers.BuildYAxis(low, high, minStep: (high - low) / 2, labeler: v => $"{v:0} g") };
    }

    private void BuildTeamChart(List<TeamKpi> teamBreakdown)
    {
        TeamSeries = new ISeries[] { BuildColumnSeries(teamBreakdown.Select(t => t.TotalBags)) };
        TeamXAxes = new[] { ChartHelpers.BuildXAxis(teamBreakdown.Select(t => t.TeamName)) };
        TeamYAxes = new[] { BarValueAxis(teamBreakdown.Select(t => t.TotalBags)) };
    }

    private void BuildOnOffChart(IReadOnlyCollection<ProductionBucket> todayLots)
    {
        // One column per hour from 00 to the current hour: share of the hour
        // the line was producing (1 = ran the whole observed hour, 0 = stopped
        // or no snapshot at all).
        var now = DateTime.Now;
        var hours = Enumerable.Range(0, now.Hour + 1).Select(h => now.Date.AddHours(h)).ToList();
        var byHour = todayLots
            .GroupBy(l => l.StartTime)
            .ToDictionary(g => g.Key, g => (Observed: g.Sum(l => l.ObservedMinutes), Run: g.Sum(l => l.RunMinutes)));

        OnOffSeries = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Values = hours.Select(h => byHour.TryGetValue(h, out var v) && v.Observed > 0 ? Math.Round(v.Run / v.Observed, 2) : 0).ToArray(),
                Name = "Bande en marche",
                Fill = new SolidColorPaint(new SKColor(0x2E, 0xCC, 0x71)),
                Padding = 2,
                MaxBarWidth = 40,
            },
        };
        OnOffXAxes = new[] { ChartHelpers.BuildXAxis(hours.Select(h => h.ToString("HH"))) };
        OnOffYAxes = new[] { ChartHelpers.BuildYAxis(minLimit: 0, maxLimit: 1, minStep: 0.5, labeler: v => $"{v:P0}") };
    }

    private async Task BuildTeamHistoryChartAsync(string machineId)
    {
        var now = DateTime.Now;
        var currentShiftId = KpiCalculator.CurrentShiftId(now);
        CurrentShiftName = KpiCalculator.ShiftName(currentShiftId);

        // 14 shift dates ending with the current poste's (a Nuit after
        // midnight belongs to yesterday), days without production at 0.
        // Reading from the first date's 00:00 also catches the tail of the
        // Nuit before it: dropped here rather than drawn as a partial bar.
        var lastDate = TeamScheduleService.GetShiftDate(now, currentShiftId);
        var dates = Enumerable.Range(0, 14).Select(i => lastDate.AddDays(i - 13)).ToList();
        var lots = await _repository.GetLotsAsync(machineId, dates[0], now.Date.AddDays(1), includeWeights: false);

        var byDate = KpiCalculator.ByDayForShift(lots, currentShiftId).ToDictionary(d => d.Date, d => d.TotalBags);
        var daily = dates.Select(d => byDate.GetValueOrDefault(d)).ToList();

        TeamHistorySeries = new ISeries[] { BuildColumnSeries(daily) };
        // Day number only: 14 "dd/MM" labels don't fit side by side at TV text size.
        TeamHistoryXAxes = new[] { ChartHelpers.BuildXAxis(dates.Select(d => d.ToString("dd"))) };
        TeamHistoryYAxes = new[] { BarValueAxis(daily) };
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
