using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace SpigaDashboards.App.Views;

// Scrolling stop bar under the machine photo (MachineHeader) and its preview
// in Parametres > Couleurs. The text is laid out as many copies side by side
// as needed to fill the bar plus one, and the row slides left by exactly one
// copy, forever: a seamless loop at Speed pixels per second. When the text
// changes (the duration every minute), the loop goes on from where it was.
public partial class StopTicker : UserControl
{
    private const string Separator = "        ◆        ";

    public static readonly DependencyProperty BadgeTextProperty = Register(nameof(BadgeText), typeof(string), "");
    public static readonly DependencyProperty TextProperty = Register(nameof(Text), typeof(string), "");
    public static readonly DependencyProperty BarColorProperty = Register(nameof(BarColor), typeof(Brush), Brushes.Firebrick);
    public static readonly DependencyProperty TextColorProperty = Register(nameof(TextColor), typeof(Brush), Brushes.White);
    public static readonly DependencyProperty SpeedProperty = Register(nameof(Speed), typeof(double), 120.0);

    public string BadgeText { get => (string)GetValue(BadgeTextProperty); set => SetValue(BadgeTextProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush BarColor { get => (Brush)GetValue(BarColorProperty); set => SetValue(BarColorProperty, value); }
    public Brush TextColor { get => (Brush)GetValue(TextColorProperty); set => SetValue(TextColorProperty, value); }
    public double Speed { get => (double)GetValue(SpeedProperty); set => SetValue(SpeedProperty, value); }

    private double _copyWidth;

    public StopTicker()
    {
        InitializeComponent();
        Loaded += (_, _) => { StartPulse(); Rebuild(); };
        Unloaded += (_, _) => StopAll();
        Track.SizeChanged += (_, _) => Rebuild();
    }

    private static DependencyProperty Register(string name, Type type, object defaultValue)
        => DependencyProperty.Register(name, type, typeof(StopTicker), new PropertyMetadata(defaultValue, OnChanged));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StopTicker ticker && ticker.IsLoaded)
        {
            if (e.Property == BarColorProperty)
                ticker.StartPulse();
            ticker.Rebuild();
        }
    }

    private void Rebuild()
    {
        var textBrush = TextColor ?? Brushes.White;
        Bar.Background = BarColor ?? Brushes.Firebrick;
        Icon.Foreground = textBrush;
        BadgeBlock.Foreground = textBrush;
        BadgeBlock.Text = BadgeText ?? "";

        var trackWidth = Track.ActualWidth;
        var text = (Text ?? "").Trim();
        if (trackWidth <= 0 || text.Length == 0)
        {
            Strip.Children.Clear();
            Shift.BeginAnimation(TranslateTransform.XProperty, null);
            return;
        }

        var sample = NewCopy(text + Separator, textBrush);
        sample.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var copyWidth = Math.Max(1, sample.DesiredSize.Width);
        var copies = (int)Math.Ceiling(trackWidth / copyWidth) + 1;

        Strip.Children.Clear();
        Strip.Height = Track.ActualHeight;
        Strip.Children.Add(sample);
        for (var i = 1; i < copies; i++)
            Strip.Children.Add(NewCopy(text + Separator, textBrush));

        // Same place in the loop as before the rebuild (no jump each minute).
        var from = _copyWidth > 0 ? -(-Shift.X % copyWidth) : 0;
        _copyWidth = copyWidth;
        var speed = Math.Max(1, Speed);
        var first = new DoubleAnimation(from, -copyWidth, TimeSpan.FromSeconds((copyWidth + from) / speed));
        first.Completed += (_, _) =>
        {
            // Then whole copies, forever (only if nothing rebuilt it meanwhile).
            if (_copyWidth == copyWidth)
                Shift.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(0, -copyWidth, TimeSpan.FromSeconds(copyWidth / speed)) { RepeatBehavior = RepeatBehavior.Forever });
        };
        Shift.BeginAnimation(TranslateTransform.XProperty, first);
    }

    private static TextBlock NewCopy(string text, Brush foreground) => new()
    {
        Text = text,
        FontSize = 22,
        FontWeight = FontWeights.Bold,
        Foreground = foreground,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Glow, light sweep and blinking icon, in step, about once a second.
    private void StartPulse()
    {
        Glow.Color = (BarColor as SolidColorBrush)?.Color ?? Colors.Red;
        var period = TimeSpan.FromMilliseconds(650);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        Glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(4, 30, period) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        Shine.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 0.16, period) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        Icon.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0.2, period) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }

    private void StopAll()
    {
        Shift.BeginAnimation(TranslateTransform.XProperty, null);
        Glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
        Shine.BeginAnimation(OpacityProperty, null);
        Icon.BeginAnimation(OpacityProperty, null);
        _copyWidth = 0;
    }
}
