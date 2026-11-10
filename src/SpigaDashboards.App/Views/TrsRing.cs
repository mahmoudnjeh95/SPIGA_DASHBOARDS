using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SpigaDashboards.App.Views;

// TRS as a ring that fills up to its value (KpiRow's TRS tile), in the
// colour of its band (SeverityBrush), with a tick at the machine's target.
// It fills from 0 when its slide comes in (SlideMotion) and moves to the new
// value on a data refresh. No value: the empty track only.
public sealed class TrsRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double?), typeof(TrsRing), new PropertyMetadata(null, OnValueChanged));

    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(double?), typeof(TrsRing), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(TrsRing), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(TrsRing), new FrameworkPropertyMetadata(7.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // The value being drawn (animated).
    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(TrsRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double? Target { get => (double?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (TrsRing)d;
        var from = (double)ring.GetValue(ShownProperty);
        ring.Animate(from, (e.NewValue as double?) ?? 0, 0);
    }

    // Its slide came in: fill up from 0, with the tiles' count-up.
    public void Enter() => Animate(0, Value ?? 0, 150);

    private void Animate(double from, double to, int delayMs)
    {
        BeginAnimation(ShownProperty, null);
        SetValue(ShownProperty, from);
        BeginAnimation(ShownProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(1000))
        {
            EasingFunction = EaseOut,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
            return;
        var thickness = Thickness;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - thickness / 2 - 1;
        var color = (Brush as SolidColorBrush)?.Color ?? Colors.Gray;

        var track = new Pen(new SolidColorBrush(Color.FromArgb(0x38, color.R, color.G, color.B)), thickness);
        dc.DrawEllipse(null, track, center, radius, radius);

        var shown = Math.Clamp((double)GetValue(ShownProperty) / 100, 0, 1);
        if (Value != null && shown > 0.001)
        {
            var arc = new Pen(new SolidColorBrush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (shown >= 0.9999)
                dc.DrawEllipse(null, arc, center, radius, radius);
            else
                dc.DrawGeometry(null, arc, Arc(center, radius, shown));
        }

        // Target: a short tick across the ring.
        if (Target is { } target && target > 0)
        {
            var angle = (target / 100 * 360 - 90) * Math.PI / 180;
            var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
            var inner = radius - thickness / 2 - 3;
            var outer = radius + thickness / 2 + 1;
            var tickBrush = Application.Current.TryFindResource("PrimaryTextBrush") as Brush ?? Brushes.White;
            dc.DrawLine(new Pen(tickBrush, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                new Point(center.X + inner * cos, center.Y + inner * sin),
                new Point(center.X + outer * cos, center.Y + outer * sin));
        }
    }

    // Clockwise from the top, over `fraction` of the circle.
    private static Geometry Arc(Point center, double radius, double fraction)
    {
        var end = (fraction * 360 - 90) * Math.PI / 180;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            context.ArcTo(new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end)),
                new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
