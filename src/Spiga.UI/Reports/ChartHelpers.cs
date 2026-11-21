using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace SpigaDashboards.App.Services;

// Shared LiveChartsCore building blocks, used by the slide charts and the
// export images (DashboardCharts) so they don't drift in style.
public static class ChartHelpers
{
    // Sized for the 60" kiosk TV read from a few metres away (1920x1080 layout).
    public const double AxisTextSize = 16;
    public const double DataLabelTextSize = 16;

    // Series colours, in order, for charts with one series per line / weight.
    public static readonly SKColor[] Palette =
    {
        new(0x3B, 0x82, 0xF6), new(0x2E, 0xCC, 0x71), new(0xF3, 0x9C, 0x12),
        new(0xE7, 0x4C, 0x3C), new(0x9B, 0x59, 0xB6), new(0x1A, 0xBC, 0x9C),
    };

    // Export images are drawn on white paper whatever the screen theme:
    // ExportService sets this while it builds its charts.
    [ThreadStatic] private static bool _printColors;

    public static SKColor TextColor => _printColors ? new SKColor(0x1B, 0x1F, 0x26) : ScreenTextColor();

    // The screen theme's chart text colour (the kiosk sets it to its ThemeService's).
    public static Func<SKColor> ScreenTextColor { get; set; } = () => new SKColor(0x1B, 0x1F, 0x26);

    public static IDisposable PrintScope()
    {
        _printColors = true;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => _printColors = false;
    }

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
        LabelsPaint = new SolidColorPaint(TextColor),
        TextSize = AxisTextSize,
    };

    // Value axis with the same text size as the X axis. minLimit 0 for
    // counts, so bars start from the baseline instead of an auto-zoomed one.
    public static Axis BuildYAxis(double? minLimit = null, double? maxLimit = null,
        double minStep = 0, Func<double, string>? labeler = null)
    {
        var axis = new Axis
        {
            LabelsPaint = new SolidColorPaint(TextColor),
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
