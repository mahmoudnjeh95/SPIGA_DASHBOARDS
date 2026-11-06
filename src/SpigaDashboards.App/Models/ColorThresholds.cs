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

    // Weight deviation, as a % of the target (1 g means nothing on a 5 kg bag
    // but is a lot on a 200 g one), each side on its own. Running heavy only
    // gives product away: green up to +OverGood, orange up to +OverWarning,
    // red beyond. Running light is a compliance risk (sous-dosage), so its
    // limits are tighter: orange below -UnderWarning, red below -UnderBad.
    public double WeightOverGood { get; set; } = 0.5;
    public double WeightOverWarning { get; set; } = 1.5;
    public double WeightUnderWarning { get; set; } = 0.1;
    public double WeightUnderBad { get; set; } = 0.5;

    public KpiSeverity Availability(double? percent) => Band(percent, AvailabilityGood, AvailabilityWarning);

    public KpiSeverity Performance(double? percent)
        => percent > PerformanceHigh ? KpiSeverity.Warning : Band(percent, PerformanceGood, PerformanceWarning);

    public KpiSeverity Quality(double? percent) => Band(percent, QualityGood, QualityWarning);

    public KpiSeverity WeightDeviation(double? deviationPercent) => deviationPercent switch
    {
        null => KpiSeverity.Unknown,
        { } d when d < -WeightUnderBad => KpiSeverity.Bad,
        { } d when d < -WeightUnderWarning => KpiSeverity.Warning,
        { } d when d <= WeightOverGood => KpiSeverity.Good,
        { } d when d <= WeightOverWarning => KpiSeverity.Warning,
        _ => KpiSeverity.Bad,
    };

    // "Higher is better": green from good, orange from warning, red below.
    private static KpiSeverity Band(double? percent, double good, double warning) => percent switch
    {
        null => KpiSeverity.Unknown,
        { } p when p >= good => KpiSeverity.Good,
        { } p when p >= warning => KpiSeverity.Warning,
        _ => KpiSeverity.Bad,
    };
}
