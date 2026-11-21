using LiveChartsCore;
using LiveChartsCore.Defaults;
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

    // Round axis step at or above `raw`: 1, 2, 2.5 or 5 times a power of ten.
    private static double NiceStep(double raw)
    {
        if (raw <= 0)
            return 0;
        var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var mantissa = raw / power;
        return power * (mantissa <= 1 ? 1 : mantissa <= 2 ? 2 : mantissa <= 2.5 ? 2.5 : mantissa <= 5 ? 5 : 10);
    }

    // Points of a line chart that print their value: all of them up to 16
    // steps; beyond (a month by day), the last one and every n-th before it,
    // so the neighbouring values don't run into each other.
    private static HashSet<int> LabelledPoints(IReadOnlyList<double?> values)
    {
        var stride = values.Count <= 16 ? 1 : (int)Math.Ceiling(values.Count / 10.0);
        var labelled = new HashSet<int>();
        int? last = null;
        for (var i = values.Count - 1; i >= 0; i--)
        {
            if (values[i] != null && (last is not { } l || l - i >= stride))
            {
                labelled.Add(i);
                last = i;
            }
        }
        return labelled;
    }

    // Value axis around the points with room above them for their values.
    private static Axis PaddedYAxis(IEnumerable<double?> values, Func<double, string> labeler)
    {
        var known = values.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).ToList();
        var (low, high) = (known.Min(), known.Max());
        var range = Math.Max(high - low, Math.Max(Math.Abs(high), 1) * 0.02);
        // Room for a value above the highest point even in the short period
        // tiles, and 2 or 3 axis labels (auto steps packed 5 into them).
        var (bottom, top) = (low - 0.2 * range, high + 0.9 * range);
        return ChartHelpers.BuildYAxis(bottom, top, minStep: NiceStep((top - bottom) / 2.5), labeler: labeler);
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
        // Headroom above 100 % for the labels of full bars (1.25 cut them
        // off in the poste view's short tiles).
        return new ChartParts(series,
            new[] { ChartHelpers.BuildXAxis(steps.Select(s => s.Label)) },
            new[] { ChartHelpers.BuildYAxis(minLimit: 0, maxLimit: 1.5, minStep: 0.5, labeler: v => v <= 1 ? $"{v:P0}" : "") });
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
            // Room above the max line for its values (they were cut off in the
            // short period tiles), 2 or 3 round axis labels (0, 50, 100).
            new[] { ChartHelpers.BuildYAxis(minLimit: 0, maxLimit: Math.Ceiling(top * 1.9),
                minStep: Math.Max(1, Math.Ceiling(top / 1.5 / 10) * 10), labeler: v => $"{v:0}") });
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

    // Flat segments of one line series over an hour axis: a null point (gap)
    // between two segments more than 2 min apart, joined otherwise.
    private sealed class Track
    {
        private const double GapHours = 2.0 / 60;
        private double? _last;
        public List<ObservablePoint> Points { get; } = new();

        public void Add(double from, double to, double value)
        {
            if (_last is { } last && from - last > GapHours)
                Points.Add(new ObservablePoint(last, null));
            Points.Add(new ObservablePoint(from, value));
            Points.Add(new ObservablePoint(to, value));
            _last = to;
        }
    }

    // Lines don't all close lots of the same size (PL03: 50 bags, one a
    // minute; the others: 1000). Consecutive lots of one production are
    // merged until they reach 1000 bags, so every line shows comparable
    // steps; 1000-bag lots stay as they are. A production change closes the
    // group early. Group average = bag-weighted average of its lots.
    private const long LotGroupBags = 1000;

    private static List<LotSpan> GroupLots(IReadOnlyList<LotSpan> lots, bool live)
    {
        var groups = new List<LotSpan>();
        LotSpan? open = null;
        double weightSum = 0;
        foreach (var lot in lots.OrderBy(l => l.EndTime))
        {
            if (open != null && lot.ProductionId != open.ProductionId)
            {
                groups.Add(open);
                open = null;
            }
            if (open == null)
            {
                open = new LotSpan { ProductionId = lot.ProductionId, StartTime = lot.StartTime };
                weightSum = 0;
            }
            if (lot.StartTime < open.StartTime)
                open.StartTime = lot.StartTime;
            open.EndTime = lot.EndTime;
            open.Bags += lot.Bags;
            weightSum += lot.Average * lot.Bags;
            open.Average = weightSum / open.Bags;
            open.Target = lot.Target;
            if (open.Bags >= LotGroupBags)
            {
                groups.Add(open);
                open = null;
            }
        }
        if (open != null && !live)
            groups.Add(open);
        return groups;
    }

    // Net average weight vs target over time: a smooth curve through one
    // point per Stiavelli lot, at the middle of its span from its first to
    // its last bag (instead of the whole lot counted in the hour it ends in).
    // X = hours since start, labelled with the clock hour like the hourly
    // charts. Lots are clipped to [start, end); the lot in progress isn't in
    // lotto yet, so the line stops at the last closed lot. Values printed at
    // each lot's middle while they stay readable. Stops aren't marked: they
    // have their own panel and pop-up (a lot interrupted by a stop still has
    // a real average, of the bags weighed before and after it).
    // live = poste in progress: the last group still below 1000 bags is
    // hidden, like a lot in progress on the 1000-bag lines.
    public static ChartParts WeightLots(IReadOnlyList<LotSpan> lots, DateTime start, DateTime end, bool live)
    {
        var hours = Math.Max(1, (end - start).TotalHours);
        var spans = GroupLots(lots, live)
            .Select(l => (Lot: l,
                From: Math.Max(0, (l.StartTime - start).TotalHours),
                To: Math.Min(hours, (l.EndTime - start).TotalHours),
                Avg: Math.Round(l.Average, 1)))
            .Where(s => s.To > s.From)
            .OrderBy(s => s.From)
            .ToList();
        if (spans.Count == 0)
            return ChartParts.Empty();

        // Smooth curve through the lot middles; two lots more than 2 min
        // apart aren't joined (null point = gap).
        const double gapHours = 2.0 / 60;
        var curve = new List<ObservablePoint>();
        for (var i = 0; i < spans.Count; i++)
        {
            if (i > 0 && spans[i].From - spans[i - 1].To > gapHours)
                curve.Add(new ObservablePoint(curve[^1].X, null));
            curve.Add(new ObservablePoint((spans[i].From + spans[i].To) / 2, spans[i].Avg));
        }

        // The target stays flat: it only moves on a production change.
        var target = new Track();
        foreach (var s in spans)
            target.Add(s.From, s.To, s.Lot.Target);

        // Readable labels on the TV: a value only when its lot's middle is far
        // enough from the last labelled one (about a label's width), none at
        // all for lines with tiny lots (PL03: 50 bags, one a minute).
        var showLabels = spans.Count <= 3 * hours;
        var minLabelGap = 0.12 * hours;
        var labelled = new List<ObservablePoint>();
        var unlabelled = new List<ObservablePoint>();
        double? lastLabel = null;
        foreach (var point in curve.Where(p => p.Y != null))
        {
            var dot = new ObservablePoint(point.X, point.Y);
            if (showLabels && (lastLabel is not { } l || dot.X - l >= minLabelGap))
            {
                labelled.Add(dot);
                lastLabel = dot.X;
            }
            else
                unlabelled.Add(dot);
        }

        var series = new ISeries[]
        {
            new LineSeries<ObservablePoint>
            {
                Values = curve,
                Name = "Poids moyen (lot)",
                Stroke = new SolidColorPaint(Green, 3),
                Fill = null,
                GeometrySize = 0,
                LineSmoothness = 0.6,
            },
            new LineSeries<ObservablePoint>
            {
                Values = target.Points,
                Name = "Cible",
                Stroke = new SolidColorPaint(Grey, 2),
                Fill = null,
                GeometrySize = 0,
                LineSmoothness = 0,
            },
            // One dot per lot at its middle, the spaced ones carrying the value.
            new ScatterSeries<ObservablePoint>
            {
                Values = labelled,
                Name = "Lot",
                Fill = new SolidColorPaint(Green),
                Stroke = null,
                GeometrySize = 9,
                IsVisibleAtLegend = false,
                DataLabelsPaint = new SolidColorPaint(ChartHelpers.TextColor),
                DataLabelsPosition = DataLabelsPosition.Top,
                DataLabelsSize = 13,
                DataLabelsFormatter = p => p.Coordinate.PrimaryValue.ToString("0.0", French),
            },
            new ScatterSeries<ObservablePoint>
            {
                Values = unlabelled,
                Name = "Lot",
                Fill = new SolidColorPaint(Green),
                Stroke = null,
                GeometrySize = showLabels ? 9 : 5,
                IsVisibleAtLegend = false,
            },
        };

        // Room above the highest lot for its value.
        var weights = spans.SelectMany(s => new[] { s.Avg, s.Lot.Target }).ToList();
        var pad = Math.Max(5, (weights.Max() - weights.Min()) * 0.2);
        var low = Math.Max(0, Math.Floor(weights.Min() - pad));
        var high = Math.Ceiling(weights.Max() + 2 * pad);
        var xAxis = new Axis
        {
            LabelsPaint = new SolidColorPaint(ChartHelpers.TextColor),
            TextSize = ChartHelpers.AxisTextSize,
            MinLimit = 0,
            MaxLimit = hours,
            MinStep = 1,
            ForceStepToMin = true,
            Labeler = v => start.AddHours(Math.Round(v)).ToString("HH"),
        };
        return new ChartParts(series,
            new[] { xAxis },
            new[] { ChartHelpers.BuildYAxis(low, high, minStep: (high - low) / 2, labeler: v => $"{v:0} g") });
    }

    // One bar per machine / team: every bar keeps its name (machine names
    // are longer than the line codes and LiveCharts drops every other label
    // once they would touch), smaller from 7 bars on (Conditionnement 2: 8).
    private static Axis NameAxis(IReadOnlyCollection<string> labels)
    {
        var axis = ChartHelpers.BuildXAxis(labels);
        axis.MinStep = 1;
        axis.ForceStepToMin = true;
        if (labels.Count > 6)
            axis.TextSize = 13;
        return axis;
    }

    // TRS per step as columns, with the machine's target as a line.
    public static ChartParts Trs(IReadOnlyList<Step> steps, double target)
    {
        var trs = steps.Select(s => KpiCalculator.Summarize(s.Lots, KpiCalculator.AvailabilityPercent(s.Lots)).TrsPercent).ToArray();
        // Room above 100 % for the values of the tallest bars (they were cut
        // off in the short tiles); the axis itself stops at 100 %.
        var top = Math.Max(115, Math.Ceiling(trs.Max(v => v ?? 0) * 1.25));
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
            new[] { ChartHelpers.BuildYAxis(minLimit: 0, maxLimit: top, minStep: 50, labeler: v => v <= 100 ? $"{v:0}%" : "") });
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
        // Headroom above the tallest stack for its total; 3 axis labels at
        // most (auto steps packed "0 t" to "50 t" into the short tile).
        var top = totals.Max() > 0 ? totals.Max() * 1.6 : (double?)null;
        return new ChartParts(series.ToArray(),
            new[] { ChartHelpers.BuildXAxis(steps.Select(s => s.Label)) },
            new[] { ChartHelpers.BuildYAxis(minLimit: 0, maxLimit: top, minStep: top is { } t ? NiceStep(t / 2) : 0,
                labeler: v => useTonnes ? $"{v:0.#} t" : ChartHelpers.Compact(v) + " kg") });
    }

    // Weight deviation (% of the target) per step, 0 % reference line.
    public static ChartParts Deviation(IReadOnlyList<Step> steps)
    {
        var values = steps.Select(s => KpiCalculator.Summarize(s.Lots, 0).WeightDeviationPercent).ToArray();
        if (values.All(v => v == null))
            return ChartParts.Empty();
        var labelled = LabelledPoints(values);
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
                DataLabelsSize = Math.Min(11, LabelSize(labelled.Count)),
                // Deviations are hundredths of a percent: two decimals, no
                // "%" (the axis says it) to keep neighbouring values apart.
                DataLabelsFormatter = p => labelled.Contains(p.Index) ? p.Coordinate.PrimaryValue.ToString("+0.00;-0.00;0", French) : "",
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
        var labelled = lines.Select(l => LabelledPoints(l.Values)).ToList();
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
            DataLabelsSize = Math.Min(11, LabelSize(labelled[i].Count)),
            DataLabelsFormatter = p => labelled[i].Contains(p.Index) ? labeler(p.Coordinate.PrimaryValue) : "",
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
            new[] { NameAxis(labels) },
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
        return new ChartParts(series.ToArray(), new[] { NameAxis(labels) }, new[] { axis });
    }
}
