namespace SpigaDashboards.App.Models;

// Weight-deviation colour band of a machine (Parametres > Machines), as a %
// of the target weight (1 g means nothing on a 5 kg bag but is a lot on a
// 200 g one), each side on its own. Running heavy only gives product away:
// green up to +OverGood, orange up to +OverWarning, red beyond. Running light
// is a compliance risk (sous-dosage), so its limits are tighter: orange below
// -UnderWarning, red below -UnderBad.
public record WeightBand
{
    public double OverGood { get; init; } = 0.5;
    public double OverWarning { get; init; } = 1.5;
    public double UnderWarning { get; init; } = 0.1;
    public double UnderBad { get; init; } = 0.5;

    // Band of a machine with none of its own (and of a group of machines).
    public static WeightBand Default => ColorThresholds.Current.DefaultWeightBand;

    public KpiSeverity Severity(double? deviationPercent) => deviationPercent switch
    {
        null => KpiSeverity.Unknown,
        { } d when d < -UnderBad => KpiSeverity.Bad,
        { } d when d < -UnderWarning => KpiSeverity.Warning,
        { } d when d <= OverGood => KpiSeverity.Good,
        { } d when d <= OverWarning => KpiSeverity.Warning,
        _ => KpiSeverity.Bad,
    };
}
