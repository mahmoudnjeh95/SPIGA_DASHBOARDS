using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace SpigaDashboards.App.Services;

// Shared LiveChartsCore building blocks, used by both the per-machine slide
// charts (MachineDashboardViewModel) and the annual synthesis charts
// (AnnualSummarySlideViewModel) so the two don't drift in style.
public static class ChartHelpers
{
    public static Axis BuildXAxis(IEnumerable<string> labels) => new()
    {
        Labels = labels.ToArray(),
        LabelsPaint = new SolidColorPaint(ThemeService.ChartTextColor),
        TextSize = 12,
    };
}
