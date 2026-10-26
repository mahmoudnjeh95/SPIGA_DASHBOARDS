using System.Windows;
using System.Windows.Controls;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Views;

public partial class WeightTable : UserControl
{
    public static readonly DependencyProperty WeightsProperty =
        DependencyProperty.Register(nameof(Weights), typeof(IReadOnlyList<WeightKpi>), typeof(WeightTable), new PropertyMetadata(null));

    public static readonly DependencyProperty ValueFontSizeProperty =
        DependencyProperty.Register(nameof(ValueFontSize), typeof(double), typeof(WeightTable), new PropertyMetadata(20.0));

    public static readonly DependencyProperty HeaderFontSizeProperty =
        DependencyProperty.Register(nameof(HeaderFontSize), typeof(double), typeof(WeightTable), new PropertyMetadata(14.0));

    public static readonly DependencyProperty CompactProperty =
        DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(WeightTable),
            new PropertyMetadata(false, (d, e) => d.SetValue(DetailVisibilityProperty, (bool)e.NewValue ? Visibility.Collapsed : Visibility.Visible)));

    public static readonly DependencyProperty DetailVisibilityProperty =
        DependencyProperty.Register(nameof(DetailVisibility), typeof(Visibility), typeof(WeightTable), new PropertyMetadata(Visibility.Visible));

    public WeightTable()
    {
        InitializeComponent();
    }

    public IReadOnlyList<WeightKpi>? Weights
    {
        get => (IReadOnlyList<WeightKpi>?)GetValue(WeightsProperty);
        set => SetValue(WeightsProperty, value);
    }

    // One line per weight (product name and deviation % hidden).
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public Visibility DetailVisibility => (Visibility)GetValue(DetailVisibilityProperty);

    public double ValueFontSize
    {
        get => (double)GetValue(ValueFontSizeProperty);
        set => SetValue(ValueFontSizeProperty, value);
    }

    public double HeaderFontSize
    {
        get => (double)GetValue(HeaderFontSizeProperty);
        set => SetValue(HeaderFontSizeProperty, value);
    }
}
