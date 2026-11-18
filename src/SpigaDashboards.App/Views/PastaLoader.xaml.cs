using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SpigaDashboards.App.Views;

// Loading motion: bind IsActive to the view-model's "loading" flag. It only
// shows if the load lasts (Delay), so a fast slide change never flashes it,
// and it only animates while shown (the kiosk runs all day). Background, if
// set, veils what is behind it.
// Entrance: the card pops up (small overshoot, ease-out), exit: it fades and
// sinks a little (ease-in, shorter), so the data does not appear behind a
// hard cut. With Windows animation effects off, it shows still and leaves at
// once.
public partial class PastaLoader : UserControl
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(400);
    private static readonly Duration Cycle = new(TimeSpan.FromSeconds(1.2));
    private static readonly Duration EnterTime = new(TimeSpan.FromMilliseconds(420));
    private static readonly Duration ExitTime = new(TimeSpan.FromMilliseconds(200));
    private static readonly IEasingFunction EnterEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
    private static readonly IEasingFunction ExitEase = new CubicEase { EasingMode = EasingMode.EaseIn };

    private static bool Motion => SystemParameters.ClientAreaAnimation;

    private bool _shown;

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(PastaLoader),
        new PropertyMetadata(false, (d, _) => ((PastaLoader)d).Update()));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(PastaLoader),
        new PropertyMetadata("Chargement des données…", (d, e) => ((PastaLoader)d).Caption.Text = (string)e.NewValue));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private readonly DispatcherTimer _delay;

    public PastaLoader()
    {
        InitializeComponent();
        Caption.Text = Text;
        Visibility = Visibility.Collapsed;
        _delay = new DispatcherTimer { Interval = Delay };
        _delay.Tick += (_, _) =>
        {
            _delay.Stop();
            Show();
        };
        // A tab switched away and back while still loading starts over.
        Loaded += (_, _) => Update();
        Unloaded += (_, _) =>
        {
            _delay.Stop();
            Hide(animate: false);
        };
    }

    private void Update()
    {
        if (IsActive)
        {
            if (!_shown)
                _delay.Start();
        }
        else
        {
            _delay.Stop();
            Hide(animate: true);
        }
    }

    private void Show()
    {
        _shown = true;
        StopAll();
        Visibility = Visibility.Visible;
        if (!Motion)
        {
            Opacity = 1;
            return;
        }
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1, EnterTime) { EasingFunction = EnterEase });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, EnterTime) { EasingFunction = EnterEase });
        CardShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, EnterTime) { EasingFunction = EnterEase });
        // The caption breathes, so a long wait still reads as "working".
        Caption.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.55, TimeSpan.FromSeconds(0.9))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
        // One after the other, like dots of a loader.
        Hop(CouscousHop, CouscousScale, CouscousTurn, Shadow1, Shadow1Scale, 0, 0);
        Hop(PenneHop, PenneScale, PenneTurn, Shadow2, Shadow2Scale, 0.18, 24);
        Hop(NestHop, NestScale, NestTurn, Shadow3, Shadow3Scale, 0.36, -18);
        // The strand: a dash running along the wave (16 + 40 = one pattern, in stroke widths).
        Strand.BeginAnimation(Shape.StrokeDashOffsetProperty,
            new DoubleAnimation(0, -56, Cycle) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void Hide(bool animate)
    {
        if (!_shown)
            return;
        _shown = false;
        if (!animate || !Motion || !IsLoaded)
        {
            StopAll();
            Visibility = Visibility.Collapsed;
            return;
        }
        // Fade and sink, then stop the loops - unless it was shown again
        // meanwhile (a new load right after the previous one).
        var fade = new DoubleAnimation(0, ExitTime) { EasingFunction = ExitEase };
        fade.Completed += (_, _) =>
        {
            if (_shown)
                return;
            StopAll();
            Visibility = Visibility.Collapsed;
        };
        CardShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, ExitTime) { EasingFunction = ExitEase });
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, ExitTime) { EasingFunction = ExitEase });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, ExitTime) { EasingFunction = ExitEase });
        BeginAnimation(OpacityProperty, fade);
    }

    private void StopAll()
    {
        BeginAnimation(OpacityProperty, null);
        Caption.BeginAnimation(OpacityProperty, null);
        CardShift.BeginAnimation(TranslateTransform.YProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        foreach (var t in new[] { CouscousHop, PenneHop, NestHop })
            t.BeginAnimation(TranslateTransform.YProperty, null);
        foreach (var s in new[] { CouscousScale, PenneScale, NestScale, Shadow1Scale, Shadow2Scale, Shadow3Scale })
        {
            s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
        foreach (var r in new[] { CouscousTurn, PenneTurn, NestTurn })
            r.BeginAnimation(RotateTransform.AngleProperty, null);
        foreach (var e in new UIElement[] { Shadow1, Shadow2, Shadow3 })
            e.BeginAnimation(OpacityProperty, null);
        Strand.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
    }

    // A hop in the first half of the cycle (stretch on take-off, squash on
    // landing, a little spin in the air), still in the second half.
    private static void Hop(TranslateTransform hop, ScaleTransform scale, RotateTransform turn,
        UIElement shadow, ScaleTransform shadowScale, double delay, double spin)
    {
        var begin = TimeSpan.FromSeconds(delay);
        hop.BeginAnimation(TranslateTransform.YProperty, Keys(begin,
            (0, 0, null), (0.3, -30, new QuadraticEase { EasingMode = EasingMode.EaseOut }),
            (0.6, 0, new QuadraticEase { EasingMode = EasingMode.EaseIn }), (1.2, 0, null)));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Keys(begin,
            (0, 1, null), (0.12, 1.1, null), (0.3, 1, null), (0.58, 1, null), (0.66, 0.78, null), (0.82, 1, null), (1.2, 1, null)));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Keys(begin,
            (0, 1, null), (0.12, 0.93, null), (0.3, 1, null), (0.58, 1, null), (0.66, 1.16, null), (0.82, 1, null), (1.2, 1, null)));
        turn.BeginAnimation(RotateTransform.AngleProperty, Keys(begin,
            (0, 0, null), (0.3, spin, new SineEase()), (0.6, 0, new SineEase()), (1.2, 0, null)));
        shadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, Keys(begin,
            (0, 1, null), (0.3, 0.5, new QuadraticEase { EasingMode = EasingMode.EaseOut }),
            (0.6, 1, new QuadraticEase { EasingMode = EasingMode.EaseIn }), (1.2, 1, null)));
        shadow.BeginAnimation(OpacityProperty, Keys(begin,
            (0, 0.22, null), (0.3, 0.08, null), (0.6, 0.22, null), (1.2, 0.22, null)));
    }

    private static DoubleAnimationUsingKeyFrames Keys(TimeSpan begin, params (double At, double Value, IEasingFunction? Ease)[] keys)
    {
        var animation = new DoubleAnimationUsingKeyFrames { BeginTime = begin, Duration = Cycle, RepeatBehavior = RepeatBehavior.Forever };
        foreach (var (at, value, ease) in keys)
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at)), ease));
        return animation;
    }
}
