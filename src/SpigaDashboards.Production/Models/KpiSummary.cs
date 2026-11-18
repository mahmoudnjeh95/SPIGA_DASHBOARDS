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
    public double? SpeedBagsPerMinute { get; set; }
    public long MetalRejects { get; set; }

    // Fastest snapshot interval of the period (bags/min), null = none measured.
    public double? MaxSpeedBagsPerMinute { get; set; }

    // TRS (OEE) -- provisional estimate, see KpiCalculator.Summarize.
    // Performance and TRS are null when no nominal rate is known for what
    // was produced (shown as "—", never as a made-up 0 %).
    public double AvailabilityPercent { get; set; }
    public double? PerformancePercent { get; set; }
    public double? QualityPercent { get; set; }
    public double? TrsPercent { get; set; }

    // Bags-weighted (net AvgWeight - TargetNetWeight), grams and % of the
    // target. Positive = running heavy. Null when there's no trustworthy
    // weight data.
    public double? WeightDeviationGrams { get; set; }
    public double? WeightDeviationPercent { get; set; }

    // Net weight produced (bags x target weight) and weight given away above
    // the target (negative = under), in kg.
    public double ProductionKg { get; set; }
    public double? GiveawayKg { get; set; }
    public string GiveawayLabel => Services.KpiCalculator.FormatGiveaway(GiveawayKg);

    // One entry per target weight produced, heaviest production first.
    public List<WeightKpi> Weights { get; set; } = new();
}

// Figures for one target weight (500 g, 1 kg, ...) over a set of buckets,
// one line of the "Production par poids" tables.
public class WeightKpi
{
    public double TargetWeight { get; set; }
    public string ProductName { get; set; } = "";
    public long TotalBags { get; set; }
    public double? AvgWeight { get; set; }
    public double? DeviationGrams { get; set; }
    public double? DeviationPercent { get; set; }
    public double ProductionKg { get; set; }
    public double? GiveawayKg { get; set; }
    public double? QualityPercent { get; set; }
    public double? TrsPercent { get; set; }
    public double? SpeedBagsPerMinute { get; set; }
    public double? MaxSpeedBagsPerMinute { get; set; }
    public KpiSeverity DeviationSeverity { get; set; }
    public KpiSeverity TrsSeverity { get; set; }

    public string ProductionText => Services.KpiCalculator.FormatMass(ProductionKg);
    public string GiveawayText => GiveawayKg is { } kg ? Services.KpiCalculator.FormatMass(kg, signed: true) : "—";
    public string GiveawayLabel => Services.KpiCalculator.FormatGiveaway(GiveawayKg);
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
