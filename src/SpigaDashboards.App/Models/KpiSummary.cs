namespace SpigaDashboards.App.Models;

public class KpiSummary
{
    // Sums across many lots overflow int on real production volumes, so these
    // period totals are long (per-lot ProductionBucket values stay int).
    public long TotalBags { get; set; }
    public long BagsUnderTolerance { get; set; }
    public long BagsInTolerance { get; set; }
    public long BagsOverTolerance { get; set; }
    public long BagsIndeterminate { get; set; }
    public double SpeedBagsPerMinute { get; set; }
    public long MetalRejects { get; set; }

    // TRS (OEE) -- provisional estimate, see KpiCalculator.Summarize.
    // Performance and TRS are null when no nominal rate is known for what
    // was produced (shown as "—", never as a made-up 0 %).
    public double AvailabilityPercent { get; set; }
    public double? PerformancePercent { get; set; }
    public double QualityPercent { get; set; }
    public double? TrsPercent { get; set; }

    // Bags-weighted (AvgWeight - TargetNetWeight), grams and % of the target.
    // Positive = running heavy. Null when there's no trustworthy weight data.
    public double? WeightDeviationGrams { get; set; }
    public double? WeightDeviationPercent { get; set; }
}

// Traffic-light level of a KPI, computed in the view-model (the converter
// only maps it to a theme brush). Unknown = no data, shown neutral grey.
public enum KpiSeverity
{
    Unknown,
    Good,
    Warning,
    Bad,
}

public class TeamKpi
{
    public string TeamName { get; set; } = "";
    public long TotalBags { get; set; }
}

public class DailyProduction
{
    public DateTime Date { get; set; }
    public long TotalBags { get; set; }
}
