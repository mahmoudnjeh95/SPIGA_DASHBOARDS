using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.Measure;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.Views;

public partial class ChartCard : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ChartCard), new PropertyMetadata(""));

    public static readonly DependencyProperty ChartProperty =
        DependencyProperty.Register(nameof(Chart), typeof(ChartParts), typeof(ChartCard),
            new PropertyMetadata(null, (d, _) => ((ChartCard)d).Apply()));

    public static readonly DependencyProperty ShowLegendProperty =
        DependencyProperty.Register(nameof(ShowLegend), typeof(bool), typeof(ChartCard),
            new PropertyMetadata(false, (d, _) => ((ChartCard)d).Apply()));

    // Series on screen, and the slide scope (SlideMotion.Scope) they belong to.
    private ISeries[] _series = Array.Empty<ISeries>();
    private string? _scope;

    // Copied from a refreshed series onto the one on screen: its points and
    // how it prints them. Paints stay (same names = same colours).
    private static readonly string[] MovedProperties =
        { "Values", "DataLabelsFormatter", "DataLabelsSize", "GeometrySize", "MaxBarWidth" };

    public ChartCard()
    {
        InitializeComponent();
        Loaded += (_, _) => _scope ??= SlideMotion.ScopeOf(this);
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

    // A new slide grows its charts from the baseline (new series). A data
    // refresh of the slide on screen gives the series already drawn their
    // new values instead, so the bars and points move to them rather than
    // the whole chart being drawn again every minute.
    private void Apply()
    {
        var parts = Chart;
        var scope = SlideMotion.ScopeOf(this);
        if (parts == null)
        {
            _series = Array.Empty<ISeries>();
            ChartView.Series = _series;
            _scope = scope;
            return;
        }

        if (scope != null && scope == _scope && SameShape(_series, parts.Series))
        {
            for (var i = 0; i < _series.Length; i++)
                Move(parts.Series[i], _series[i]);
        }
        else
        {
            _series = parts.Series;
            ChartView.Series = _series;
        }
        ChartView.XAxes = parts.XAxes;
        ChartView.YAxes = parts.YAxes;
        // Legend in the title row, and only with two series or more (a single
        // weight's "800 g" says nothing the title doesn't).
        var entries = parts.Series.Where(s => s.IsVisibleAtLegend)
            .Select(s => new LegendEntry(s.Name ?? "", SeriesBrush(s))).ToList();
        Legend.ItemsSource = ShowLegend && entries.Count >= 2 ? entries : null;
        _scope = scope;
    }

    // Bound OneTime in ChartCard.xaml: a binding that watches a property of
    // an object without change notification goes through a static
    // PropertyDescriptor table, and an unfrozen brush drawn by the legend dot
    // points back at that dot, so every slide that ever showed a legend (its
    // charts, bitmaps and the whole panel) stayed in memory: 8 GB + 3.5 GB of
    // graphics memory after 49 h on the hall screen (15/11/2026).
    public sealed record LegendEntry(string Name, Brush Brush);

    // The series' colour: its line for a line, its fill for bars. Frozen (see
    // LegendEntry).
    private static Brush SeriesBrush(ISeries series)
    {
        foreach (var name in new[] { "Stroke", "Fill" })
        {
            if (Property(series.GetType(), name)?.GetValue(series) is SolidColorPaint { Color: var c })
            {
                var brush = new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));
                brush.Freeze();
                return brush;
            }
        }
        return Brushes.Gray;
    }

    private static bool SameShape(IReadOnlyList<ISeries> shown, IReadOnlyList<ISeries> next)
        => shown.Count == next.Count && shown.Count > 0
           && shown.Zip(next).All(p => p.First.GetType() == p.Second.GetType() && p.First.Name == p.Second.Name);

    private static void Move(ISeries from, ISeries to)
    {
        // Same concrete type (SameShape). A name may be declared at several
        // levels (generic series hiding a base property): the most derived wins.
        foreach (var name in MovedProperties)
        {
            if (Property(to.GetType(), name) is { CanWrite: true } property)
                property.SetValue(to, property.GetValue(from));
        }
    }

    private static System.Reflection.PropertyInfo? Property(Type type, string name)
        => type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.Name == name && p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderByDescending(p => Depth(p.DeclaringType))
            .FirstOrDefault();

    private static int Depth(Type? type)
    {
        var depth = 0;
        for (; type != null; type = type.BaseType)
            depth++;
        return depth;
    }
}
