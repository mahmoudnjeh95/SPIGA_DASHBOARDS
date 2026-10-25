using System.Windows;
using System.Windows.Controls;
using LiveChartsCore.Measure;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.Views;

public partial class ChartCard : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ChartCard), new PropertyMetadata(""));

    public static readonly DependencyProperty ChartProperty =
        DependencyProperty.Register(nameof(Chart), typeof(ChartParts), typeof(ChartCard), new PropertyMetadata(null));

    public static readonly DependencyProperty ShowLegendProperty =
        DependencyProperty.Register(nameof(ShowLegend), typeof(bool), typeof(ChartCard),
            new PropertyMetadata(false, (d, e) => ((ChartCard)d).ChartView.LegendPosition = (bool)e.NewValue ? LegendPosition.Top : LegendPosition.Hidden));

    public ChartCard()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public ChartParts? Chart
    {
        get => (ChartParts?)GetValue(ChartProperty);
        set => SetValue(ChartProperty, value);
    }

    public bool ShowLegend
    {
        get => (bool)GetValue(ShowLegendProperty);
        set => SetValue(ShowLegendProperty, value);
    }
}
