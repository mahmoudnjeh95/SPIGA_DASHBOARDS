using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels.Slides;

// One machine, one poste, one team: the KPIs (TRS included) are computed from
// that poste's hours only. The main poste of the slide also carries the
// charts and the per-weight table; the other postes of the same shift day
// are shown as compact summaries.
public class ShiftSummaryViewModel
{
    public string ShiftName { get; init; } = "";
    public string TimeRange { get; init; } = "";
    public string TeamName { get; init; } = "";
    public bool IsRunning { get; init; }
    public string StateLabel => IsRunning ? "EN COURS" : "TERMINÉ";
    public KpiSummary Kpis { get; init; } = new();
    public double TrsTarget { get; init; }
    public KpiSeverity TrsSeverity { get; init; }
    public KpiSeverity WeightDeviationSeverity { get; init; }

    // "Poids moyen" replaces the bag count: one value per target weight.
    public string AvgWeightText => KpiCalculator.FormatAverages(Kpis.Weights);
    public string TargetWeightsText => Kpis.Weights.Count == 0
        ? "Pas de données de poids"
        : "Cible " + string.Join(" · ", Kpis.Weights.Select(w => $"{w.TargetWeight:0} g"));
    // Net weight produced by the team over the poste, all weights together.
    public string ProductionText => KpiCalculator.FormatMass(Kpis.ProductionKg);

    // Poste in progress: is TRS / speed going up or down? (KpiRow arrow)
    public KpiTrend? TrsTrend { get; init; }
    public KpiTrend? SpeedTrend { get; init; }

    // Main poste only: per hour of the poste, up to now.
    public ChartParts OnOffChart { get; init; } = ChartParts.Empty();
    public ChartParts SpeedChart { get; init; } = ChartParts.Empty();
    public ChartParts WeightChart { get; init; } = ChartParts.Empty();

    public static ShiftSummaryViewModel Build(
        int shiftId,
        DateTime shiftDate,
        bool withCharts,
        bool isRunning,
        IReadOnlyCollection<ProductionBucket> dayLots,
        TeamScheduleService teamSchedule,
        DateTime now,
        TrsBand trsBand,
        WeightBand weightBand,
        IReadOnlyList<LotSpan>? lotSpans = null)
    {
        // A Nuit bucket after midnight belongs to the shift date it started on.
        var lots = dayLots
            .Where(l => l.ShiftId == shiftId && TeamScheduleService.GetShiftDate(l.StartTime, shiftId) == shiftDate)
            .ToList();

        var kpis = KpiCalculator.Summarize(lots, KpiCalculator.AvailabilityPercent(lots), trsBand, weightBand);
        var team = teamSchedule.GetTeamForShift(shiftDate, shiftId);

        ChartParts onOff = ChartParts.Empty(), speed = ChartParts.Empty(), weight = ChartParts.Empty();
        if (withCharts)
        {
            var start = shiftDate.AddHours(KpiCalculator.ShiftStartHour(shiftId));
            var byHour = lots.ToLookup(l => l.StartTime);
            var steps = Enumerable.Range(0, 8)
                .Select(i => start.AddHours(i))
                .Where(h => h <= now)
                .Select(h => new DashboardCharts.Step(h.ToString("HH"), byHour[h].ToList()))
                .ToList();
            onOff = DashboardCharts.OnOff(steps);
            speed = DashboardCharts.Speed(steps);
            // Lots drawn over their real time span when the line has some in
            // the poste; otherwise the hourly chart (running average fallback).
            var end = start.AddHours(8);
            weight = lotSpans is { Count: > 0 }
                ? DashboardCharts.WeightLots(lotSpans, start, now < end ? now : end, live: now < end)
                : DashboardCharts.Weight(steps);
        }

        var (trsTrend, speedTrend) = withCharts && isRunning ? Trends(lots, kpis) : (null, null);

        return new ShiftSummaryViewModel
        {
            TrsTrend = trsTrend,
            SpeedTrend = speedTrend,
            ShiftName = KpiCalculator.ShiftName(shiftId),
            TimeRange = KpiCalculator.ShiftTimeRange(shiftId),
            TeamName = string.IsNullOrEmpty(team) ? TeamScheduleService.NoTeam : team,
            IsRunning = isRunning,
            Kpis = kpis,
            TrsTarget = trsBand.Target,
            TrsSeverity = KpiCalculator.TrsSeverity(kpis.TrsPercent, trsBand),
            WeightDeviationSeverity = KpiCalculator.WeightDeviationSeverity(kpis.WeightDeviationPercent, weightBand),
            OnOffChart = onOff,
            SpeedChart = speed,
            WeightChart = weight,
        };
    }

    // The poste's figures are cumulative: they rise while the last hour does
    // better than the poste so far, and fall while it does worse. Last hour =
    // the latest hourly buckets covering at least 45 observed minutes; no
    // trend before the poste has 30 more minutes than that to compare with.
    private const double RecentMinutes = 45;
    private const double TrsStep = 2;    // points
    private const double SpeedStep = 1;  // bags / minute

    private static (KpiTrend? Trs, KpiTrend? Speed) Trends(IReadOnlyCollection<ProductionBucket> lots, KpiSummary poste)
    {
        var recent = new List<ProductionBucket>();
        double minutes = 0;
        foreach (var hour in lots.GroupBy(l => l.StartTime).OrderByDescending(g => g.Key))
        {
            recent.AddRange(hour);
            minutes += hour.Sum(l => l.ObservedMinutes);
            if (minutes >= RecentMinutes)
                break;
        }
        if (minutes < RecentMinutes || lots.Sum(l => l.ObservedMinutes) - minutes < 30)
            return (null, null);

        var recentTrs = KpiCalculator.Summarize(recent, KpiCalculator.AvailabilityPercent(recent)).TrsPercent;
        var recentSpeed = KpiCalculator.AverageSpeed(recent);
        return (KpiTrend.Of(recentTrs, poste.TrsPercent, TrsStep, "{0:0.0} %"),
                KpiTrend.Of(recentSpeed, poste.SpeedBagsPerMinute, SpeedStep, "{0:0.0}"));
    }
}

// Direction of a poste figure (+1 rising, -1 falling, 0 steady) and the last
// hour's own value, e.g. "dern. heure 61,0 %".
public sealed record KpiTrend(int Direction, string Text)
{
    private static readonly System.Globalization.CultureInfo French = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    public static KpiTrend? Of(double? recent, double? overall, double step, string format)
    {
        if (recent is not { } r || overall is not { } o)
            return null;
        var direction = r - o >= step ? 1 : o - r >= step ? -1 : 0;
        return new KpiTrend(direction, "dern. heure " + string.Format(French, format, r));
    }
}
