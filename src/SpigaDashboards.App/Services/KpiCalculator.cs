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
    // methodology (which needs planned-stop and ideal-cycle-time inputs we
    // don't collect yet). `lots` are hour/day buckets (see ProductionBucket):
    //  - Disponibilite: share of the observed time in which the line produced
    //    (passed in, see AvailabilityPercent).
    //  - Performance: bags/min over producing time only vs. a bags-weighted
    //    average of each product's NominalRatePerMinute (0 until set per article).
    //  - Qualite: bags in tolerance / total bags.
    public static KpiSummary Summarize(IReadOnlyCollection<ProductionBucket> lots, double availabilityPercent)
    {
        var producing = lots.Where(l => l.IsProducing).ToList();
        if (producing.Count == 0)
            return new KpiSummary { AvailabilityPercent = Math.Round(availabilityPercent, 1) };

        var totalBags = producing.Sum(l => (long)l.TotalBags);
        var runMinutes = producing.Sum(l => l.RunMinutes);
        var conformIntervals = producing.Count(l => l.IsConform);

        var qualityPercent = totalBags > 0 ? 100.0 * producing.Sum(l => (long)l.BagsInTolerance) / totalBags : 0;

        var actualRate = runMinutes > 0 ? totalBags / runMinutes : 0;
        var weightedNominalRate = totalBags > 0 ? producing.Sum(l => l.NominalRatePerMinute * l.TotalBags) / totalBags : 0;
        var performancePercent = weightedNominalRate > 0 ? 100.0 * actualRate / weightedNominalRate : 0;

        var trsPercent = availabilityPercent * performancePercent * qualityPercent / 10000.0;

        return new KpiSummary
        {
            TotalBags = totalBags,
            BagsUnderTolerance = producing.Sum(l => (long)l.BagsUnderTolerance),
            BagsInTolerance = producing.Sum(l => (long)l.BagsInTolerance),
            BagsOverTolerance = producing.Sum(l => (long)l.BagsOverTolerance),
            BagsIndeterminate = producing.Sum(l => (long)l.BagsIndeterminate),
            MetalRejects = producing.Sum(l => (long)l.MetalRejects),
            SpeedBagsPerMinute = Math.Round(actualRate, 2),
            ConformRatePercent = Math.Round(100.0 * conformIntervals / producing.Count, 1),
            AvailabilityPercent = Math.Round(availabilityPercent, 1),
            PerformancePercent = Math.Round(performancePercent, 1),
            QualityPercent = Math.Round(qualityPercent, 1),
            TrsPercent = Math.Round(trsPercent, 1),
            WeightDeviationGrams = Math.Round(WeightedDeviation(producing), 2),
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

    private static double WeightedDeviation(IEnumerable<ProductionBucket> lots)
    {
        var list = lots.ToList();
        var bags = list.Sum(l => l.WeighedBags);
        return bags > 0 ? list.Sum(l => l.WeighedDeviationSum) / bags : 0;
    }

    // Share (0-100) of the observed time in which the line produced bags.
    public static double AvailabilityPercent(IReadOnlyCollection<ProductionBucket> lots)
    {
        var observed = lots.Sum(l => l.ObservedMinutes);
        return observed > 0 ? 100.0 * lots.Sum(l => l.RunMinutes) / observed : 0;
    }

    public static List<ShiftKpi> ByShift(IReadOnlyCollection<ProductionBucket> lots)
    {
        return lots
            .GroupBy(l => l.ShiftId)
            .Select(g => new ShiftKpi
            {
                ShiftId = g.Key,
                ShiftName = ShiftNames.GetValueOrDefault(g.Key, $"Shift {g.Key}"),
                TotalBags = g.Sum(l => (long)l.TotalBags),
            })
            .OrderBy(s => s.ShiftId)
            .ToList();
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
