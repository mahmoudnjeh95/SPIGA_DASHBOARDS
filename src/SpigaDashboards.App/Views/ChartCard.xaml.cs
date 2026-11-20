using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.Views;

// Every chart of the application. Besides drawing a ChartParts, it lets
// whoever has a mouse change how the chart looks: kind (bars, line, points,
// area), which series are shown (toolbar or click on the legend), and an
// enlarged view with zoom (toolbar or double-click). The choice is kept per
// chart title on this PC (ChartPrefs). A new chart gets all of it by being
// shown in a ChartCard.
public partial class ChartCard : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ChartCard),
            new PropertyMetadata("", (d, _) => ((ChartCard)d).Apply()));

    public static readonly DependencyProperty ChartProperty =
        DependencyProperty.Register(nameof(Chart), typeof(ChartParts), typeof(ChartCard),
            new PropertyMetadata(null, (d, _) => ((ChartCard)d).Apply()));

    public static readonly DependencyProperty ShowLegendProperty =
        DependencyProperty.Register(nameof(ShowLegend), typeof(bool), typeof(ChartCard),
            new PropertyMetadata(false, (d, _) => ((ChartCard)d).Apply()));

    // The enlarged view (ChartZoomWindow): toolbar always shown, legend with
    // two series or more, mouse-wheel zoom and drag to pan along the X axis.
    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(ChartCard),
            new PropertyMetadata(false, (d, _) => ((ChartCard)d).OnExpandedChanged()));

    // Inside a card that has its own heading (Planification > Statistiques):
    // no tile border, no title row text; Title still names the chart (its
    // preferences, the enlarged view).
    public static readonly DependencyProperty PlainProperty =
        DependencyProperty.Register(nameof(Plain), typeof(bool), typeof(ChartCard),
            new PropertyMetadata(false, (d, _) => ((ChartCard)d).OnPlainChanged()));

    public bool Plain
    {
        get => (bool)GetValue(PlainProperty);
        set => SetValue(PlainProperty, value);
    }

    private void OnPlainChanged()
    {
        if (Plain)
        {
            Tile.BorderThickness = new Thickness(0);
            Tile.Padding = new Thickness(0);
            Tile.Margin = new Thickness(0);
            Tile.Background = Brushes.Transparent;
            TitleText.Visibility = Visibility.Collapsed;
        }
        else
        {
            Tile.ClearValue(Border.BorderThicknessProperty);
            Tile.ClearValue(Border.PaddingProperty);
            Tile.ClearValue(MarginProperty);
            Tile.ClearValue(Border.BackgroundProperty);
            TitleText.ClearValue(VisibilityProperty);
        }
    }

    // Series on screen, and the slide scope (SlideMotion.Scope) they belong to.
    private ISeries[] _series = Array.Empty<ISeries>();
    private string? _scope;
    private ChartKind _kind = ChartKind.Auto;

    // Copied from a refreshed series onto the one on screen: its points and
    // how it prints them. Paints stay (same names = same colours).
    private static readonly string[] MovedProperties =
        { "Values", "DataLabelsFormatter", "DataLabelsSize", "GeometrySize", "MaxBarWidth" };

    public ChartCard()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Software rendering: the chart is drawn at once, without its
            // growing animation (redrawn by the CPU every frame).
            if (Services.RenderBudget.IsSoftware)
                ChartView.AnimationsSpeed = TimeSpan.Zero;
            _scope ??= SlideMotion.ScopeOf(this);
            ChartPrefs.Changed -= OnPrefChanged;   // Loaded can come twice
            ChartPrefs.Changed += OnPrefChanged;
        };
        Unloaded += (_, _) => ChartPrefs.Changed -= OnPrefChanged;
        MouseEnter += (_, _) => ShowTools(true);
        MouseLeave += (_, _) => ShowTools(false);
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

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    // Preferences are kept per chart: its title, or its series' names
    // (digits dropped: "Cible 65 %" and "Cible 70 %" are the same chart).
    private string PrefKey
        => !string.IsNullOrWhiteSpace(Title) ? Title
            : Normalize(string.Join("|", Chart?.Series.Select(s => s.Name ?? "") ?? Enumerable.Empty<string>()));

    // Hidden series are kept the same way: "Cible 65 %" hidden on one
    // machine stays hidden on the next one whose target is 70 %.
    private static string Normalize(string name)
        => new string(name.Where(c => !char.IsDigit(c) && !char.IsWhiteSpace(c)).ToArray());

    private static bool IsHidden(ChartPref pref, string? name)
        => name != null && pref.Hidden.Contains(Normalize(name));

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
            UpdateTools(null, new ChartPref());
            return;
        }

        var pref = ChartPrefs.Get(PrefKey);
        _kind = ChartStyler.CanChangeKind(parts.Series) ? pref.Kind : ChartKind.Auto;
        var yLabeler = parts.YAxes.FirstOrDefault()?.Labeler;
        // The enlarged view draws copies: a series or an axis belongs to one chart.
        var next = ChartStyler.Render(parts.Series, _kind, yLabeler, copy: IsExpanded);

        if (scope != null && scope == _scope && SameShape(_series, next))
        {
            for (var i = 0; i < _series.Length; i++)
                Move(next[i], _series[i]);
        }
        else
        {
            _series = next;
            ChartView.Series = _series;
        }
        foreach (var s in _series)
            s.IsVisible = !IsHidden(pref, s.Name);

        if (IsExpanded)
        {
            ChartView.XAxes = ChartStyler.Clone(parts.XAxes);
            ChartView.YAxes = ChartStyler.Clone(parts.YAxes);
        }
        else
        {
            ChartView.XAxes = parts.XAxes;
            ChartView.YAxes = parts.YAxes;
        }

        // Legend in the title row, and only with two series or more (a single
        // weight's "800 g" says nothing the title doesn't).
        var entries = _series.Where(s => s.IsVisibleAtLegend && !string.IsNullOrEmpty(s.Name))
            .GroupBy(s => s.Name!).Select(g => g.First())
            .Select(s => new LegendEntry(s.Name!, SeriesBrush(s), IsHidden(pref, s.Name))).ToList();
        Legend.ItemsSource = (ShowLegend || IsExpanded || Plain) && entries.Count >= 2 ? entries : null;
        _scope = scope;
        UpdateTools(parts, pref);
    }

    // Reset the enlarged view's zoom: fresh copies of the axes.
    public void ResetZoom()
    {
        if (Chart is { } parts && IsExpanded)
        {
            ChartView.XAxes = ChartStyler.Clone(parts.XAxes);
            ChartView.YAxes = ChartStyler.Clone(parts.YAxes);
        }
    }

    private void OnPrefChanged(string key)
    {
        if (key == PrefKey)
            Apply();
    }

    private void OnExpandedChanged()
    {
        ChartView.ZoomMode = IsExpanded ? ZoomAndPanMode.X : ZoomAndPanMode.None;
        // Tiles are short of room: icons only there, with words when enlarged.
        KindLabel.Visibility = SeriesLabel.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        ShowTools(IsExpanded, animate: false);
        Apply();
    }

    // ---------- toolbar ----------

    private void UpdateTools(ChartParts? parts, ChartPref pref)
    {
        var hasData = parts is { HasData: true };
        KindButton.Visibility = hasData && ChartStyler.CanChangeKind(parts!.Series) ? Visibility.Visible : Visibility.Collapsed;
        KindLabel.Text = KindName(_kind);
        var toggleable = parts == null ? 0 : ChartStyler.Toggleable(parts.Series).Count();
        SeriesButton.Visibility = hasData && toggleable >= 2 ? Visibility.Visible : Visibility.Collapsed;
        SeriesLabel.Text = pref.Hidden.Count > 0 ? $"Paramètres ({toggleable - pref.Hidden.Count}/{toggleable})" : "Paramètres";
        ZoomButton.Visibility = hasData && !IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowTools(bool show, bool animate = true)
    {
        show |= IsExpanded;
        Tools.IsHitTestVisible = show;
        var to = show ? 1.0 : 0.0;
        if (animate)
            Tools.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(show ? 180 : 260))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else
        {
            Tools.BeginAnimation(OpacityProperty, null);
            Tools.Opacity = to;
        }
    }

    private static string KindName(ChartKind kind) => kind switch
    {
        ChartKind.Bars => "Barres",
        ChartKind.Line => "Courbe",
        ChartKind.Points => "Points",
        ChartKind.Area => "Aire",
        _ => "Type",
    };

    private void KindButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = KindButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (kind, label) in new[]
                 {
                     (ChartKind.Auto, "D'origine"), (ChartKind.Bars, "Barres"), (ChartKind.Line, "Courbe"),
                     (ChartKind.Points, "Points"), (ChartKind.Area, "Aire"),
                 })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _kind == kind };
            item.Click += (_, _) =>
            {
                var pref = ChartPrefs.Get(PrefKey);
                ChartPrefs.Set(PrefKey, new ChartPref { Kind = kind, Hidden = pref.Hidden });
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void SeriesButton_Click(object sender, RoutedEventArgs e)
    {
        if (Chart is not { } parts)
            return;
        var menu = new ContextMenu { PlacementTarget = SeriesButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var hidden = ChartPrefs.Get(PrefKey).Hidden;
        foreach (var name in ChartStyler.Toggleable(parts.Series))
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = !hidden.Contains(Normalize(name)), StaysOpenOnClick = true };
            item.Click += (_, _) => Toggle(name);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = "Tout afficher" };
        all.Click += (_, _) => ChartPrefs.Set(PrefKey, new ChartPref { Kind = ChartPrefs.Get(PrefKey).Kind });
        menu.Items.Add(all);
        menu.IsOpen = true;
    }

    private void LegendEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name })
            Toggle(name);
    }

    // Switch a series off or on. The last one shown stays: an empty chart
    // would look like missing data.
    private void Toggle(string name)
    {
        if (Chart is not { } parts)
            return;
        var pref = ChartPrefs.Get(PrefKey);
        var hidden = new List<string>(pref.Hidden);
        if (!hidden.Remove(Normalize(name)))
        {
            var shown = ChartStyler.Toggleable(parts.Series).Count(n => !hidden.Contains(Normalize(n)));
            if (shown <= 1)
                return;
            hidden.Add(Normalize(name));
        }
        ChartPrefs.Set(PrefKey, new ChartPref { Kind = pref.Kind, Hidden = hidden });
    }

    private void ZoomButton_Click(object sender, RoutedEventArgs e) => OpenZoom();

    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !IsExpanded)
        {
            OpenZoom();
            e.Handled = true;
        }
    }

    private void OpenZoom()
    {
        if (Chart is not { HasData: true } parts)
            return;
        new ChartZoomWindow(Title, parts, ShowLegend) { Owner = Window.GetWindow(this) }.Show();
    }

    // ---------- legend ----------

    // Bound OneTime in ChartCard.xaml: a binding that watches a property of
    // an object without change notification goes through a static
    // PropertyDescriptor table, and an unfrozen brush drawn by the legend dot
    // points back at that dot, so every slide that ever showed a legend (its
    // charts, bitmaps and the whole panel) stayed in memory: 8 GB + 3.5 GB of
    // graphics memory after 49 h on the hall screen (15/11/2026).
    public sealed record LegendEntry(string Name, Brush Brush, bool Hidden)
    {
        public double Opacity => Hidden ? 0.4 : 1;
        public TextDecorationCollection? Decorations => Hidden ? TextDecorations.Strikethrough : null;
    }

    // The series' colour: its line for a line, its fill for bars, its dots
    // for points. Frozen (see LegendEntry).
    private static Brush SeriesBrush(ISeries series)
    {
        foreach (var name in new[] { "Stroke", "Fill", "GeometryFill" })
        {
            if (Property(series.GetType(), name)?.GetValue(series) is SolidColorPaint { Color: var c } && c.Alpha > 120)
            {
                var brush = new SolidColorBrush(Color.FromArgb(255, c.Red, c.Green, c.Blue));
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
