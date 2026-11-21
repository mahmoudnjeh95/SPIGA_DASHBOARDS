using System.Globalization;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace Spiga.UI.Services;

// One chart of the web deck, ready for SpigaChart. HasData = false shows
// "Pas de données" instead of an empty chart. Times: a time axis (one time
// per value) instead of the Labels.
public sealed record DeckChart(IReadOnlyList<string> Labels, IReadOnlyList<ChartSeries> Series, Func<double, string> Format,
    double? YMin = null, double? YMax = null, bool HasData = true, IReadOnlyList<DateTime>? Times = null)
{
    public static DeckChart Empty { get; } = new(Array.Empty<string>(), Array.Empty<ChartSeries>(), v => v.ToString("0.#", Fmt.French), HasData: false);
}

// The charts of the dashboard slides, as the kiosk draws them
// (SpigaDashboards.App DashboardCharts, LiveCharts) but as SpigaChart series:
// same figures from the same buckets, one time step per label.
public static class DeckCharts
{
    private static readonly CultureInfo French = Fmt.French;

    public const string Blue = "#3987E5";
    public const string Green = "#2ECC71";
    public const string Orange = "#EC835A";
    public const string Red = "#D03B3B";
    public const string Grey = "#9AA4B2";
    public static readonly string[] Palette = { "#3987E5", "#199E70", "#EC835A", "#9085E9", "#FAB219", "#E74C3C", "#1ABC9C", "#7F8C8D" };

    public sealed record Step(string Label, IReadOnlyCollection<ProductionBucket> Lots);

    public static List<Step> Steps(IEnumerable<(DateTime Key, string Label, List<ProductionBucket> Lots)> groups)
        => groups.Select(g => new Step(g.Label, g.Lots)).ToList();

    // Share of each step the line was producing over the time it was
    // required to run (0 to 100 %).
    public static DeckChart OnOff(IReadOnlyList<Step> steps)
    {
        if (steps.Count == 0)
            return DeckChart.Empty;
        var values = steps.Select(s =>
        {
            var required = s.Lots.Sum(l => l.RequiredMinutes);
            return (double?)(required > 0 ? Math.Round(Math.Min(1, s.Lots.Sum(l => l.RunMinutes) / required) * 100) : 0);
        }).ToList();
        return new DeckChart(steps.Select(s => s.Label).ToList(), new[] { new ChartSeries("En marche", values, Green) },
            v => $"{v:0} %", 0, 100);
    }

    // Average speed (bars) and max speed reached (line), bags / minute.
    public static DeckChart Speed(IReadOnlyList<Step> steps)
    {
        var avg = steps.Select(s => KpiCalculator.AverageSpeed(s.Lots) is { } v ? (double?)Math.Round(v, 1) : null).ToList();
        if (avg.All(v => v == null))
            return DeckChart.Empty;
        var max = steps.Select(s =>
        {
            var m = s.Lots.Select(l => l.MaxSpeedBagsPerMinute).DefaultIfEmpty(0).Max();
            return m > 0 ? (double?)Math.Round(m, 1) : null;
        }).ToList();
        return new DeckChart(steps.Select(s => s.Label).ToList(),
            new[] { new ChartSeries("Vitesse moy.", avg, Blue), new ChartSeries("Vitesse max", max, Orange, ChartKind.Line) },
            v => v.ToString("0", French), 0);
    }

    // Net average weight vs its target per step (bags-weighted), steps
    // without trustworthy weight data skipped.
    public static DeckChart Weight(IReadOnlyList<Step> steps)
    {
        var weighed = steps
            .Select(s => (s.Label, Bags: s.Lots.Sum(l => l.WeighedBags), Sum: s.Lots.Sum(l => l.WeighedWeightSum), Target: s.Lots.Sum(l => l.WeighedTargetSum)))
            .Where(s => s.Bags > 0)
            .Select(s => (s.Label, Avg: Math.Round(s.Sum / s.Bags, 1), Target: Math.Round(s.Target / s.Bags, 1)))
            .ToList();
        if (weighed.Count == 0)
            return DeckChart.Empty;
        var all = weighed.SelectMany(w => new[] { w.Avg, w.Target }).ToList();
        var pad = Math.Max(5, (all.Max() - all.Min()) * 0.2);
        return new DeckChart(weighed.Select(w => w.Label).ToList(),
            new[]
            {
                new ChartSeries("Poids moyen", weighed.Select(w => (double?)w.Avg).ToList(), Green, ChartKind.Line),
                new ChartSeries("Cible", weighed.Select(w => (double?)w.Target).ToList(), Grey, ChartKind.Line, Reference: true),
            },
            v => v.ToString("0.0", French) + " g", Math.Max(0, Math.Floor(all.Min() - pad)), Math.Ceiling(all.Max() + pad));
    }

    // TRS per step with the machine's target.
    public static DeckChart Trs(IReadOnlyList<Step> steps, double target)
    {
        if (steps.Count == 0)
            return DeckChart.Empty;
        var trs = steps.Select(s => KpiCalculator.Summarize(s.Lots, KpiCalculator.AvailabilityPercent(s.Lots)).TrsPercent is { } v ? (double?)Math.Round(v, 1) : null).ToList();
        return new DeckChart(steps.Select(s => s.Label).ToList(),
            new[]
            {
                new ChartSeries("TRS", trs, Blue),
                new ChartSeries($"Cible {target:0} %", steps.Select(_ => (double?)target).ToList(), Green, ChartKind.Line, Reference: true),
            },
            v => $"{v:0} %", 0, Math.Max(100, Math.Ceiling(trs.Max(v => v ?? 0) / 10) * 10));
    }

    // Net weight produced per step, one series per target weight (most
    // produced first, piled up), in kg or t.
    public static DeckChart ProductionByWeight(IReadOnlyList<Step> steps)
    {
        var weights = steps.SelectMany(s => s.Lots).Where(l => l.TargetNetWeight > 0)
            .GroupBy(l => Math.Round(l.TargetNetWeight))
            .OrderByDescending(g => g.Sum(l => l.TotalBags))
            .Select(g => g.Key)
            .ToList();
        if (weights.Count == 0 || steps.All(s => s.Lots.Sum(l => l.TotalBags) == 0))
            return DeckChart.Empty;
        var tonnes = steps.Max(s => KpiCalculator.ByWeight(s.Lots).Sum(w => w.ProductionKg)) >= 10_000;
        var unit = tonnes ? 1000.0 : 1.0;
        var series = weights.Select((w, i) => new ChartSeries($"{w:0} g",
            steps.Select(s => (double?)Math.Round(KpiCalculator.ByWeight(s.Lots.Where(l => Math.Round(l.TargetNetWeight) == w).ToList())
                .Sum(k => k.ProductionKg) / unit, 1)).ToList(),
            Palette[i % Palette.Length], Stack: "w")).ToList();
        return new DeckChart(steps.Select(s => s.Label).ToList(), series,
            v => tonnes ? v.ToString("0.#", French) + " t" : Fmt.Num(v) + " kg", 0);
    }

    // Weight deviation (% of the target) per step, 0 % reference.
    public static DeckChart Deviation(IReadOnlyList<Step> steps)
    {
        var values = steps.Select(s => KpiCalculator.Summarize(s.Lots, 0).WeightDeviationPercent is { } v ? (double?)Math.Round(v, 2) : null).ToList();
        if (values.All(v => v == null))
            return DeckChart.Empty;
        return new DeckChart(steps.Select(s => s.Label).ToList(),
            new[]
            {
                new ChartSeries("Écart (%)", values, Red, ChartKind.Line),
                new ChartSeries("Cible", steps.Select(_ => (double?)0).ToList(), Grey, ChartKind.Line, Reference: true),
            },
            v => v.ToString("+0.00;-0.00;0", French) + " %");
    }

    // One line per named series (synthesis: one per production line).
    public static DeckChart Lines(IReadOnlyList<string> labels, IReadOnlyList<(string Name, double?[] Values)> lines,
        Func<double, string> format, double? reference = null, string referenceName = "Cible")
    {
        if (lines.All(l => l.Values.All(v => v == null)))
            return DeckChart.Empty;
        var series = lines.Select((l, i) => new ChartSeries(l.Name, l.Values, Palette[i % Palette.Length], ChartKind.Line)).ToList();
        if (reference is { } r)
            series.Add(new ChartSeries(referenceName, labels.Select(_ => (double?)r).ToList(), Grey, ChartKind.Line, Reference: true));
        return new DeckChart(labels, series, format);
    }

    // One bar per label (machines, teams).
    public static DeckChart Bars(IReadOnlyList<string> labels, IReadOnlyList<double?> values, string name, Func<double, string> format,
        double? target = null, string? targetName = null)
    {
        if (labels.Count == 0 || values.All(v => v is null or 0))
            return DeckChart.Empty;
        var series = new List<ChartSeries> { new(name, values, Blue) };
        if (target is { } t)
            series.Add(new ChartSeries(targetName ?? $"Cible {t:0} %", labels.Select(_ => (double?)t).ToList(), Green, ChartKind.Line, Reference: true));
        return new DeckChart(labels, series, format, 0);
    }

    // One bar per label, piled up by series (production per machine and team).
    public static DeckChart Stacked(IReadOnlyList<string> labels, IReadOnlyList<(string Name, IReadOnlyList<double?> Values)> parts,
        Func<double, string> format)
    {
        if (labels.Count == 0 || parts.All(p => p.Values.All(v => v is null or 0)))
            return DeckChart.Empty;
        return new DeckChart(labels, parts.Select((p, i) => new ChartSeries(p.Name, p.Values, Palette[i % Palette.Length], Stack: "s")).ToList(),
            format, 0);
    }

    // Net average weight vs target over time (kiosk WeightLots): one point
    // per Stiavelli lot at the middle of its span, lots of one production
    // grouped up to 1000 bags so every line shows comparable steps; two lots
    // more than 2 min apart are not joined. The target is flat over each lot.
    // live = poste in progress: the last group still under 1000 bags is left out.
    public static DeckChart WeightLots(IReadOnlyList<LotSpan> lots, DateTime start, DateTime end, bool live)
    {
        var spans = GroupLots(lots, live)
            .Select(l => (From: l.StartTime < start ? start : l.StartTime, To: l.EndTime > end ? end : l.EndTime, Avg: Math.Round(l.Average, 1), l.Target))
            .Where(s => s.To > s.From)
            .OrderBy(s => s.From)
            .ToList();
        if (spans.Count == 0)
            return DeckChart.Empty;
        // Points of both series on one time list: the curve at lot middles,
        // the target at lot edges; NaN = a gap in the line.
        var points = new List<(DateTime T, double? Avg, double? Target)>();
        for (var i = 0; i < spans.Count; i++)
        {
            var s = spans[i];
            if (i > 0 && (s.From - spans[i - 1].To).TotalMinutes > 2)
                points.Add((spans[i - 1].To.AddSeconds(1), double.NaN, double.NaN));
            points.Add((s.From, null, s.Target));
            points.Add((s.From + (s.To - s.From) / 2, s.Avg, null));
            points.Add((s.To, null, s.Target));
        }
        var all = spans.SelectMany(s => new[] { s.Avg, s.Target }).ToList();
        var pad = Math.Max(5, (all.Max() - all.Min()) * 0.2);
        return new DeckChart(Array.Empty<string>(),
            new[]
            {
                new ChartSeries("Poids moyen (lot)", points.Select(p => p.Avg).ToList(), Green, ChartKind.Line),
                new ChartSeries("Cible", points.Select(p => p.Target).ToList(), Grey, ChartKind.Line, Reference: true),
            },
            v => v.ToString("0.0", French) + " g", Math.Max(0, Math.Floor(all.Min() - pad)), Math.Ceiling(all.Max() + 2 * pad),
            Times: points.Select(p => p.T).ToList());
    }

    // Lines don't all close lots of the same size (PL03: 50 bags, the others
    // 1000): consecutive lots of one production are merged up to 1000 bags;
    // a production change closes the group early (kiosk GroupLots).
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
            open.Average = open.Bags > 0 ? weightSum / open.Bags : 0;
            open.Target = lot.Target;
            if (open.Bags >= 1000)
            {
                groups.Add(open);
                open = null;
            }
        }
        if (open != null && !live)
            groups.Add(open);
        return groups;
    }
}
