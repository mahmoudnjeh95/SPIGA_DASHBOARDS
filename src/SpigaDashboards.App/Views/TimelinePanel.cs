using System.Windows;
using System.Windows.Controls;

namespace SpigaDashboards.App.Views;

// Lays children along a time axis: Start and End are fractions (0..1) of the
// panel's width, so a week board stays right whatever the window size. A
// child with its own Width (the "now" line) keeps it, starting at Start.
public class TimelinePanel : Panel
{
    public static readonly DependencyProperty StartProperty = DependencyProperty.RegisterAttached(
        "Start", typeof(double), typeof(TimelinePanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty EndProperty = DependencyProperty.RegisterAttached(
        "End", typeof(double), typeof(TimelinePanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static double GetStart(DependencyObject d) => (double)d.GetValue(StartProperty);
    public static void SetStart(DependencyObject d, double value) => d.SetValue(StartProperty, value);
    public static double GetEnd(DependencyObject d) => (double)d.GetValue(EndProperty);
    public static void SetEnd(DependencyObject d, double value) => d.SetValue(EndProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        // As tall as its tallest child: the parent decides the rest (a board
        // row has a fixed height; the "now" line then stretches to it).
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            var start = Math.Clamp(GetStart(child), 0, 1) * finalSize.Width;
            var end = Math.Clamp(GetEnd(child), 0, 1) * finalSize.Width;
            var fixedWidth = child is FrameworkElement fe && !double.IsNaN(fe.Width) ? fe.Width : 0;
            var width = Math.Max(Math.Max(end - start, fixedWidth), 3);
            child.Arrange(new Rect(start, 0, width, finalSize.Height));
        }
        return finalSize;
    }
}
