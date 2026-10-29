using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Services;

// One chart, ready to bind (Series / XAxes / YAxes / LegendTextPaint) or to
// render to an image (ExportService). HasData = false shows "Pas de donnees"
// instead of an empty chart.
public sealed record ChartParts(ISeries[] Series, Axis[] XAxes, Axis[] YAxes, bool HasData = true)
{
    public SolidColorPaint LegendTextPaint { get; } = new(ChartHelpers.TextColor);

    public static ChartParts Empty() => new(Array.Empty<ISeries>(),
        new[] { ChartHelpers.BuildXAxis(Array.Empty<string>()) }, new[] { ChartHelpers.BuildYAxis() }, false);
}

// Builders of the dashboard charts, from buckets grouped per time step
// (PeriodRanges.Group): the slides and the exports draw the same charts.
public static class DashboardCharts
{
    private static readonly SKColor Blue = ChartHelpers.Palette[0];
    private static readonly SKColor Green = ChartHelpers.Palette[1];
    private static readonly SKColor Orange = ChartHelpers.Palette[2];
    private static readonly SKColor Red = ChartHelpers.Palette[3];
    private static readonly SKColor Grey = new(0x9A, 0xA4, 0xB2);
    private static readonly System.Globalization.CultureInfo French = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    public sealed record Step(string Label, IReadOnlyCollection<ProductionBucket> Lots);

    public static List<Step> Steps(IEnumerable<(DateTime Key, string Label, List<ProductionBucket> Lots)> groups)
        => groups.Select(g => new Step(g.Label, g.Lots)).ToList();

    // Share of each step the line was producing (1 = ran the whole observed
    // time, 0 = stopped or no snapshot at all).
    public static ChartParts OnOff(IReadOnlyList<Step> steps)
    {
        var series = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Rx = 6,
                Ry = 6,
                Values = steps.Select(s =>
                {
                    var observed = s.Lots.Sum(l => l.ObservedMinutes);
                    return observed > 0 ? Math.Round(s.Lots.Sum(l => l.RunMinutes) / observed, 2) : 0;
                }).ToArray(),
                Name = "En marche",
                Fill = new SolidColorPaint(Green),
                Padding = 2,
                MaxBarWidth = 40,
            },
        };
        return new ChartParts(series,
            new[] { ChartHelpers.BuildXAxis(steps.Select(s => s.Label)) },
            new[] { ChartHelpers.BuildYAxis(minLimit: 0, maxLimit: 1, minStep: 0.5, labeler: v => $"{v:P0}") });
    }

    // Average speed (bags / producing minute) as columns, max speed reached
    // (fastest snapshot interval) as a line; steps without production are gaps.
    public static ChartParts Speed(IReadOnlyList<Step> steps)
    {
        var avg = steps.Select(s =>
        {
            var run = s.Lots.Sum(l => l.RunMinutes);
            return run > 0 ? (double?)Math.Round(s.Lots.Sum(l => l.TotalBags) / run, 1) : null;
        }).ToArray();
        var max = steps.Select(s =>
        {
            var m = s.Lots.Select(l => l.MaxSpeedBagsPerMinute).DefaultIfEmpty(0).Max();
            return m > 0 ? (double?)Math.Round(m, 1) : null;
        }).ToArray();
        if (avg.All(v => v == null))
            return ChartParts.Empty();

        var top = avg.Concat(max).Max(v => v ?? 0);
        var series = new ISeries[]
        {
            new ColumnSeries<double?>
            {
                Rx = 6,
                Ry = 6,
                Values = avg,
                Name = "Vitesse moy.",
                Fill = new SolidColorPaint(Blue),
                MaxBarWidth = 36,
                DataLabelsPaint = steps.Count <= 16 ? new SolidColorPaint(ChartHelpers.TextColor) : null,
                DataLabelsPosition = DataLabelsPosition.Top,
                DataLabelsSize = 13,
                DataLabelsFormatter = p => p.Coordinate.PrimaryValue > 0 ? $"{p.Coordinate.PrimaryValue:0}" : "",
            },
            new LineSeries<double?>
            {
                Values = max,
                Name = "Vitesse max",
                Stroke = new SolidColorPaint(Orange, 3),
                GeometryStroke = new SolidColorPaint(Orange, 3),
                GeometrySize = 8,
                Fill = null,
                LineSmoothness = 0,
            },
        };
        return new ChartParts(series,
            new[] { ChartHelpers.BuildXAxis(steps.Select(s => s.Label)) },
            new[] { ChartHelpers.BuildYAxis(minLimit: 0, maxLimit: Math.Ceiling(top * 1.4), minStep: Math.Max(1, Math.Ceiling(top / 2)), labeler: v => $"{v:0}") });
    }

    // Net average weight vs target per step. The target is bags-weighted like
    // the average, so a changeover inside a step compares each bag with its
    // own target. Steps without trustworthy weight data are skipped.
    public static ChartParts Weight(IReadOnlyList<Step> steps)
    {
        var weighed = steps
            .Select(s => (s.Label, Bags: s.Lots.Sum(l => l.WeighedBags), Sum: s.Lots.Sum(l => l.WeighedWeightSum), Target: s.Lots.Sum(l => l.WeighedTargetSum)))
            .Where(s => s.Bags > 0)
            .Select(s => (s.Label, Avg: Math.Round(s.Sum / s.Bags, 1), Target: Math.Round(s.Target / s.Bags, 1)))
            .ToList();
        if (weighed.Count == 0)
            return ChartParts.Empty();

        var series = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = weighed.Select(b => b.Avg).ToArray(),
                Name = "Poids moyen",
                Stroke = new SolidColorPaint(Green, 3),
                Fill = null,
                GeometrySize = 10,
            },
            new LineSeries<double>
            {
                Values = weighed.Select(b => b.Target).ToArray(),
                Name = "Cible",
                Stroke = new SolidColorPaint(Grey, 2),
                Fill = null,
                GeometrySize = 0,
            },
        };
        // Fixed range around the data with 3 labels: auto-scaling packed 5+
        // labels into the short tile.
        var weights = weighed.SelectMany(b => new[] { b.Avg, b.Target }).ToList();
        var pad = Math.Max(5, (weights.Max() - weights.Min()) * 0.2);
        var low = Math.Max(0, Math.Floor(weights.Min() - pad));
        var high = Math.Ceiling(weights.Max() + pad);
        return new ChartParts(series,
            new[] { ChartHelpers.BuildXAxis(weighed.Select(b => b.Label)) },
            new[] { ChartHelpers.BuildYAxis(low, high, minStep: (high - low) / 2, labeler: v => $"{v:0} g") });
    }

    // TRS per step as columns, with the machine's target as a line.
    public static ChartParts Trs(IReadOnlyList<Step> steps, double target)
    {
        var trs = steps.Select(s => KpiCalculator.Summarize(s.Lots, KpiCalculator.AvailabilityPercent(s.Lots)).TrsPercent).ToArray();
        var top = Math.Max(100, Math.Ceiling(trs.Max(v => v ?? 0) * 1.2));
        var series = new ISeries[]
        {
            new ColumnSeries<double?>
            {
                Rx = 6,
                Ry = 6,
                Values = trs,
                Name = "TRS",
                Fill = new SolidColorPaint(Blue),
                DataLabelsPaint = steps.Count <= 16 ? new SolidColorPaint(ChartHelpers.TextColor) : null,
                DataLabelsPosition = DataLabelsPosition.Top,
                DataLabelsSize = 12,
                DataLabelsFormatter = p => p.Coordinate.PrimaryValue > 0 ? $"{p.Coordinate.PrimaryValue:0}" : "",
                MaxBarWidth = 36,
            },
            new LineSeries<double>
            {
                Values = steps.Select(_ => target).ToArray(),
                Name = $"Cible {target:0} %",
                Stroke = new SolidColorPaint(Green, 2),
                Fill = null,
                GeometrySize = 0,
            },
        };
        return new ChartParts(series,
            new[] { ChartHelpers.BuildXAxis(steps.Select(s => s.Label)) },
            new[] { ChartHelpers.BuildYAxis(minLimit: 0, maxLimit: top, minStep: 50, labeler: v => $"{v:0}%") });
    }

    // Net weight produced per step, one stacked colour per target weight.
    public static ChartParts ProductionByWeight(IReadOnlyList<Step> steps)
    {
        var weights = steps.SelectMany(s => s.Lots).Where(l => l.TargetNetWeight > 0)
            .GroupBy(l => Math.Round(l.TargetNetWeight))
            .OrderByDescending(g => g.Sum(l => l.TotalBags))
            .Select(g => g.Key)
            .ToList();
        if (weights.Count == 0 || steps.All(s => s.Lots.Sum(l => l.TotalBags) == 0))
            return ChartParts.Empty();

        var useTonnes = steps.Max(s => KpiCalculator.ByWeight(s.Lots).Sum(w => w.ProductionKg)) >= 10_000;
        var unit = useTonnes ? 1000.0 : 1.0;
        var series = weights.Select((w, i) => (ISeries)new StackedColumnSeries<double>
        {
            Values = steps.Select(s =>
                Math.Round(KpiCalculator.ByWeight(s.Lots.Where(l => Math.Round(l.TargetNetWeight) == w).ToList())
                    .Sum(k => k.ProductionKg) / unit, 1)).ToArray(),
            Name = $"{w:0} g",
            Fill = new SolidColorPaint(ChartHelpers.Palette[i % ChartHelpers.Palette.Length]),
            MaxBarWidth = 36,
        }).ToArray();
        return new ChartParts(series,
            new[] { ChartHelpers.BuildXAxis(steps.Select(s => s.Label)) },
            new[] { ChartHelpers.BuildYAxis(minLimit: 0, labeler: v => useTonnes ? $"{v:0.#} t" : ChartHelpers.Compact(v) + " kg") });
    }

    // Weight deviation (% of the target) per step, 0 % reference line.
    public static ChartParts Deviation(IReadOnlyList<Step> steps)
    {
        var values = steps.Select(s => KpiCalculator.Summarize(s.Lots, 0).WeightDeviationPercent).ToArray();
        if (values.All(v => v == null))
            return ChartParts.Empty();
        var series = new ISeries[]
        {
            new LineSeries<double?>
            {
                Values = values,
                Name = "Ecart (%)",
                Stroke = new SolidColorPaint(Red, 3),
                GeometryStroke = new SolidColorPaint(Red, 2),
                GeometrySize = 8,
                Fill = null,
                LineSmoothness = 0,
            },
            new LineSeries<double>
            {
                Values = steps.Select(_ => 0.0).ToArray(),
                Name = "Cible",
                Stroke = new SolidColorPaint(Grey, 2),
                Fill = null,
                GeometrySize = 0,
            },
        };
        return new ChartParts(series,
            new[] { ChartHelpers.BuildXAxis(steps.Select(s => s.Label)) },
            new[] { ChartHelpers.BuildYAxis(labeler: v => $"{v:+0.0;-0.0;0}%") });
    }

    // One line per named series (synthesis: one per production line), gaps for unknown values.
    public static ChartParts Lines(IReadOnlyList<string> labels, IReadOnlyList<(string Name, double?[] Values)> lines,
        Func<double, string> labeler, double? reference = null)
    {
        if (lines.All(l => l.Values.All(v => v == null)))
            return ChartParts.Empty();
        var series = lines.Select((l, i) => (ISeries)new LineSeries<double?>
        {
            Values = l.Values,
            Name = l.Name,
            Stroke = new SolidColorPaint(ChartHelpers.Palette[i % ChartHelpers.Palette.Length], 3),
            GeometryStroke = new SolidColorPaint(ChartHelpers.Palette[i % ChartHelpers.Palette.Length], 2),
            GeometrySize = 8,
            Fill = null,
            LineSmoothness = 0,
        }).ToList();
        if (reference is { } r)
        {
            series.Add(new LineSeries<double>
            {
                Values = labels.Select(_ => r).ToArray(),
                Name = "Cible",
                Stroke = new SolidColorPaint(Grey, 2),
                Fill = null,
                GeometrySize = 0,
            });
        }
        return new ChartParts(series.ToArray(),
            new[] { ChartHelpers.BuildXAxis(labels) },
            new[] { ChartHelpers.BuildYAxis(labeler: labeler) });
    }

    // Single-series bar chart with the value printed on each bar (teams, machines).
    public static ChartParts Bars(IReadOnlyList<string> labels, IReadOnlyList<double> values, string name, Func<double, string> format)
    {
        var max = values.DefaultIfEmpty(0).Max();
        var axis = ChartHelpers.BuildYAxis(minLimit: 0, maxLimit: max > 0 ? max * 1.35 : null);
        // Values are printed on the bars: the value axis only crowded the tiles.
        axis.IsVisible = false;
        return new ChartParts(new ISeries[]
            {
                new ColumnSeries<double>
                {
                    Rx = 6,
                    Ry = 6,
                    Values = values.ToArray(),
                    Name = name,
                    Fill = new SolidColorPaint(Blue),
                    DataLabelsPaint = new SolidColorPaint(ChartHelpers.TextColor),
                    DataLabelsPosition = DataLabelsPosition.Top,
                    DataLabelsSize = ChartHelpers.DataLabelTextSize,
                    DataLabelsFormatter = p => format(p.Coordinate.PrimaryValue),
                    MaxBarWidth = 60,
                },
            },
            new[] { ChartHelpers.BuildXAxis(labels) },
            new[] { axis });
    }

    // One TRS bar per label (no bar when unknown), with the target as a
    // green line when there's a single one.
    public static ChartParts TrsBars(IReadOnlyList<string> labels, IReadOnlyList<double?> values, double? target)
    {
        var top = Math.Max(100, Math.Ceiling(Math.Max(values.Max(v => v ?? 0), target ?? 0) * 1.25));
        var axis = ChartHelpers.BuildYAxis(minLimit: 0, maxLimit: top);
        axis.IsVisible = false;
        var series = new List<ISeries>
        {
            new ColumnSeries<double?>
            {
                Rx = 6,
                Ry = 6,
                Values = values.ToArray(),
                Name = "TRS",
                Fill = new SolidColorPaint(Blue),
                DataLabelsPaint = new SolidColorPaint(ChartHelpers.TextColor),
                DataLabelsPosition = DataLabelsPosition.Top,
                DataLabelsSize = ChartHelpers.DataLabelTextSize,
                DataLabelsFormatter = p => p.Coordinate.PrimaryValue.ToString("0.0", French) + " %",
                MaxBarWidth = 60,
            },
        };
        if (target is { } t)
        {
            series.Add(new LineSeries<double>
            {
                Values = labels.Select(_ => t).ToArray(),
                Name = $"Cible {t:0} %",
                Stroke = new SolidColorPaint(Green, 2),
                Fill = null,
                GeometrySize = 0,
                DataLabelsPaint = null,
            });
        }
        return new ChartParts(series.ToArray(), new[] { ChartHelpers.BuildXAxis(labels) }, new[] { axis });
    }
}
