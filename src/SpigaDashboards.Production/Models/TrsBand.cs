namespace SpigaDashboards.App.Models;

// TRS colour band of a machine: green from Target, orange from OrangeFrom,
// red below. OrangeFrom = target - the machine's tolerance (Parametres >
// Machines), or a share of the target (Parametres > Couleurs) when it has
// none. A group of machines (synthese, conditionnement, cumul) uses the
// average of its machines' bands, as it already did for the target.
public readonly record struct TrsBand(double Target, double OrangeFrom)
{
    public static TrsBand ForTarget(double target, double? tolerance = null)
        => new(target, tolerance is { } points
            ? Math.Max(0, target - points)
            : ColorThresholds.Current.TrsWarningPercentOfTarget / 100 * target);

    public static TrsBand Default => ForTarget(AppSettings.DefaultTrsTarget);

    public static TrsBand Average(IReadOnlyCollection<TrsBand> bands)
        => bands.Count == 0 ? Default : new(bands.Average(b => b.Target), bands.Average(b => b.OrangeFrom));

    public KpiSeverity Severity(double? trsPercent) => trsPercent switch
    {
        null => KpiSeverity.Unknown,
        { } t when t >= Target => KpiSeverity.Good,
        { } t when t >= OrangeFrom => KpiSeverity.Warning,
        _ => KpiSeverity.Bad,
    };
}
