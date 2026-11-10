using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SpigaDashboards.App.Views;

// Green "REPRISE" bar of a line whose stop just ended (MachineHeader).
public partial class RecoveryBar : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(RecoveryBar), new PropertyMetadata(""));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public RecoveryBar()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Play);
        };
    }

    private void Play()
    {
        if (!IsVisible)
            return;
        SlideMotion.DropIn(this);

        var pop = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(250) };
        pop.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)),
            new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 4 }));
        CheckScale.ScaleX = 0;
        CheckScale.ScaleY = 0;
        CheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        CheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        // Two sweeps of light from left to right, then nothing moves.
        var width = Math.Max(ActualWidth, 400);
        var cycle = TimeSpan.FromMilliseconds(1900);
        var sweep = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(500), Duration = cycle, RepeatBehavior = new RepeatBehavior(2) };
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(-180, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sweep.KeyFrames.Add(new EasingDoubleKeyFrame(width + 20, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300)),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        SweepShift.BeginAnimation(TranslateTransform.XProperty, null);
        SweepShift.X = -180;
        SweepShift.BeginAnimation(TranslateTransform.XProperty, sweep);
        Sweep.Opacity = 1;
    }
}
