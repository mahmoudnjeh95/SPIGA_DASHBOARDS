using System.IO;
using System.Reflection;
using System.Text.Json;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace SpigaDashboards.App.Services;

// How the user wants a chart drawn. Auto = as designed in DashboardCharts.
public enum ChartKind { Auto, Bars, Line, Points, Area }

// What the user changed on a chart (ChartCard): its kind and the series they
// switched off. Kept per chart (its title) on this PC, so the choice
// survives the slide rotation and a restart.
public sealed class ChartPref
{
    public ChartKind Kind { get; set; }
    public List<string> Hidden { get; set; } = new();
}

public static class ChartPrefs
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpigaDashboards", "chart-views.json");

    private static Dictionary<string, ChartPref>? _prefs;

    // A chart's preference changed: every card showing that chart redraws
    // (the enlarged view and the tile behind it stay the same).
    public static event Action<string>? Changed;

    public static ChartPref Get(string key)
    {
        Load();
        return _prefs!.TryGetValue(key, out var pref) ? pref : new ChartPref();
    }

    public static void Set(string key, ChartPref pref)
    {
        Load();
        if (pref.Kind == ChartKind.Auto && pref.Hidden.Count == 0)
            _prefs!.Remove(key);
        else
            _prefs![key] = pref;
        Save();
        Changed?.Invoke(key);
    }

    private static void Load()
    {
        if (_prefs != null)
            return;
        try
        {
            _prefs = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, ChartPref>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch (Exception)
        {
            _prefs = new();   // unreadable file: start again from the designed charts
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_prefs));
        }
        catch (Exception)
        {
            // A preference that cannot be saved still applies until the app closes.
        }
    }
}

// Redraws the series of a ChartParts as another kind, and copies charts for
// the enlarged view (a LiveCharts series or axis belongs to one chart).
// Only the measured series change kind: target / reference lines (no
// points) and the invisible label carriers stay as they are.
public static class ChartStyler
{
    public static bool IsData(ISeries s)
        => Generic(s) is { } g
           && (g == typeof(ColumnSeries<>) || g == typeof(StackedColumnSeries<>)
               || (g == typeof(LineSeries<>) && Get(s, "GeometrySize") is double size && size > 0 && Get(s, "Stroke") != null));

    public static bool CanChangeKind(IEnumerable<ISeries> series) => series.Any(IsData);

    // The series the user can switch on and off: those with a legend entry.
    public static IEnumerable<string> Toggleable(IEnumerable<ISeries> series)
        => series.Where(s => s.IsVisibleAtLegend && !string.IsNullOrEmpty(s.Name)).Select(s => s.Name!).Distinct();

    // copy = true: new series even when the kind does not change (enlarged view).
    public static ISeries[] Render(IReadOnlyList<ISeries> series, ChartKind kind, Func<double, string>? labeler, bool copy)
        => series.Select(s =>
            {
                if (kind == ChartKind.Auto || !IsData(s) || (kind == ChartKind.Bars && IsBar(s)))
                    return copy ? Clone(s) : s;
                return Convert(s, kind, labeler) ?? (copy ? Clone(s) : s);
            }).ToArray();

    private static bool IsBar(ISeries s) => Generic(s) is { } g && (g == typeof(ColumnSeries<>) || g == typeof(StackedColumnSeries<>));

    private static Type? Generic(ISeries s)
        => s.GetType() is { IsGenericType: true } t ? t.GetGenericTypeDefinition() : null;

    private static ISeries? Convert(ISeries s, ChartKind kind, Func<double, string>? labeler)
    {
        try
        {
            var model = s.GetType().GetGenericArguments()[0];
            var color = ColorOf(s);
            var target = kind == ChartKind.Bars ? typeof(ColumnSeries<>) : typeof(LineSeries<>);
            var n = (ISeries)Activator.CreateInstance(target.MakeGenericType(model))!;
            Set(n, "Values", Get(s, "Values"));
            n.Name = s.Name;
            n.IsVisibleAtLegend = s.IsVisibleAtLegend;
            Set(n, "ScalesXAt", Get(s, "ScalesXAt"));   // same axes as designed (own scale for counts, %)
            Set(n, "ScalesYAt", Get(s, "ScalesYAt"));
            var labelPaint = Get(s, "DataLabelsPosition") is DataLabelsPosition.Middle
                ? new SolidColorPaint(ChartHelpers.TextColor)
                : Get(s, "DataLabelsPaint") is SolidColorPaint p ? new SolidColorPaint(p.Color) : null;
            Set(n, "DataLabelsPaint", labelPaint);
            Set(n, "DataLabelsSize", Get(s, "DataLabelsSize"));
            Set(n, "DataLabelsPosition", DataLabelsPosition.Top);
            // Same visual type (line to line): the designed labels carry over;
            // otherwise the value axis says how to write a value.
            if (n.GetType().GetGenericTypeDefinition() == Generic(s) && Get(s, "DataLabelsFormatter") is { } same)
                Set(n, "DataLabelsFormatter", same);
            else if (labelPaint != null)
                SetFormatter(n, v => Label(v, labeler));

            switch (kind)
            {
                case ChartKind.Bars:
                    Set(n, "Fill", new SolidColorPaint(color));
                    Set(n, "Rx", 6.0);
                    Set(n, "Ry", 6.0);
                    Set(n, "MaxBarWidth", 40.0);
                    break;
                case ChartKind.Line:
                    Set(n, "Stroke", new SolidColorPaint(color, 3));
                    Set(n, "Fill", null);
                    Set(n, "GeometryStroke", new SolidColorPaint(color, 3));
                    Set(n, "GeometrySize", 9.0);
                    Set(n, "LineSmoothness", 0.35);
                    break;
                case ChartKind.Points:
                    Set(n, "Stroke", null);
                    Set(n, "Fill", null);
                    Set(n, "GeometryStroke", null);
                    Set(n, "GeometryFill", new SolidColorPaint(color));
                    Set(n, "GeometrySize", 14.0);
                    break;
                case ChartKind.Area:
                    Set(n, "Stroke", new SolidColorPaint(color, 3));
                    Set(n, "Fill", new SolidColorPaint(color.WithAlpha(70)));
                    Set(n, "GeometryStroke", new SolidColorPaint(color, 2));
                    Set(n, "GeometrySize", 6.0);
                    Set(n, "LineSmoothness", 0.5);
                    break;
            }
            return n;
        }
        catch (Exception)
        {
            return null;   // a series this code does not know: keep it as designed
        }
    }

    private static string Label(double v, Func<double, string>? labeler)
    {
        var text = labeler?.Invoke(v);
        return !string.IsNullOrEmpty(text) ? text
            : v.ToString(Math.Abs(v) < 10 ? "0.##" : "0.#", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));
    }

    // DataLabelsFormatter is a Func<ChartPoint<model, visual, label>, string>
    // whose types depend on the series: built for the series at hand.
    private static void SetFormatter(ISeries series, Func<double, string> format)
    {
        var property = Property(series.GetType(), "DataLabelsFormatter");
        if (property == null)
            return;
        var point = property.PropertyType.GetGenericArguments()[0];
        var make = typeof(ChartStyler).GetMethod(nameof(MakeFormatter), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(point);
        property.SetValue(series, make.Invoke(null, new object[] { format }));
    }

    private static Func<TPoint, string> MakeFormatter<TPoint>(Func<double, string> format) where TPoint : ChartPoint
        => p => format(p.Coordinate.PrimaryValue);

    private static SKColor ColorOf(ISeries s)
    {
        foreach (var name in new[] { "Fill", "Stroke", "GeometryFill", "GeometryStroke" })
            if (Get(s, name) is SolidColorPaint { Color: var c } && c.Alpha > 0)
                return c;
        return ChartHelpers.Palette[0];
    }

    // ---------- copies for the enlarged view ----------

    public static ISeries Clone(ISeries s) => (ISeries)CloneObject(s);

    public static Axis[] Clone(IEnumerable<Axis> axes) => axes.Select(a => (Axis)CloneObject(a)).ToArray();

    private static object CloneObject(object source)
    {
        var copy = Activator.CreateInstance(source.GetType())!;
        // Most derived declaration of each name only: a base one set after it
        // would undo it (bar corners, sizes).
        var properties = source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.SetMethod?.IsPublic == true)
            .GroupBy(p => p.Name)
            .Select(g => g.OrderByDescending(p => Depth(p.DeclaringType)).First());
        foreach (var p in properties)
        {
            try
            {
                var value = p.GetValue(source);
                p.SetValue(copy, value is SolidColorPaint paint ? CopyPaint(paint) : value);
            }
            catch (Exception)
            {
                // A property tied to the chart that drew the original: left at its default.
            }
        }
        return copy;
    }

    private static SolidColorPaint CopyPaint(SolidColorPaint p)
        => new(p.Color, p.StrokeThickness) { PathEffect = p.PathEffect, IsAntialias = p.IsAntialias };

    // ---------- reflection on the generic series ----------

    private static object? Get(object o, string name) => Property(o.GetType(), name)?.GetValue(o);

    private static void Set(object o, string name, object? value)
    {
        if (Property(o.GetType(), name) is { CanWrite: true } p)
            p.SetValue(o, value);
    }

    // The most derived property of that name (generic series redeclare some).
    private static PropertyInfo? Property(Type type, string name)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name == name && p.GetIndexParameters().Length == 0)
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
