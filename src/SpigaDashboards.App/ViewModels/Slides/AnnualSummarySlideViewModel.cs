using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels.Slides;

// Slides 9-13: cumul mensuel par secteur sur toute l'annee (tableau + graphiques).
// One instance per sector; months with no data yet (e.g. the rest of the
// current year) are omitted rather than shown as a row of zeros.
public partial class AnnualSummarySlideViewModel : ObservableObject
{
    private static readonly string[] MonthNames =
    {
        "Jan", "Fev", "Mar", "Avr", "Mai", "Jun", "Jul", "Aou", "Sep", "Oct", "Nov", "Dec",
    };

    [ObservableProperty] private string sectorLabel = "";
    [ObservableProperty] private int year;
    [ObservableProperty] private List<MonthlySectorKpi> monthlyKpis = new();

    // TRS mensuel par secteur.
    [ObservableProperty] private ISeries[] trsTrendSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] trsTrendXAxes = Array.Empty<Axis>();

    // Tendance des ecarts de poids cumules (bags-weighted AvgWeight - Target, per month).
    [ObservableProperty] private ISeries[] weightDeviationTrendSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] weightDeviationTrendXAxes = Array.Empty<Axis>();

    // A year of sub-minute Stiavelli snapshots is heavy to re-aggregate on
    // SQLEXPRESS, and these monthly figures barely move: keep each sector's
    // table for CacheDuration. Only the data is cached -- charts are rebuilt
    // every time so they follow the current theme.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
    private static readonly Dictionary<string, (DateTime LoadedAt, int Year, List<MonthlySectorKpi> Monthly)> Cache = new();

    public static void ClearCache() => Cache.Clear();

    public static async Task<AnnualSummarySlideViewModel> LoadAsync(ProductionRepository repository, string sector)
    {
        var slide = new AnnualSummarySlideViewModel { SectorLabel = sector, Year = DateTime.Now.Year };

        if (!Cache.TryGetValue(sector, out var cached)
            || cached.Year != slide.Year
            || DateTime.Now - cached.LoadedAt > CacheDuration)
        {
            cached = (DateTime.Now, slide.Year, await LoadMonthlyAsync(repository, sector, slide.Year));
            Cache[sector] = cached;
        }

        slide.MonthlyKpis = cached.Monthly;
        slide.BuildTrsTrendChart(cached.Monthly);
        slide.BuildWeightDeviationTrendChart(cached.Monthly);

        return slide;
    }

    private static async Task<List<MonthlySectorKpi>> LoadMonthlyAsync(ProductionRepository repository, string sector, int year)
    {
        var from = new DateTime(year, 1, 1);
        var to = from.AddYears(1);

        var lots = await repository.GetLotsForSectorAsync(sector, from, to);

        var monthly = new List<MonthlySectorKpi>();
        for (var month = 1; month <= 12; month++)
        {
            var monthLots = lots.Where(l => l.StartTime.Month == month).ToList();
            if (monthLots.Count == 0)
                continue;

            var kpi = KpiCalculator.Summarize(monthLots, KpiCalculator.AvailabilityPercent(monthLots));
            var avgWeight = KpiCalculator.WeightedAverageWeight(monthLots) ?? 0;

            monthly.Add(new MonthlySectorKpi
            {
                Month = month,
                MonthName = MonthNames[month - 1],
                AvgWeight = Math.Round(avgWeight, 1),
                WeightDeviationGrams = kpi.WeightDeviationGrams,
                TrsPercent = kpi.TrsPercent,
                BagsConform = kpi.BagsInTolerance,
                BagsOver = kpi.BagsOverTolerance,
                BagsUnder = kpi.BagsUnderTolerance,
            });
        }

        return monthly;
    }

    private void BuildTrsTrendChart(List<MonthlySectorKpi> monthly)
    {
        TrsTrendSeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = monthly.Select(m => m.TrsPercent).ToArray(),
                Name = "TRS %",
                Stroke = new SolidColorPaint(new SKColor(0x3B, 0x82, 0xF6), 3),
                Fill = null,
                GeometrySize = 10,
                GeometryStroke = new SolidColorPaint(new SKColor(0x3B, 0x82, 0xF6), 2),
            },
        };
        TrsTrendXAxes = new[] { ChartHelpers.BuildXAxis(monthly.Select(m => m.MonthName)) };
    }

    private void BuildWeightDeviationTrendChart(List<MonthlySectorKpi> monthly)
    {
        WeightDeviationTrendSeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = monthly.Select(m => m.WeightDeviationGrams).ToArray(),
                Name = "Ecart (g)",
                Stroke = new SolidColorPaint(new SKColor(0xE7, 0x4C, 0x3C), 3),
                Fill = null,
                GeometrySize = 10,
                GeometryStroke = new SolidColorPaint(new SKColor(0xE7, 0x4C, 0x3C), 2),
            },
        };
        WeightDeviationTrendXAxes = new[] { ChartHelpers.BuildXAxis(monthly.Select(m => m.MonthName)) };
    }
}
