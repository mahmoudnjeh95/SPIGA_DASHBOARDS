using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace SpigaDashboards.App.Views;

// Motion of the slide deck, shared by MainWindow and TvWindow. Purely visual:
// nothing here reads or changes what a slide shows.
//  - Transition on the slide host: bound to MainViewModel.SlideChangeCount,
//    which only moves when another slide comes up (not on the periodic data
//    refresh), so the new slide slides in and its tiles (KpiTile / ChartTile
//    borders) fade up one after the other.
//  - Timer on a thin bar: bound to MainViewModel.RotationCycle, it fills from
//    left to right over Seconds (the slide duration), and hides while Paused.
public static class SlideMotion
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    // ---- Slide transition ----

    public static readonly DependencyProperty TransitionProperty = DependencyProperty.RegisterAttached(
        "Transition", typeof(int), typeof(SlideMotion), new PropertyMetadata(0, OnTransitionChanged));

    public static int GetTransition(DependencyObject d) => (int)d.GetValue(TransitionProperty);
    public static void SetTransition(DependencyObject d, int value) => d.SetValue(TransitionProperty, value);

    private static void OnTransitionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement host || (int)e.NewValue == 0)
            return;

        var shift = new TranslateTransform(36, 0);
        host.RenderTransform = shift;
        host.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(420)) { EasingFunction = EaseOut });
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(36, 0, Ms(520)) { EasingFunction = EaseOut });

        // The new slide's tiles exist once its template is applied.
        host.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            CascadeTiles(host);
            EnterFigures(host);
        });
    }

    // Figures (TileFx) count up from 0 and TRS rings fill up, with the tiles.
    private static void EnterFigures(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is TextBlock block && TileFx.IsFigure(block))
                TileFx.Enter(block);
            else if (child is TrsRing ring)
                ring.Enter();
            EnterFigures(child);
        }
    }

    // ---- Scope of the slide on screen ----
    // Set on the slide host, bound to MainViewModel.SlideScope: which slide,
    // period, filters and poste the figures are about. TileFx compares a
    // figure only with the last one shown in the same scope, and ChartCard
    // moves its bars to their new values only within the same scope.

    public static readonly DependencyProperty ScopeProperty = DependencyProperty.RegisterAttached(
        "Scope", typeof(string), typeof(SlideMotion), new PropertyMetadata(null));

    public static string? GetScope(DependencyObject d) => (string?)d.GetValue(ScopeProperty);
    public static void SetScope(DependencyObject d, string? value) => d.SetValue(ScopeProperty, value);

    // The Scope of the slide host `d` is in (null outside one).
    public static string? ScopeOf(DependencyObject d)
    {
        for (var current = d; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (GetScope(current) is { } scope)
                return scope;
        }
        return null;
    }

    // ---- Bars that drop in (stop bar, "REPRISE" bar) ----

    public static void DropIn(FrameworkElement element)
    {
        var shift = new TranslateTransform(0, -14);
        var scale = new ScaleTransform(1, 0.6);
        element.RenderTransformOrigin = new Point(0.5, 0);
        element.RenderTransform = new TransformGroup { Children = { scale, shift } };
        var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(300)) { EasingFunction = EaseOut });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-14, 0, Ms(480)) { EasingFunction = back });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, 1, Ms(480)) { EasingFunction = back });
    }

    // ---- Shift change: the team / poste chip announces it ----
    // Announce="{Binding CurrentTeamLabel}": when the value changes (not the
    // first time it is set), the element pops, glows three times in the accent
    // colour, and its first TextBlock flips over to the new text.

    public static readonly DependencyProperty AnnounceProperty = DependencyProperty.RegisterAttached(
        "Announce", typeof(object), typeof(SlideMotion), new PropertyMetadata(null, OnAnnounceChanged));

    public static object? GetAnnounce(DependencyObject d) => d.GetValue(AnnounceProperty);
    public static void SetAnnounce(DependencyObject d, object? value) => d.SetValue(AnnounceProperty, value);

    private static void OnAnnounceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || !element.IsLoaded
            || e.OldValue is null or "" || Equals(e.OldValue, e.NewValue))
            return;

        var pop = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = pop;
        var swell = new DoubleAnimationUsingKeyFrames();
        swell.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), EaseOut));
        swell.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)),
            new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 5 }));
        pop.BeginAnimation(ScaleTransform.ScaleXProperty, swell);
        pop.BeginAnimation(ScaleTransform.ScaleYProperty, swell);

        // A glow for a few seconds only: an effect costs in software rendering.
        var accent = (Application.Current.TryFindResource("AccentBrush") as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        var glow = new DropShadowEffect { Color = accent, ShadowDepth = 0, BlurRadius = 0, Opacity = 1 };
        element.Effect = glow;
        var pulses = new DoubleAnimation(0, 26, Ms(450))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        pulses.Completed += (_, _) =>
        {
            if (element.Effect == glow)
                element.Effect = null;
        };
        glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, pulses);

        if (FirstText(element) is { } text)
        {
            var flip = new ScaleTransform(1, 0);
            text.RenderTransformOrigin = new Point(0.5, 0.5);
            text.RenderTransform = flip;
            flip.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0, 1, Ms(520))
                { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } });
        }
    }

    private static TextBlock? FirstText(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is TextBlock text)
                return text;
            if (FirstText(child) is { } found)
                return found;
        }
        return null;
    }

    private static void CascadeTiles(FrameworkElement host)
    {
        var tiles = new List<Border>();
        CollectTiles(host, tiles);
        var keys = new[] { "KpiTile", "KpiTileCompact", "ChartTile" }
            .Select(k => Application.Current.TryFindResource(k)).Where(s => s != null).ToHashSet();

        var delay = 0;
        foreach (var tile in tiles.Where(t => keys.Contains(t.Style)).Take(24))
        {
            var lift = new TranslateTransform(0, 14);
            tile.RenderTransform = lift;
            var begin = TimeSpan.FromMilliseconds(120 + delay);
            // Hidden until its turn; FillBehavior.Stop hands the opacity back to the style at the end.
            var fade = Delayed(0, 1, begin, 380);
            fade.FillBehavior = FillBehavior.Stop;
            tile.BeginAnimation(UIElement.OpacityProperty, fade);
            lift.BeginAnimation(TranslateTransform.YProperty, Delayed(14, 0, begin, 460));
            delay += 45;
        }
    }

    // Holds `from` until `begin`, then eases to `to` in `ms`.
    private static DoubleAnimationUsingKeyFrames Delayed(double from, double to, TimeSpan begin, int ms)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(begin)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(begin + TimeSpan.FromMilliseconds(ms)), EaseOut));
        return animation;
    }

    private static void CollectTiles(DependencyObject node, List<Border> tiles)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is Border border && border.Style != null)
                tiles.Add(border);
            CollectTiles(child, tiles);
        }
    }

    // ---- Slide timer bar ----

    public static readonly DependencyProperty TimerProperty = DependencyProperty.RegisterAttached(
        "Timer", typeof(int), typeof(SlideMotion), new PropertyMetadata(0, OnTimerChanged));

    public static int GetTimer(DependencyObject d) => (int)d.GetValue(TimerProperty);
    public static void SetTimer(DependencyObject d, int value) => d.SetValue(TimerProperty, value);

    public static readonly DependencyProperty SecondsProperty = DependencyProperty.RegisterAttached(
        "Seconds", typeof(double), typeof(SlideMotion), new PropertyMetadata(15.0));

    public static double GetSeconds(DependencyObject d) => (double)d.GetValue(SecondsProperty);
    public static void SetSeconds(DependencyObject d, double value) => d.SetValue(SecondsProperty, value);

    public static readonly DependencyProperty PausedProperty = DependencyProperty.RegisterAttached(
        "Paused", typeof(bool), typeof(SlideMotion), new PropertyMetadata(false, OnTimerChanged));

    public static bool GetPaused(DependencyObject d) => (bool)d.GetValue(PausedProperty);
    public static void SetPaused(DependencyObject d, bool value) => d.SetValue(PausedProperty, value);

    private static void OnTimerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement bar)
            return;

        if (bar.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            scale = new ScaleTransform(0, 1);
            bar.RenderTransform = scale;
            bar.RenderTransformOrigin = new Point(0, 0.5);
        }

        if (GetPaused(bar) || GetTimer(bar) == 0)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.ScaleX = 0;
            return;
        }

        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromSeconds(Math.Max(1, GetSeconds(bar)))));
    }

    // ---- Bars that grow in (Planification statistics) ----
    // Grow="X" grows the element from its left edge, "Y" from its bottom,
    // each time it is loaded. A transform of its own, created here: one set in
    // a template would be frozen and shared, and couldn't be animated.

    public static readonly DependencyProperty GrowProperty = DependencyProperty.RegisterAttached(
        "Grow", typeof(string), typeof(SlideMotion), new PropertyMetadata(null, OnGrowChanged));

    public static string? GetGrow(DependencyObject d) => (string?)d.GetValue(GrowProperty);
    public static void SetGrow(DependencyObject d, string? value) => d.SetValue(GrowProperty, value);

    private static void OnGrowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && e.OldValue == null && e.NewValue != null)
            element.Loaded += (_, _) => Grow(element);
    }

    private static void Grow(FrameworkElement element)
    {
        var vertical = GetGrow(element) == "Y";
        var scale = new ScaleTransform(vertical ? 1 : 0, vertical ? 0 : 1);
        element.RenderTransformOrigin = vertical ? new Point(0.5, 1) : new Point(0, 0.5);
        element.RenderTransform = scale;
        var animation = new DoubleAnimation(0, 1, Ms(750)) { EasingFunction = EaseOut, BeginTime = TimeSpan.FromMilliseconds(80) };
        scale.BeginAnimation(vertical ? ScaleTransform.ScaleYProperty : ScaleTransform.ScaleXProperty, animation);
    }

    private static Duration Ms(int ms) => new(TimeSpan.FromMilliseconds(ms));
}
