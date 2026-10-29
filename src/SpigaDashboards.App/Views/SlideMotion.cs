using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        host.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => CascadeTiles(host));
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

    private static Duration Ms(int ms) => new(TimeSpan.FromMilliseconds(ms));
}
