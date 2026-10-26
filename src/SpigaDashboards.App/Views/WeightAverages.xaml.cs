using System.Windows;
using System.Windows.Controls;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Views;

public partial class WeightAverages : UserControl
{
    public static readonly DependencyProperty WeightsProperty =
        DependencyProperty.Register(nameof(Weights), typeof(IReadOnlyList<WeightKpi>), typeof(WeightAverages),
            new PropertyMetadata(null, (d, _) => ((WeightAverages)d).Update()));

    public static readonly DependencyProperty LargeFontSizeProperty =
        DependencyProperty.Register(nameof(LargeFontSize), typeof(double), typeof(WeightAverages),
            new PropertyMetadata(44.0, (d, _) => ((WeightAverages)d).Update()));

    public static readonly DependencyProperty SmallFontSizeProperty =
        DependencyProperty.Register(nameof(SmallFontSize), typeof(double), typeof(WeightAverages),
            new PropertyMetadata(24.0, (d, _) => ((WeightAverages)d).Update()));

    public static readonly DependencyProperty EffectiveFontSizeProperty =
        DependencyProperty.Register(nameof(EffectiveFontSize), typeof(double), typeof(WeightAverages), new PropertyMetadata(44.0));

    // Cards too narrow for "503,4 g / 500 g" hide the target when there is a
    // single weight (it is in the weight table); several weights keep it.
    public static readonly DependencyProperty ShowTargetWhenSingleProperty =
        DependencyProperty.Register(nameof(ShowTargetWhenSingle), typeof(bool), typeof(WeightAverages),
            new PropertyMetadata(true, (d, _) => ((WeightAverages)d).Update()));

    public static readonly DependencyProperty TargetVisibilityProperty =
        DependencyProperty.Register(nameof(TargetVisibility), typeof(Visibility), typeof(WeightAverages), new PropertyMetadata(Visibility.Visible));

    public static readonly DependencyProperty TargetFontSizeProperty =
        DependencyProperty.Register(nameof(TargetFontSize), typeof(double), typeof(WeightAverages), new PropertyMetadata(16.0));

    public WeightAverages()
    {
        InitializeComponent();
        Update();
    }

    public IReadOnlyList<WeightKpi>? Weights
    {
        get => (IReadOnlyList<WeightKpi>?)GetValue(WeightsProperty);
        set => SetValue(WeightsProperty, value);
    }

    public double LargeFontSize
    {
        get => (double)GetValue(LargeFontSizeProperty);
        set => SetValue(LargeFontSizeProperty, value);
    }

    public double SmallFontSize
    {
        get => (double)GetValue(SmallFontSizeProperty);
        set => SetValue(SmallFontSizeProperty, value);
    }

    public bool ShowTargetWhenSingle
    {
        get => (bool)GetValue(ShowTargetWhenSingleProperty);
        set => SetValue(ShowTargetWhenSingleProperty, value);
    }

    public Visibility TargetVisibility => (Visibility)GetValue(TargetVisibilityProperty);
    public double EffectiveFontSize => (double)GetValue(EffectiveFontSizeProperty);
    public double TargetFontSize => (double)GetValue(TargetFontSizeProperty);

    // One weight: big value; several: one smaller line each. No weight: "—".
    private void Update()
    {
        if (Items == null)
            return;
        var count = Weights?.Count ?? 0;
        var size = count > 1 ? SmallFontSize : LargeFontSize;
        SetValue(EffectiveFontSizeProperty, size);
        SetValue(TargetFontSizeProperty, Math.Max(12, Math.Round(size * 0.45)));
        SetValue(TargetVisibilityProperty, count > 1 || ShowTargetWhenSingle ? Visibility.Visible : Visibility.Collapsed);
        Empty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
