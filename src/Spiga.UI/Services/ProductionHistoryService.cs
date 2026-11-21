using Spiga.Core.Security;
using Spiga.Data;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels;
using SpigaDashboards.App.ViewModels.Slides;

namespace Spiga.UI.Services;

// Production > Tableau de bord > Historique (kiosk: HistoryViewModel): the
// production hour by hour over chosen dates and filters, its totals, and the
// Excel / PDF report (ExportService, the kiosk's). Read through repositories
// limited to the account's machines; a team-bound account counts its team only.
public sealed class ProductionHistoryService(
    IDbConnectionFactory production, AppDbOptions appDb, AppSettings settings, TeamScheduleService teams)
{
    // A year of hours over ten lines is ~90 000 rows: the screen keeps the
    // newest; the exports have them all.
    public const int MaxDays = 366;

    public IReadOnlyList<string> Teams => teams.TeamNames;

    public IReadOnlyList<double> Weights
        => settings.NominalRates.Where(r => r.WeightGrams > 0).Select(r => r.WeightGrams).Distinct().Order().ToList();

    public static bool CanExport(Actor actor) => NativePages.ExportPermission(NativePage.Dashboard) is { } export
                                                 && actor.Covers(export, DashboardDeckService.Permission);

    public async Task<List<Machine>> MachinesAsync(Actor actor)
        => (await Repository(actor).GetMachinesAsync())
            .Where(m => !settings.HiddenMachineIds.Contains(m.MachineId))
            .OrderBy(m => Array.IndexOf(SlideDeck.SectorOrder, m.Sector)).ThenBy(m => m.MachineId)
            .ToList();

    // from / to: whole days, to included.
    public async Task<ProductionReport> SearchAsync(Actor actor, DateTime fromDay, DateTime toDay, DashboardFilter filter, CancellationToken cancel = default)
    {
        filter = DashboardDeckService.ForActor(actor, filter);
        var from = fromDay.Date;
        var to = toDay.Date.AddDays(1);
        if (to <= from)
            (from, to) = (to.AddDays(-1), from.AddDays(1));
        if ((to - from).TotalDays > MaxDays)
            throw new RuleViolationException($"Choisissez au plus {MaxDays} jours.");
        if (from < settings.DataStartDate && to > settings.DataStartDate)
            from = settings.DataStartDate.Date;

        var repository = Repository(actor);
        var machines = (await MachinesAsync(actor)).Where(filter.IncludesMachine).ToList();
        var lots = filter.Apply(await repository.GetLotsForMachinesAsync(machines.Select(m => m.MachineId).ToList(), from, to), teams);
        cancel.ThrowIfCancellationRequested();

        var names = machines.ToDictionary(m => m.MachineId, m => m.MachineName);
        var sectors = machines.ToDictionary(m => m.MachineId, m => m.Sector);
        var rows = lots
            .Where(l => l.IsProducing)
            .OrderByDescending(l => l.StartTime)
            .ThenBy(l => l.MachineId)
            .Select(l =>
            {
                var k = KpiCalculator.Summarize(new[] { l }, KpiCalculator.AvailabilityPercent(new[] { l }));
                return new HistoryRow
                {
                    StartTime = l.StartTime,
                    MachineId = l.MachineId,
                    MachineName = names.GetValueOrDefault(l.MachineId, l.MachineId),
                    Sector = sectors.GetValueOrDefault(l.MachineId, ""),
                    ShiftName = KpiCalculator.ShiftName(l.ShiftId),
                    Team = teams.TeamOf(l),
                    ProductName = l.ProductName ?? "",
                    TargetNetWeight = l.TargetNetWeight,
                    TareGrams = settings.FindTare(l.MachineId, l.TargetNetWeight),
                    AvgWeight = l.AvgWeight is { } a ? Math.Round(a, 1) : null,
                    DeviationGrams = k.WeightDeviationGrams,
                    ProductionKg = Math.Round(l.TotalBags * l.TargetNetWeight / 1000.0, 1),
                    TotalBags = l.TotalBags,
                    BagsInTolerance = l.BagsInTolerance,
                    BagsOverTolerance = l.BagsOverTolerance,
                    BagsUnderTolerance = l.BagsUnderTolerance,
                    SpeedBagsPerMinute = k.SpeedBagsPerMinute ?? 0,
                    MaxSpeedBagsPerMinute = k.MaxSpeedBagsPerMinute,
                    AvailabilityPercent = k.AvailabilityPercent,
                    TrsPercent = k.TrsPercent,
                    IsConform = l.IsConform,
                };
            })
            .ToList();
        return ProductionReport.Build(lots, machines, rows, teams, settings, from, to, filter.Describe());
    }

    // The report file, for an account with the export right only.
    public static (string Name, string Mime, byte[] Bytes) Export(Actor actor, ProductionReport report, bool pdf)
    {
        if (!CanExport(actor))
            throw new AccessDeniedException("Votre compte ne peut pas exporter le tableau de bord.");
        if (report.Detail.Count == 0)
            throw new RuleViolationException("Aucun résultat à exporter : lancez d'abord une recherche.");
        var name = $"rapport_production_{report.From:yyyyMMdd}_{report.To.AddDays(-1):yyyyMMdd}";
        return pdf
            ? (name + ".pdf", "application/pdf", ExportService.PdfBytes(report))
            : (name + ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ExportService.ExcelBytes(report));
    }

    private ProductionRepository Repository(Actor actor) => ScopedData.For(actor, DashboardDeckService.Permission, production, appDb, settings).Production;
}
