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
    // don't collect yet):
    //  - Disponibilite: fraction of MachineStatusLog snapshots in the period
    //    where the belt was running (passed in, computed from status history).
    //  - Performance: actual bags/min vs. a bags-weighted average of each
    //    lot's product's NominalRatePerMinute (the closest thing we have to
    //    an ideal cycle time).
    //  - Qualite: bags in tolerance / total bags.
    public static KpiSummary Summarize(IReadOnlyCollection<ProductionLot> lots, double availabilityPercent)
    {
        if (lots.Count == 0)
            return new KpiSummary { AvailabilityPercent = Math.Round(availabilityPercent, 1) };

        var totalBags = lots.Sum(l => (long)l.TotalBags);
        var totalMinutes = lots.Sum(l => l.DurationMinutes);
        var conformLots = lots.Count(l => l.IsConform);

        var qualityPercent = totalBags > 0 ? 100.0 * lots.Sum(l => (long)l.BagsInTolerance) / totalBags : 0;

        var actualRate = totalMinutes > 0 ? totalBags / totalMinutes : 0;
        var weightedNominalRate = totalBags > 0 ? lots.Sum(l => l.NominalRatePerMinute * l.TotalBags) / totalBags : 0;
        var performancePercent = weightedNominalRate > 0 ? 100.0 * actualRate / weightedNominalRate : 0;

        var trsPercent = availabilityPercent * performancePercent * qualityPercent / 10000.0;

        var weightDeviation = totalBags > 0
            ? lots.Sum(l => (l.AvgWeight - l.TargetNetWeight) * l.TotalBags) / totalBags
            : 0;

        return new KpiSummary
        {
            TotalBags = totalBags,
            BagsUnderTolerance = lots.Sum(l => (long)l.BagsUnderTolerance),
            BagsInTolerance = lots.Sum(l => (long)l.BagsInTolerance),
            BagsOverTolerance = lots.Sum(l => (long)l.BagsOverTolerance),
            BagsIndeterminate = lots.Sum(l => (long)l.BagsIndeterminate),
            MetalRejects = lots.Sum(l => (long)l.MetalRejects),
            SpeedBagsPerMinute = totalMinutes > 0 ? Math.Round(totalBags / totalMinutes, 2) : 0,
            ConformRatePercent = Math.Round(100.0 * conformLots / lots.Count, 1),
            AvailabilityPercent = Math.Round(availabilityPercent, 1),
            PerformancePercent = Math.Round(performancePercent, 1),
            QualityPercent = Math.Round(qualityPercent, 1),
            TrsPercent = Math.Round(trsPercent, 1),
            WeightDeviationGrams = Math.Round(weightDeviation, 2),
        };
    }

    public static List<ShiftKpi> ByShift(IReadOnlyCollection<ProductionLot> lots)
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

    public static List<DailyProduction> ByDay(IReadOnlyCollection<ProductionLot> lots)
    {
        return lots
            .GroupBy(l => l.StartTime.Date)
            .Select(g => new DailyProduction { Date = g.Key, TotalBags = g.Sum(l => (long)l.TotalBags) })
            .OrderBy(d => d.Date)
            .ToList();
    }

    public static List<DailyProduction> ByDayForShift(IReadOnlyCollection<ProductionLot> lots, int shiftId)
    {
        return ByDay(lots.Where(l => l.ShiftId == shiftId).ToList());
    }
}
