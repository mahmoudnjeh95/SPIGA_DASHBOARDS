namespace SpigaDashboards.App.Models;

public class KpiSummary
{
    // Sums across many lots overflow int on real production volumes, so these
    // period totals are long (per-lot ProductionLot values stay int).
    public long TotalBags { get; set; }
    public long BagsUnderTolerance { get; set; }
    public long BagsInTolerance { get; set; }
    public long BagsOverTolerance { get; set; }
    public long BagsIndeterminate { get; set; }
    public double SpeedBagsPerMinute { get; set; }
    public double ConformRatePercent { get; set; }
    public long MetalRejects { get; set; }

    // TRS (OEE) -- provisional estimate, see KpiCalculator.Summarize.
    public double AvailabilityPercent { get; set; }
    public double PerformancePercent { get; set; }
    public double QualityPercent { get; set; }
    public double TrsPercent { get; set; }

    // Bags-weighted (AvgWeight - TargetNetWeight), grams. Positive = running heavy.
    public double WeightDeviationGrams { get; set; }
}

public class ShiftKpi
{
    public int ShiftId { get; set; }
    public string ShiftName { get; set; } = "";
    public long TotalBags { get; set; }
}

public class DailyProduction
{
    public DateTime Date { get; set; }
    public long TotalBags { get; set; }
}
