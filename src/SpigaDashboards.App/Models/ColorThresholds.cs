namespace SpigaDashboards.App.Models;

// Where the coloured figures turn from green to orange to red (Parametres >
// Couleurs, shared by every PC). The defaults are the bands the app used
// before they could be set.
public class ColorThresholds
{
    // The values in force (AppSettings.Colors), for the converters and
    // KpiCalculator, which have no AppSettings at hand.
    public static ColorThresholds Current { get; set; } = new();

    // TRS: green from the machine's own target (Machines tab), orange from
    // this share of the target (%), red below -- unless the machine has its
    // own tolerance (Machines tab, see TrsBand).
    public double TrsWarningPercentOfTarget { get; set; } = 75;

    // Disponibilite (%): green from Good, orange from Warning, red below.
    public double AvailabilityGood { get; set; } = 85;
    public double AvailabilityWarning { get; set; } = 60;

    // Performance (%): as Disponibilite, but above High it is orange again
    // (the cadence nominale is probably set too low).
    public double PerformanceGood { get; set; } = 85;
    public double PerformanceWarning { get; set; } = 60;
    public double PerformanceHigh { get; set; } = 105;

    // Taux de conformite / Qualite (%): 90 % of bags in tolerance is not good.
    public double QualityGood { get; set; } = 98;
    public double QualityWarning { get; set; } = 95;

    // Weight deviation limits: set per machine since 1.5.18 (Parametres >
    // Machines, see WeightBand), no longer shown in Couleurs. Kept as the band
    // of a machine with none of its own, so limits set in Couleurs by 1.5.16
    // / 1.5.17 still apply until the machines get theirs.
    public double WeightOverGood { get; set; } = 0.5;
    public double WeightOverWarning { get; set; } = 1.5;
    public double WeightUnderWarning { get; set; } = 0.1;
    public double WeightUnderBad { get; set; } = 0.5;

    [System.Text.Json.Serialization.JsonIgnore]
    public WeightBand DefaultWeightBand => new()
    {
        OverGood = WeightOverGood,
        OverWarning = WeightOverWarning,
        UnderWarning = WeightUnderWarning,
        UnderBad = WeightUnderBad,
    };

    public KpiSeverity Availability(double? percent) => Band(percent, AvailabilityGood, AvailabilityWarning);

    public KpiSeverity Performance(double? percent)
        => percent > PerformanceHigh ? KpiSeverity.Warning : Band(percent, PerformanceGood, PerformanceWarning);

    public KpiSeverity Quality(double? percent) => Band(percent, QualityGood, QualityWarning);

    // "Higher is better": green from good, orange from warning, red below.
    private static KpiSeverity Band(double? percent, double good, double warning) => percent switch
    {
        null => KpiSeverity.Unknown,
        { } p when p >= good => KpiSeverity.Good,
        { } p when p >= warning => KpiSeverity.Warning,
        _ => KpiSeverity.Bad,
    };
}
