using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using SpigaDashboards.App.ViewModels.Slides;

namespace SpigaDashboards.App.Views;

// Up / down arrow beside a poste figure (KpiRow: TRS, vitesse), with the change
// over the last KpiTrend.TrendWindow under it: arrow and change in the colour
// of what the move means (TRS, vitesse: rising green, falling red; poids:
// towards the target green, away from it red), flat grey stroke when the
// figure on screen is the same.
// Slides in from the way it points when it appears or turns, then stays still.
public sealed class TrendBadge : StackPanel
{
    public static readonly DependencyProperty TrendProperty = DependencyProperty.Register(
        nameof(Trend), typeof(KpiTrend), typeof(TrendBadge), new PropertyMetadata(null, OnTrendChanged));

    public KpiTrend? Trend { get => (KpiTrend?)GetValue(TrendProperty); set => SetValue(TrendProperty, value); }

    // Rising / falling: the chosen icons (Assets/trend-up|down.png); steady: a grey dash.
    private static readonly ImageSource Up = Icon("trend-up.png");
    private static readonly ImageSource Down = Icon("trend-down.png");
    // Weight: the arrow in the colour of what it means (rising away from the
    // target = red, falling towards it = green).
    private static readonly ImageSource UpBad = Icon("trend-up-red.png");
    private static readonly ImageSource DownGood = Icon("trend-down-green.png");

    private readonly Image _icon = new() { Width = 44, Height = 44, Margin = new Thickness(0, -4, 0, -4), HorizontalAlignment = HorizontalAlignment.Center };
    // Same figure: a flat grey stroke in the arrows' style, at their size.
    private readonly System.Windows.Shapes.Path _flat = new()
    {
        Data = Geometry.Parse("M 7,18 L 37,18"),
        StrokeThickness = 6,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Width = 44,
        Height = 36,
        Margin = new Thickness(0, -2, 0, -2),
        HorizontalAlignment = HorizontalAlignment.Center,
    };
    private readonly TextBlock _label = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TranslateTransform _shift = new();

    public TrendBadge()
    {
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(10, 0, 6, 0);
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);
        _flat.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SecondaryTextBrush");
        Children.Add(_icon);
        Children.Add(_flat);
        Children.Add(_label);
        RenderTransform = _shift;
        _label.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        Visibility = Visibility.Collapsed;
    }

    private static void OnTrendChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var badge = (TrendBadge)d;
        var old = e.OldValue as KpiTrend;
        if (e.NewValue is not KpiTrend trend)
        {
            badge.Visibility = Visibility.Collapsed;
            return;
        }

        badge.Visibility = Visibility.Visible;
        badge._icon.Source = trend.Direction > 0 ? (trend.Good < 0 ? UpBad : Up)
            : trend.Direction < 0 ? (trend.Good > 0 ? DownGood : Down)
            : null;
        badge._icon.Visibility = trend.Direction == 0 ? Visibility.Collapsed : Visibility.Visible;
        badge._flat.Visibility = trend.Direction == 0 ? Visibility.Visible : Visibility.Collapsed;
        // The change in the colour of what it means: green good, red bad, grey same.
        badge._label.SetResourceReference(TextBlock.ForegroundProperty,
            trend.Good > 0 ? "SuccessBrush" : trend.Good < 0 ? "DangerBrush" : "SecondaryTextBrush");
        badge._label.Text = trend.Text;

        if (old?.Direction == trend.Direction)
            return;
        var from = trend.Direction > 0 ? 10 : trend.Direction < 0 ? -10 : 0;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
        badge.Opacity = 0;
        badge.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)) { BeginTime = TimeSpan.FromMilliseconds(300) });
        badge._shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(300) });
    }

    private static ImageSource Icon(string file)
    {
        var image = new BitmapImage(new Uri($"pack://application:,,,/Assets/{file}"));
        image.Freeze();
        return image;
    }
}
