using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SpigaDashboards.App.Views;

// Live figures of the KPI tiles (Views/KpiRow). Purely visual: nothing here
// reads or changes what a slide computes.
//  - Count-up: the figure rolls to its value, from 0 when its slide comes in,
//    from the figure shown before on a data refresh of the same slide.
//  - Change flash: when the figure moved by at least MinDelta since this
//    slide (same window, machine, period, filters and poste: SlideMotion.Scope)
//    was last on screen, its tile glows green (better) or red (worse) and
//    shows the change ("▲ +1,2 pt") for a few seconds.
//  - Alarm: while the figure is in the red (its colour is DangerBrush), the
//    tile has a slow red breathing outline.
// The overlays are an adorner over the tile, nothing is added to its content.
public static class TileFx
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private const int CountMs = 900;
    // Entrance: the tiles fade up from about 120 ms (SlideMotion.CascadeTiles).
    private const int EntranceDelayMs = 150;
    private const int DeltaShownMs = 9000;

    // Last figure shown per window + scope + metric, to tell what changed
    // since the slide was last on screen. Older than this: not compared.
    private static readonly TimeSpan MemoryLife = TimeSpan.FromMinutes(30);
    private static readonly Dictionary<string, (double Value, DateTime At)> Memory = new();

    // ---- Attached properties set on the figure's TextBlock ----

    // The figure (a number, or null for "—").
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(object), typeof(TileFx), new PropertyMetadata(null, (d, _) => Queue(d)));
    public static object? GetValue(DependencyObject d) => d.GetValue(ValueProperty);
    public static void SetValue(DependencyObject d, object? value) => d.SetValue(ValueProperty, value);

    // String.Format pattern of the figure, e.g. "{0:0.0}%".
    public static readonly DependencyProperty FormatProperty = DependencyProperty.RegisterAttached(
        "Format", typeof(string), typeof(TileFx), new PropertyMetadata("{0}"));
    public static string GetFormat(DependencyObject d) => (string)d.GetValue(FormatProperty);
    public static void SetFormat(DependencyObject d, string value) => d.SetValue(FormatProperty, value);

    // Pattern of the change, e.g. "{0:+0.0;-0.0} pt". None: no change shown.
    public static readonly DependencyProperty DeltaFormatProperty = DependencyProperty.RegisterAttached(
        "DeltaFormat", typeof(string), typeof(TileFx), new PropertyMetadata(null));
    public static string? GetDeltaFormat(DependencyObject d) => (string?)d.GetValue(DeltaFormatProperty);
    public static void SetDeltaFormat(DependencyObject d, string? value) => d.SetValue(DeltaFormatProperty, value);

    // Name of the figure in the memory ("trs", "speed"...). None: nothing remembered.
    public static readonly DependencyProperty MetricProperty = DependencyProperty.RegisterAttached(
        "Metric", typeof(string), typeof(TileFx), new PropertyMetadata(null));
    public static string? GetMetric(DependencyObject d) => (string?)d.GetValue(MetricProperty);
    public static void SetMetric(DependencyObject d, string? value) => d.SetValue(MetricProperty, value);

    // Which way is good: "Up" (TRS), "Down", or "Zero" (weight deviation:
    // closer to the target is better). Anything else: neutral (blue) flash.
    public static readonly DependencyProperty BetterProperty = DependencyProperty.RegisterAttached(
        "Better", typeof(string), typeof(TileFx), new PropertyMetadata(null));
    public static string? GetBetter(DependencyObject d) => (string?)d.GetValue(BetterProperty);
    public static void SetBetter(DependencyObject d, string? value) => d.SetValue(BetterProperty, value);

    // Smallest change worth a flash (a TRS going from 53,4 to 53,5 % isn't news).
    public static readonly DependencyProperty MinDeltaProperty = DependencyProperty.RegisterAttached(
        "MinDelta", typeof(double), typeof(TileFx), new PropertyMetadata(0.0));
    public static double GetMinDelta(DependencyObject d) => (double)d.GetValue(MinDeltaProperty);
    public static void SetMinDelta(DependencyObject d, double value) => d.SetValue(MinDeltaProperty, value);

    // The figure being drawn, animated by the count-up.
    private static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached(
        "Shown", typeof(double), typeof(TileFx), new PropertyMetadata(0.0, OnShownChanged));

    private sealed class State
    {
        public bool Seen;
        public bool Queued;
        public string? Scope;
        public double? Value;
    }

    private static readonly ConditionalWeakTable<TextBlock, State> States = new();

    // A slide came in (SlideMotion): every figure of it is looked at again,
    // changed or not.
    internal static void Enter(TextBlock block) => Queue(block);

    internal static bool IsFigure(DependencyObject d) => d.ReadLocalValue(ValueProperty) != DependencyProperty.UnsetValue;

    // Bindings of the new slide land one after the other: look at the figure
    // once they are all in (and once the slide host has its new Scope).
    private static void Queue(DependencyObject d)
    {
        if (d is not TextBlock block)
            return;
        var state = States.GetOrCreateValue(block);
        if (state.Queued)
            return;
        state.Queued = true;
        block.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Evaluate(block, state));
    }

    private static void Evaluate(TextBlock block, State state)
    {
        state.Queued = false;
        var value = ToDouble(GetValue(block));
        var scope = SlideMotion.ScopeOf(block);
        var key = scope != null && GetMetric(block) is { } metric && Window.GetWindow(block) is { } window
            ? $"{RuntimeHelpers.GetHashCode(window)}|{scope}|{metric}"
            : null;
        var now = DateTime.Now;
        var fx = TileAdorner.For(block);

        if (!state.Seen || state.Scope != scope)
        {
            // Another slide (or period / filter): count up from 0, then show
            // what changed since this slide was last on screen.
            state.Seen = true;
            state.Scope = scope;
            double? before = key != null && Memory.TryGetValue(key, out var memory) && now - memory.At < MemoryLife
                ? memory.Value : null;
            fx?.Reset();
            CountTo(block, 0, value, EntranceDelayMs);
            var shownAt = EntranceDelayMs + CountMs;
            if (fx != null)
            {
                ShowChange(block, fx, before, value, shownAt);
                fx.SetAlarm(IsAlarm(block), shownAt);
            }
        }
        else if (!Same(state.Value, value))
        {
            // Data refresh of the slide on screen: roll from what is shown.
            var from = state.Value.HasValue ? (double)block.GetValue(ShownProperty) : 0;
            CountTo(block, from, value, 0);
            if (fx != null)
            {
                ShowChange(block, fx, state.Value, value, 0);
                fx.SetAlarm(IsAlarm(block), 0);
            }
        }
        else
            fx?.SetAlarm(IsAlarm(block), 0);

        state.Value = value;
        if (key != null && value is { } v)
            Memory[key] = (v, now);
    }

    private static void CountTo(TextBlock block, double from, double? to, int delayMs)
    {
        block.BeginAnimation(ShownProperty, null);
        if (to is not { } target)
        {
            block.Text = "—";
            return;
        }
        block.SetValue(ShownProperty, from);
        block.Text = Format(block, from);
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(CountMs))
        {
            EasingFunction = EaseOut,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
        };
        // The last frame lands exactly on the value (and its exact text).
        animation.Completed += (_, _) =>
        {
            if (ToDouble(GetValue(block)) == target)
                block.Text = Format(block, target);
        };
        block.BeginAnimation(ShownProperty, animation);
    }

    private static void OnShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock block && ToDouble(GetValue(block)) != null)
            block.Text = Format(block, (double)e.NewValue);
    }

    private static void ShowChange(TextBlock block, TileAdorner fx, double? before, double? after, int delayMs)
    {
        if (before is not { } old || after is not { } now)
            return;
        var delta = now - old;
        if (Math.Abs(delta) < Math.Max(GetMinDelta(block), 1e-9) || Format(block, old) == Format(block, now))
            return;

        var better = GetBetter(block) switch
        {
            "Up" => Math.Sign(delta),
            "Down" => -Math.Sign(delta),
            "Zero" => Math.Sign(Math.Abs(old) - Math.Abs(now)),
            _ => 0,
        };
        var color = ThemeColor(better > 0 ? "SuccessBrush" : better < 0 ? "DangerBrush" : "AccentBrush");
        var text = GetDeltaFormat(block) is { } pattern
            ? (delta > 0 ? "▲ " : "▼ ") + string.Format(French, pattern, delta)
            : null;
        fx.Flash(color, text, delayMs);
        Pop(block, delayMs);
    }

    // The figure itself swells a little as its tile flashes.
    private static void Pop(TextBlock block, int delayMs)
    {
        var scale = new ScaleTransform(1, 1);
        block.RenderTransformOrigin = new Point(0, 0.5);
        block.RenderTransform = scale;
        var pop = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(delayMs) };
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), EaseOut));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650)), EaseOut));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    // In the red: the figure has the theme's danger colour (SeverityBrush /
    // PercentBandBrush converters).
    private static bool IsAlarm(TextBlock block)
        => block.Foreground is SolidColorBrush brush && ToDouble(GetValue(block)) != null
           && brush.Color == ThemeColor("DangerBrush");

    internal static Color ThemeColor(string key)
        => (Application.Current.TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.Gray;

    private static string Format(TextBlock block, double value) => string.Format(French, GetFormat(block), value);

    private static bool Same(double? a, double? b) => a == b;

    private static double? ToDouble(object? value) => value switch
    {
        null => null,
        double d when double.IsNaN(d) => null,
        double d => d,
        IConvertible c => c.ToDouble(CultureInfo.InvariantCulture),
        _ => null,
    };

    internal static IEasingFunction Ease => EaseOut;
}

// Overlays of one KPI tile: change flash, change pill, alarm outline. Opacity
// animations only (no effect, no re-render per frame): cheap in the software
// rendering of a remote desktop session.
internal sealed class TileAdorner : Adorner
{
    private static readonly DependencyProperty AdornerProperty = DependencyProperty.RegisterAttached(
        "TileAdorner", typeof(TileAdorner), typeof(TileAdorner), new PropertyMetadata(null));

    private readonly Grid _root = new();
    private readonly Border _alarm;
    private readonly Border _alarmHalo;
    private readonly Border _flash;
    private readonly Border _pill;
    private readonly TextBlock _pillText;
    private readonly TranslateTransform _pillShift = new();
    private bool _alarmOn;

    private TileAdorner(Border tile) : base(tile)
    {
        IsHitTestVisible = false;
        var radius = tile.CornerRadius;

        _alarmHalo = new Border { CornerRadius = radius, BorderThickness = new Thickness(6), Opacity = 0 };
        _alarm = new Border { CornerRadius = radius, BorderThickness = new Thickness(2), Opacity = 0 };
        _flash = new Border { CornerRadius = radius, BorderThickness = new Thickness(2), Opacity = 0 };
        _pillText = new TextBlock { FontSize = 14, FontWeight = FontWeights.Bold };
        _pill = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 2, 9, 2),
            Margin = new Thickness(0, 6, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = _pillText,
            Opacity = 0,
            RenderTransform = _pillShift,
        };
        _root.Children.Add(_alarmHalo);
        _root.Children.Add(_alarm);
        _root.Children.Add(_flash);
        _root.Children.Add(_pill);
        AddVisualChild(_root);

        // The main poste figures hide under a planned stop panel, etc.
        tile.IsVisibleChanged += (_, e) => Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Hidden;
        Visibility = tile.IsVisible ? Visibility.Visible : Visibility.Hidden;
    }

    // The overlay of the tile around `figure` (a KpiTile border), made the
    // first time; null outside a tile or a window.
    public static TileAdorner? For(DependencyObject figure)
    {
        var tile = FindTile(figure);
        if (tile == null)
            return null;
        if (tile.GetValue(AdornerProperty) is TileAdorner existing)
            return existing;
        var layer = AdornerLayer.GetAdornerLayer(tile);
        if (layer == null)
            return null;
        var adorner = new TileAdorner(tile);
        layer.Add(adorner);
        tile.SetValue(AdornerProperty, adorner);
        tile.Unloaded += OnTileUnloaded;
        return adorner;
    }

    private static void OnTileUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border tile || tile.GetValue(AdornerProperty) is not TileAdorner adorner)
            return;
        tile.Unloaded -= OnTileUnloaded;
        tile.ClearValue(AdornerProperty);
        adorner.StopAll();
        (VisualTreeHelper.GetParent(adorner) as AdornerLayer)?.Remove(adorner);
    }

    private static Border? FindTile(DependencyObject node)
    {
        var styles = new[] { "KpiTile", "KpiTileCompact" }
            .Select(k => Application.Current.TryFindResource(k)).Where(s => s != null).ToList();
        for (var current = VisualTreeHelper.GetParent(node); current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Border border && border.Style != null && styles.Contains(border.Style))
                return border;
        }
        return null;
    }

    public void Reset()
    {
        _flash.BeginAnimation(OpacityProperty, null);
        _flash.Opacity = 0;
        _pill.BeginAnimation(OpacityProperty, null);
        _pill.Opacity = 0;
        SetAlarm(false, 0);
    }

    private void StopAll()
    {
        Reset();
        _pillShift.BeginAnimation(TranslateTransform.YProperty, null);
    }

    public void Flash(Color color, string? text, int delayMs)
    {
        _flash.BorderBrush = new SolidColorBrush(color);
        _flash.Background = new SolidColorBrush(Color.FromArgb(0x2E, color.R, color.G, color.B));
        _flash.BeginAnimation(OpacityProperty, Keys(delayMs, (0, 0), (220, 1), (900, 1), (3200, 0)));

        if (text == null)
            return;
        _pillText.Text = text;
        _pillText.Foreground = new SolidColorBrush(color);
        _pill.Background = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B));
        _pill.BeginAnimation(OpacityProperty, Keys(delayMs, (0, 0), (300, 1), (9000, 1), (10000, 0)));
        _pillShift.BeginAnimation(TranslateTransform.YProperty, Keys(delayMs, (0, -8), (400, 0)));
        // Where the tile is, now that the slide's entrance is over.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => AdornerLayer.GetAdornerLayer(AdornedElement)?.Update(AdornedElement));
    }

    // Slow red breathing of the outline, from delayMs (after the count-up of
    // an entrance, so it never shows over a tile still fading in).
    public void SetAlarm(bool on, int delayMs)
    {
        if (on == _alarmOn)
            return;
        _alarmOn = on;
        if (!on)
        {
            _alarm.BeginAnimation(OpacityProperty, null);
            _alarmHalo.BeginAnimation(OpacityProperty, null);
            _alarm.Opacity = 0;
            _alarmHalo.Opacity = 0;
            return;
        }
        var red = TileFx.ThemeColor("DangerBrush");
        _alarm.BorderBrush = new SolidColorBrush(red);
        _alarmHalo.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, red.R, red.G, red.B));
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var begin = TimeSpan.FromMilliseconds(delayMs);
        _alarm.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(1400))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease, BeginTime = begin });
        _alarmHalo.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.9, TimeSpan.FromMilliseconds(1400))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease, BeginTime = begin });
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => AdornerLayer.GetAdornerLayer(AdornedElement)?.Update(AdornedElement));
    }

    // Holds the first value until delayMs, then goes through the (ms, value) frames.
    private static DoubleAnimationUsingKeyFrames Keys(int delayMs, params (int Ms, double Value)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(frames[0].Value, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        foreach (var (ms, value) in frames)
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs + ms)), TileFx.Ease));
        return animation;
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _root;

    protected override Size MeasureOverride(Size constraint)
    {
        _root.Measure(AdornedElement.RenderSize);
        return AdornedElement.RenderSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _root.Arrange(new Rect(AdornedElement.RenderSize));
        return finalSize;
    }
}
