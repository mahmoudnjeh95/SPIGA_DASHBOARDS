using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.ViewModels.Slides;

// Direction of a poste figure over the last TrendWindow, as shown on screen
// (to 0,1): +1 up, -1 down, 0 same figure; Text = "+0,3 pt · 2 min". Good is
// the colour: +1 green, -1 red, 0 grey (TRS and speed: up is good; weight:
// closer to the target is good whichever way it moved).
public sealed record KpiTrend(int Direction, string Text, int Good)
{
    public static readonly TimeSpan TrendWindow = TimeSpan.FromMinutes(2);
    private static readonly System.Globalization.CultureInfo French = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    // "On the target": the main weight's average within this of its target.
    public const double WeightOnTargetGrams = 0.5;

    public static bool WeightOnTarget(KpiSummary kpis)
        => kpis.Weights.FirstOrDefault() is { AvgWeight: { } avg } main
           && Math.Abs(avg - main.TargetWeight) < WeightOnTargetGrams;

    public static KpiTrend? Of(double? now, double? before, string unit, int? goodDirection = null)
    {
        if (now is not { } n || before is not { } b)
            return null;
        var delta = Math.Round(n, 1) - Math.Round(b, 1);
        var minutes = $"{TrendWindow.TotalMinutes:0} min";
        if (Math.Abs(delta) < 0.05)
            return new KpiTrend(0, string.Format(French, "{0:0.0}{1} · {2}", 0.0, unit, minutes), 0);
        var direction = Math.Sign(delta);
        return new KpiTrend(direction, string.Format(French, "{0:+0.0;-0.0}{1} · {2}", delta, unit, minutes), goodDirection ?? direction);
    }
}
