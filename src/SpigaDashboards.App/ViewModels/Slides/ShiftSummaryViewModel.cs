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

        return new ShiftSummaryViewModel
        {
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
