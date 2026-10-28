using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Services;

public static class KpiCalculator
{
    private static readonly Dictionary<int, string> ShiftNames = new()
    {
        { 1, "Matin" },
        { 2, "Soir" },
        { 3, "Nuit" },
    };

    public static string ShiftName(int shiftId) => ShiftNames.GetValueOrDefault(shiftId, $"Shift {shiftId}");

    public static int ShiftStartHour(int shiftId) => shiftId switch
    {
        1 => 6,
        2 => 14,
        _ => 22,
    };

    public static string ShiftTimeRange(int shiftId) => shiftId switch
    {
        1 => "06h - 14h",
        2 => "14h - 22h",
        _ => "22h - 06h",
    };

    // Postes per client spec: Matin 06-14, Soir 14-22, Nuit 22-06 (wraps midnight).
    public static int CurrentShiftId(DateTime now) => now.Hour switch
    {
        < 6 => 3,
        < 14 => 1,
        < 22 => 2,
        _ => 3,
    };

    // TRS (OEE) = Disponibilite x Performance x Qualite. This is a provisional
    // estimate built from data we already have, not the client's official
    // methodology. `lots` are hour/day buckets (see ProductionBucket):
    //  - Disponibilite: share of the observed time, planned breaks the line
    //    was stopped in excluded, in which the line produced (passed in, see
    //    AvailabilityPercent).
    //  - Performance: minutes the bags would have taken at each production's
    //    own nominal rate / producing minutes they actually took. Productions
    //    with no known rate are left out of both sides; none at all -> null
    //    (and TRS null) rather than a misleading 0 %. Not clamped: > 100 %
    //    means the nominal rate is set too low (flagged orange on screen).
    //  - Qualite: bags in tolerance / total bags.
    // Also breaks the buckets down per target weight (Weights), with each
    // weight's TRS banded against trsTarget, and totals the net weight made.
    public static KpiSummary Summarize(
        IReadOnlyCollection<ProductionBucket> lots, double availabilityPercent, double trsTarget = AppSettings.DefaultTrsTarget)
    {
        var summary = SummarizeCore(lots, availabilityPercent);
        summary.Weights = ByWeight(lots, trsTarget);
        summary.ProductionKg = Math.Round(summary.Weights.Sum(w => w.ProductionKg), 1);
        var weighed = summary.Weights.Where(w => w.GiveawayKg.HasValue).ToList();
        summary.GiveawayKg = weighed.Count > 0 ? Math.Round(weighed.Sum(w => w.GiveawayKg!.Value), 1) : null;
        return summary;
    }

    // One entry per target weight (rounded to the gram), biggest production
    // first. Production = bags x net average weight, or x target when that
    // weight has no trustworthy weight data; giveaway = bags x deviation.
    public static List<WeightKpi> ByWeight(IReadOnlyCollection<ProductionBucket> lots, double trsTarget = AppSettings.DefaultTrsTarget)
    {
        return lots
            .Where(l => l.TargetNetWeight > 0)
            .GroupBy(l => Math.Round(l.TargetNetWeight))
            .Select(g =>
            {
                var group = g.ToList();
                var k = SummarizeCore(group, AvailabilityPercent(group));
                var avg = WeightedAverageWeight(group);
                var product = group.GroupBy(l => l.ProductName ?? "")
                    .OrderByDescending(p => p.Sum(l => l.TotalBags))
                    .Select(p => p.Key)
                    .FirstOrDefault() ?? "";
                return new WeightKpi
                {
                    TargetWeight = g.Key,
                    ProductName = product,
                    TotalBags = k.TotalBags,
                    AvgWeight = avg is { } a ? Math.Round(a, 1) : null,
                    DeviationGrams = k.WeightDeviationGrams,
                    DeviationPercent = k.WeightDeviationPercent,
                    ProductionKg = Math.Round(k.TotalBags * (avg ?? g.Key) / 1000.0, 1),
                    GiveawayKg = k.WeightDeviationGrams is { } dev ? Math.Round(k.TotalBags * dev / 1000.0, 1) : null,
                    QualityPercent = k.QualityPercent,
                    TrsPercent = k.TrsPercent,
                    SpeedBagsPerMinute = k.SpeedBagsPerMinute,
                    MaxSpeedBagsPerMinute = k.MaxSpeedBagsPerMinute,
                    DeviationSeverity = WeightDeviationSeverity(k.WeightDeviationPercent),
                    TrsSeverity = TrsSeverity(k.TrsPercent, trsTarget),
                };
            })
            .Where(w => w.TotalBags > 0 || w.AvgWeight.HasValue)
            .OrderByDescending(w => w.ProductionKg)
            .ToList();
    }

    // Colour band of a TRS against the machine's target: reached = green,
    // within 75 % of it = orange, below = red.
    public static KpiSeverity TrsSeverity(double? trsPercent, double target) => trsPercent switch
    {
        null => KpiSeverity.Unknown,
        { } t when t >= target => KpiSeverity.Good,
        { } t when t >= 0.75 * target => KpiSeverity.Warning,
        _ => KpiSeverity.Bad,
    };

    private static readonly System.Globalization.CultureInfo French = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    // "850 kg", "12,4 t" (plain spaces: the PDF font may lack U+202F).
    public static string FormatMass(double kg, bool signed = false)
    {
        var sign = signed && kg > 0 ? "+" : "";
        var text = Math.Abs(kg) >= 10_000
            ? (kg / 1000).ToString("N1", French) + " t"
            : kg.ToString(Math.Abs(kg) >= 100 ? "N0" : "N1", French) + " kg";
        return sign + text.Replace(' ', ' ').Replace(' ', ' ');
    }

    // Average net weights of the weights produced, e.g. "501,2 g" or
    // "501,2 g · 1 003,5 g"; "—" when none is known.
    public static string FormatAverages(IEnumerable<WeightKpi> weights)
    {
        var known = weights.Where(w => w.AvgWeight.HasValue).Select(w => w.AvgWeight!.Value.ToString("N1", French) + " g").ToList();
        return known.Count == 0 ? "—" : string.Join(" · ", known).Replace(' ', ' ').Replace(' ', ' ');
    }

    private static KpiSummary SummarizeCore(IReadOnlyCollection<ProductionBucket> lots, double availabilityPercent)
    {
        // Weight comes from lots / sane running averages, which can sit in a
        // bucket with no counter delta (lot closed just after the snapshot).
        var (deviationGrams, deviationPercent) = WeightedDeviation(lots);

        var producing = lots.Where(l => l.IsProducing).ToList();
        if (producing.Count == 0)
        {
            return new KpiSummary
            {
                AvailabilityPercent = Math.Round(availabilityPercent, 1),
                WeightDeviationGrams = deviationGrams,
                WeightDeviationPercent = deviationPercent,
            };
        }

        var totalBags = producing.Sum(l => (long)l.TotalBags);
        var runMinutes = producing.Sum(l => l.RunMinutes);

        var qualityPercent = totalBags > 0 ? 100.0 * producing.Sum(l => (long)l.BagsInTolerance) / totalBags : 0;

        var actualRate = runMinutes > 0 ? totalBags / runMinutes : 0;
        var ratedRunMinutes = producing.Sum(l => l.RatedRunMinutes);
        double? performancePercent = ratedRunMinutes > 0 ? 100.0 * producing.Sum(l => l.IdealMinutes) / ratedRunMinutes : null;

        double? trsPercent = performancePercent is { } perf ? availabilityPercent * perf * qualityPercent / 10000.0 : null;
        var maxSpeed = producing.Max(l => l.MaxSpeedBagsPerMinute);

        return new KpiSummary
        {
            TotalBags = totalBags,
            BagsUnderTolerance = producing.Sum(l => (long)l.BagsUnderTolerance),
            BagsInTolerance = producing.Sum(l => (long)l.BagsInTolerance),
            BagsOverTolerance = producing.Sum(l => (long)l.BagsOverTolerance),
            BagsIndeterminate = producing.Sum(l => (long)l.BagsIndeterminate),
            MetalRejects = producing.Sum(l => (long)l.MetalRejects),
            SpeedBagsPerMinute = Math.Round(actualRate, 1),
            MaxSpeedBagsPerMinute = maxSpeed > 0 ? Math.Round(maxSpeed, 1) : null,
            AvailabilityPercent = Math.Round(availabilityPercent, 1),
            PerformancePercent = performancePercent is { } p ? Math.Round(p, 1) : null,
            QualityPercent = Math.Round(qualityPercent, 1),
            TrsPercent = trsPercent is { } t ? Math.Round(t, 1) : null,
            WeightDeviationGrams = deviationGrams,
            WeightDeviationPercent = deviationPercent,
        };
    }

    // Bags-weighted average weight over bags whose weight data is trustworthy;
    // null when there's none (e.g. CC01's corrupt running average).
    public static double? WeightedAverageWeight(IEnumerable<ProductionBucket> lots)
    {
        var list = lots.ToList();
        var bags = list.Sum(l => l.WeighedBags);
        return bags > 0 ? list.Sum(l => l.WeighedWeightSum) / bags : null;
    }

    // Average weight of one production's weighed bags at its current target
    // in these buckets (header line), null when it has none.
    public static double? ProductionAverageWeight(IEnumerable<ProductionBucket> lots, string productionId, double target)
    {
        var parts = lots.SelectMany(l => l.WeightParts).Where(p => p.ProductionId == productionId && p.Target == target).ToList();
        var bags = parts.Sum(p => p.Bags);
        return bags > 0 ? parts.Sum(p => p.WeightSum) / bags : null;
    }

    private static (double? Grams, double? Percent) WeightedDeviation(IEnumerable<ProductionBucket> lots)
    {
        var list = lots.ToList();
        var bags = list.Sum(l => l.WeighedBags);
        var targetSum = list.Sum(l => l.WeighedTargetSum);
        if (bags <= 0 || targetSum <= 0)
            return (null, null);
        var deviationSum = list.Sum(l => l.WeighedDeviationSum);
        return (Math.Round(deviationSum / bags, 2), Math.Round(100.0 * deviationSum / targetSum, 2));
    }

    // Colour band of the weight deviation, relative to the target (1 g means
    // nothing on a 5 kg bag but is a lot on a 200 g one). Running heavy only
    // gives product away: within 0.5 % green, within 1.5 % orange, beyond
    // red. Running light is a compliance risk (sous-dosage): orange as soon
    // as the average is 0.1 % under the target, red from 0.5 % under.
    public static KpiSeverity WeightDeviationSeverity(double? deviationPercent) => deviationPercent switch
    {
        null => KpiSeverity.Unknown,
        < -0.5 => KpiSeverity.Bad,
        < -0.1 => KpiSeverity.Warning,
        <= 0.5 => KpiSeverity.Good,
        <= 1.5 => KpiSeverity.Warning,
        _ => KpiSeverity.Bad,
    };

    // Share (0-100) of the required time (observed, minus the planned breaks
    // the line was stopped in) in which the line produced bags.
    public static double AvailabilityPercent(IReadOnlyCollection<ProductionBucket> lots)
    {
        var required = lots.Sum(l => l.RequiredMinutes);
        return required > 0 ? Math.Min(100, 100.0 * lots.Sum(l => l.RunMinutes) / required) : 0;
    }

    // Bags per team (TeamScheduleService roster) over the period. Every
    // configured team is listed, even at 0, so the bars compare like for like;
    // hours on a poste with no team assigned go to "Sans equipe".
    public static List<TeamKpi> ByTeam(IReadOnlyCollection<ProductionBucket> lots, TeamScheduleService teamSchedule)
    {
        var byTeam = lots
            .GroupBy(l => teamSchedule.GetTeamForShift(TeamScheduleService.GetShiftDate(l.StartTime, l.ShiftId), l.ShiftId))
            .ToDictionary(g => g.Key, g => g.Sum(l => (long)l.TotalBags));

        var teamNames = teamSchedule.TeamNames;
        var result = teamNames
            .Select(name => new TeamKpi { TeamName = name, TotalBags = byTeam.GetValueOrDefault(name) })
            .ToList();
        var unassigned = byTeam.Where(kv => !teamNames.Contains(kv.Key)).Sum(kv => kv.Value);
        if (unassigned > 0)
            result.Add(new TeamKpi { TeamName = "Sans equipe", TotalBags = unassigned });
        return result;
    }

    public static List<DailyProduction> ByDay(IReadOnlyCollection<ProductionBucket> lots)
    {
        return lots
            .GroupBy(l => l.StartTime.Date)
            .Select(g => new DailyProduction { Date = g.Key, TotalBags = g.Sum(l => (long)l.TotalBags) })
            .OrderBy(d => d.Date)
            .ToList();
    }

    // Grouped by shift date, not calendar date: a Nuit poste (22h-06h) is one
    // bar on the day it started, not split across two days.
    public static List<DailyProduction> ByDayForShift(IReadOnlyCollection<ProductionBucket> lots, int shiftId)
    {
        return lots
            .Where(l => l.ShiftId == shiftId)
            .GroupBy(l => TeamScheduleService.GetShiftDate(l.StartTime, shiftId))
            .Select(g => new DailyProduction { Date = g.Key, TotalBags = g.Sum(l => (long)l.TotalBags) })
            .OrderBy(d => d.Date)
            .ToList();
    }
}
