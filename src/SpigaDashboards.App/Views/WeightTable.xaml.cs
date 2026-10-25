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

    public WeightTable()
    {
        InitializeComponent();
    }

    public IReadOnlyList<WeightKpi>? Weights
    {
        get => (IReadOnlyList<WeightKpi>?)GetValue(WeightsProperty);
        set => SetValue(WeightsProperty, value);
    }

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
