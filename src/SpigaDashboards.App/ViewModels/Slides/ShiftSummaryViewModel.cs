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
    public KpiTrend? WeightTrend { get; init; }
    // Main weight on its target: the Poids moyen tile lights up gold, no arrow.
    public bool WeightOnTarget => KpiTrend.WeightOnTarget(Kpis);

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
        IReadOnlyList<LotSpan>? lotSpans = null,
        IReadOnlyCollection<ProductionBucket>? earlierLots = null)
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

        // Poste in progress: the same figures TrendWindow ago (earlierLots =
        // the day read up to then), to show whether they are going up or down.
        KpiTrend? trsTrend = null, speedTrend = null, weightTrend = null;
        if (withCharts && isRunning && earlierLots != null)
        {
            var before = earlierLots
                .Where(l => l.ShiftId == shiftId && TeamScheduleService.GetShiftDate(l.StartTime, shiftId) == shiftDate)
                .ToList();
            var earlier = KpiCalculator.Summarize(before, KpiCalculator.AvailabilityPercent(before));
            trsTrend = KpiTrend.Of(kpis.TrsPercent, earlier.TrsPercent, " pt");
            speedTrend = KpiTrend.Of(kpis.SpeedBagsPerMinute, earlier.SpeedBagsPerMinute, "");
            // Weight: the main (biggest) weight of the poste, when it was
            // already the main one 2 min ago. Closer to the target is good
            // (green arrow, up or down), further is bad (red). On the target:
            // no arrow, the tile is lit gold instead (WeightOnTarget).
            var mainWeight = kpis.Weights.FirstOrDefault();
            var earlierWeight = mainWeight == null ? null
                : earlier.Weights.FirstOrDefault(w => Math.Round(w.TargetWeight) == Math.Round(mainWeight.TargetWeight));
            if (!KpiTrend.WeightOnTarget(kpis) && mainWeight?.AvgWeight is { } a && earlierWeight?.AvgWeight is { } b)
            {
                var closer = Math.Sign(Math.Abs(b - mainWeight.TargetWeight) - Math.Abs(a - mainWeight.TargetWeight));
                weightTrend = KpiTrend.Of(a, b, " g", goodDirection: closer);
            }
        }

        return new ShiftSummaryViewModel
        {
            TrsTrend = trsTrend,
            SpeedTrend = speedTrend,
            WeightTrend = weightTrend,
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
}

// Direction of a poste figure over the last TrendWindow, as shown on screen
// (to 0,1): +1 up, -1 down, 0 same figure; Text = "+0,3 pt · 2 min". Good is
// the colour: +1 green, -1 red, 0 grey (TRS and speed: up is good; weight:
// closer to the target is good whichever way it moved).
public sealed record KpiTrend(int Direction, string Text, int Good)
{
    public static readonly TimeSpan TrendWindow = TimeSpan.FromMinutes(2);
    private static readonly System.Globalization.CultureInfo French = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    // "On the target": the main weight's average within this of its target.
    public const double WeightOnTargetGrams = 0.5;

    public static bool WeightOnTarget(KpiSummary kpis)
        => kpis.Weights.FirstOrDefault() is { AvgWeight: { } avg } main
           && Math.Abs(avg - main.TargetWeight) < WeightOnTargetGrams;

    public static KpiTrend? Of(double? now, double? before, string unit, int? goodDirection = null)
    {
        if (now is not { } n || before is not { } b)
            return null;
        var delta = Math.Round(n, 1) - Math.Round(b, 1);
        var minutes = $"{TrendWindow.TotalMinutes:0} min";
        if (Math.Abs(delta) < 0.05)
            return new KpiTrend(0, string.Format(French, "{0:0.0}{1} · {2}", 0.0, unit, minutes), 0);
        var direction = Math.Sign(delta);
        return new KpiTrend(direction, string.Format(French, "{0:+0.0;-0.0}{1} · {2}", delta, unit, minutes), goodDirection ?? direction);
    }
}
