namespace SpigaDashboards.App.Models;

public class KpiSummary
{
    public int TotalBags { get; set; }
    public int BagsUnderTolerance { get; set; }
    public int BagsInTolerance { get; set; }
    public int BagsOverTolerance { get; set; }
    public int BagsIndeterminate { get; set; }
    public double SpeedBagsPerMinute { get; set; }
    public double ConformRatePercent { get; set; }
    public int MetalRejects { get; set; }

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
    public int TotalBags { get; set; }
}

public class DailyProduction
{
    public DateTime Date { get; set; }
    public int TotalBags { get; set; }
}
