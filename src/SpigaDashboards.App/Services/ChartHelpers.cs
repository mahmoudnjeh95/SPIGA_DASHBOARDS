using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace SpigaDashboards.App.Services;

// Shared LiveChartsCore building blocks, used by both the per-machine slide
// charts (MachineDashboardViewModel) and the annual synthesis charts
// (AnnualSummarySlideViewModel) so the two don't drift in style.
public static class ChartHelpers
{
    // Sized for the 60" kiosk TV read from a few metres away (1920x1080 layout).
    public const double AxisTextSize = 16;
    public const double DataLabelTextSize = 16;

    // Short counts for axes and bar labels: 9639, 15 k, 1.2 M.
    public static string Compact(double value) => Math.Abs(value) switch
    {
        >= 1_000_000 => $"{value / 1_000_000:0.#} M",
        >= 10_000 => $"{value / 1_000:0} k",
        _ => $"{value:0}",
    };

    public static Axis BuildXAxis(IEnumerable<string> labels) => new()
    {
        Labels = labels.ToArray(),
        LabelsPaint = new SolidColorPaint(ThemeService.ChartTextColor),
        TextSize = AxisTextSize,
    };

    // Value axis with the same text size as the X axis. minLimit 0 for
    // counts, so bars start from the baseline instead of an auto-zoomed one.
    public static Axis BuildYAxis(double? minLimit = null, double? maxLimit = null,
        double minStep = 0, Func<double, string>? labeler = null)
    {
        var axis = new Axis
        {
            LabelsPaint = new SolidColorPaint(ThemeService.ChartTextColor),
            TextSize = AxisTextSize,
            MinLimit = minLimit,
            MaxLimit = maxLimit,
            MinStep = minStep,
        };
        if (labeler != null)
            axis.Labeler = labeler;
        return axis;
    }
}
