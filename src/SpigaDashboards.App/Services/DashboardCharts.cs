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

    // Every value is printed on the charts (the TV has no mouse for tooltips),
    // smaller as the steps get more numerous (a month by day = 31 bars).
    private static double LabelSize(int steps) => steps <= 16 ? 13 : steps <= 24 ? 11 : 9;

    // Line charts: half a step of margin on each side, so the values of the
    // first and last points aren't cut off.
    private static Axis LineXAxis(IReadOnlyCollection<string> labels)
    {
        var axis = ChartHelpers.BuildXAxis(labels);
        axis.MinLimit = -0.5;
        axis.MaxLimit = labels.Count - 0.5;
        return axis;
    }

    // Value axis around the points with room above them for their values.
    private static Axis PaddedYAxis(IEnumerable<double?> values, Func<double, string> labeler)
    {
        var known = values.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).ToList();
        var (low, high) = (known.Min(), known.Max());
        var range = Math.Max(high - low, Math.Max(Math.Abs(high), 1) * 0.02);
        return ChartHelpers.BuildYAxis(low - 0.2 * range, high + 0.35 * range, labeler: labeler);
    }

    // Share of each step the line was producing over the time it was required
    // to run (observed minus the planned breaks it stopped in): 1 = ran the
    // whole hour apart from its break, 0 = stopped or no snapshot at all.
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
                    var required = s.Lots.Sum(l => l.RequiredMinutes);
                    return required > 0 ? Math.Round(Math.Min(1, s.Lots.Sum(l => l.RunMinutes) / required), 2) : 0;
                }).ToArray(),
                Name = "En marche",
                Fill = new SolidColorPaint(Green),
                Padding = 2,
                MaxBarWidth = 40,
                DataLabelsPaint = new SolidColorPaint(ChartHelpers.TextColor),
                DataLabelsPosition = DataLabelsPosition.Top,
                DataLabelsSize = LabelSize(steps.Count),
                DataLabelsFormatter = p => p.Coordinate.PrimaryValue > 0 ? $"{p.Coordinate.PrimaryValue * 100:0} %" : "",
            },
        };
        // Headroom above 100 % for the labels of full bars.
        return new ChartParts(series,
            new[] { ChartHelpers.BuildXAxis(steps.Select(s => s.Label)) },
            new[] { ChartHelpers.BuildYAxis(minLimit: 0, maxLimit: 1.25, minStep: 0.5, labeler: v => v <= 1 ? $"{v:P0}" : "") });
    }

    // Average speed (bags / minute of steady running, KpiCalculator.AverageSpeed)
    // as columns, max speed reached (fastest snapshot interval) as a line; steps
    // without production are gaps. Average printed inside its bar, max above its
    // point, so they never overlap.
    public static ChartParts Speed(IReadOnlyList<Step> steps)
    {
        var avg = steps.Select(s => KpiCalculator.AverageSpeed(s.Lots) is { } v ? (double?)Math.Round(v, 1) : null).ToArray();
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
                DataLabelsPaint = new SolidColorPaint(SKColors.White),
                DataLabelsPosition = DataLabelsPosition.Middle,
                DataLabelsSize = LabelSize(steps.Count),
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
                DataLabelsPaint = new SolidColorPaint(Orange),
                DataLabelsPosition = DataLabelsPosition.Top,
                DataLabelsSize = LabelSize(steps.Count),
                DataLabelsFormatter = p => $"{p.Coordinate.PrimaryValue:0}",
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
                DataLabelsPaint = new SolidColorPaint(ChartHelpers.TextColor),
                DataLabelsPosition = DataLabelsPosition.Top,
                DataLabelsSize = LabelSize(weighed.Count),
                DataLabelsFormatter = p => p.Coordinate.PrimaryValue.ToString("0.0", French),
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
            new[] { LineXAxis(weighed.Select(b => b.Label).ToList()) },
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
                DataLabelsPaint = new SolidColorPaint(ChartHelpers.TextColor),
                DataLabelsPosition = DataLabelsPosition.Top,
                DataLabelsSize = LabelSize(steps.Count),
                DataLabelsFormatter = p => p.Coordinate.PrimaryValue > 0 ? $"{p.Coordinate.PrimaryValue:0}" : "",
                MaxBarWidth = 36,
            },
            new LineSeries<double>
            {
                Values = steps.Select(_ => target).ToArray(),
                Name = $"Cible {target:0} %",
                Stroke = new SolidColorPaint(Green, 2),
                Fill = null,
                GeometrySize = 0,
            },
        };
        return new ChartParts(series,
            new[] { ChartHelpers.BuildXAxis(steps.Select(s => s.Label)) },
            new[] { ChartHelpers.BuildYAxis(minLimit: 0, maxLimit: top, minStep: 50, labeler: v => $"{v:0}%") });
    }

    // Net weight produced per step, one stacked colour per target weight, the
    // step's total printed above the stack (an invisible line carries it).
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
        }).ToList();
        var totals = steps.Select(s => Math.Round(KpiCalculator.ByWeight(s.Lots).Sum(k => k.ProductionKg) / unit, 1)).ToArray();
        series.Add(new LineSeries<double>
        {
            Values = totals,
            Name = "Total",
            IsVisibleAtLegend = false,
            Stroke = null,
            Fill = null,
            GeometrySize = 0,
            GeometryStroke = null,
            GeometryFill = null,
            DataLabelsPaint = new SolidColorPaint(ChartHelpers.TextColor),
            DataLabelsPosition = DataLabelsPosition.Top,
            DataLabelsSize = LabelSize(steps.Count),
            DataLabelsFormatter = p => p.Coordinate.PrimaryValue <= 0 ? ""
                : useTonnes ? p.Coordinate.PrimaryValue.ToString("0.#", French) : ChartHelpers.Compact(p.Coordinate.PrimaryValue),
        });
        // Headroom above the tallest stack for its total.
        return new ChartParts(series.ToArray(),
            new[] { ChartHelpers.BuildXAxis(steps.Select(s => s.Label)) },
            new[] { ChartHelpers.BuildYAxis(minLimit: 0, maxLimit: totals.Max() > 0 ? totals.Max() * 1.35 : null,
                labeler: v => useTonnes ? $"{v:0.#} t" : ChartHelpers.Compact(v) + " kg") });
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
                Name = "Écart (%)",
                Stroke = new SolidColorPaint(Red, 3),
                GeometryStroke = new SolidColorPaint(Red, 2),
                GeometrySize = 8,
                Fill = null,
                LineSmoothness = 0,
                DataLabelsPaint = new SolidColorPaint(ChartHelpers.TextColor),
                DataLabelsPosition = DataLabelsPosition.Top,
                DataLabelsSize = Math.Min(11, LabelSize(steps.Count)),
                // Deviations are hundredths of a percent: two decimals, no
                // "%" (the axis says it) to keep neighbouring values apart.
                DataLabelsFormatter = p => p.Coordinate.PrimaryValue.ToString("+0.00;-0.00;0", French),
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
            new[] { LineXAxis(steps.Select(s => s.Label).ToList()) },
            new[] { PaddedYAxis(values.Append(0), v => $"{v:+0.00;-0.00;0}%") });
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
            // In the line's own colour, to tell the lines' values apart.
            DataLabelsPaint = new SolidColorPaint(ChartHelpers.Palette[i % ChartHelpers.Palette.Length]),
            DataLabelsPosition = DataLabelsPosition.Top,
            DataLabelsSize = Math.Min(11, LabelSize(labels.Count)),
            DataLabelsFormatter = p => labeler(p.Coordinate.PrimaryValue),
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
            new[] { LineXAxis(labels) },
            new[] { PaddedYAxis(lines.SelectMany(l => l.Values).Append(reference), labeler) });
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
                DataLabelsFormatter = p => p.Coordinate.PrimaryValue.ToString("0.0", French) + " %",
                MaxBarWidth = 60,
            },
        };
        if (target is { } t)
        {
            series.Add(new LineSeries<double>
            {
                Values = labels.Select(_ => t).ToArray(),
                Name = $"Cible {t:0} %",
                Stroke = new SolidColorPaint(Green, 2),
                Fill = null,
                GeometrySize = 0,
                DataLabelsPaint = null,
            });
        }
        return new ChartParts(series.ToArray(), new[] { ChartHelpers.BuildXAxis(labels) }, new[] { axis });
    }
}
