using System.Globalization;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels.Slides;

// "Aujourd'hui" / "Poste precedent" slide: one machine, one poste in full
// (the poste running now, or the one before it), and under it the postes
// already worked on the same shift day (Matin; Matin + Soir). The poste and
// team filters pick which postes are shown; the weight filter restricts the
// buckets the figures come from.
public class MachineDaySlideViewModel
{
    public string SectorLabel { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string PeriodLabel { get; init; } = "";
    public bool IsConnected { get; init; }
    public bool BeltRunning { get; init; }
    public string RunStateLabel => BeltRunning ? "En marche" : "A l'arret";
    public ProductionInfo? CurrentProduction { get; init; }
    public string CurrentProductionLabel { get; init; } = "";
    public string FilterLabel { get; init; } = "";
    public ShiftSummaryViewModel Main { get; init; } = null!;
    public IReadOnlyList<ShiftSummaryViewModel> PreviousShifts { get; init; } = Array.Empty<ShiftSummaryViewModel>();
    public bool HasPreviousShifts => PreviousShifts.Count > 0;

    // Returns a MachinePeriodSlideViewModel over the whole shift day instead
    // when no poste of that day matches the poste / team filters.
    public static async Task<object> LoadAsync(
        ProductionRepository repository,
        TeamScheduleService teamSchedule,
        AppSettings settings,
        string sector,
        Machine machine,
        DashboardFilter filter,
        bool previousShift)
    {
        var now = DateTime.Now;
        var currentShiftId = KpiCalculator.CurrentShiftId(now);
        // A Nuit after midnight belongs to yesterday's shift day.
        var shiftDate = TeamScheduleService.GetShiftDate(now, currentShiftId);
        var (date, shiftId) = previousShift
            ? currentShiftId > 1 ? (shiftDate, currentShiftId - 1) : (shiftDate.AddDays(-1), 3)
            : (shiftDate, currentShiftId);

        // Postes 1..shiftId of that shift day, oldest first.
        var postes = Enumerable.Range(1, shiftId)
            .Where(id => filter.ShiftId is null || filter.ShiftId == id)
            .Where(id => filter.Team is null || teamSchedule.GetTeamForShift(date, id) == filter.Team)
            .ToList();
        if (postes.Count == 0)
        {
            return await MachinePeriodSlideViewModel.LoadAsync(repository, teamSchedule, settings, sector, machine,
                date, date.AddDays(1), previousShift ? "Poste precedent" : "Aujourd'hui", filter);
        }

        var status = await repository.GetLatestStatusAsync(machine.MachineId);
        // The Nuit poste ends at 06:00 the next calendar day.
        var allLots = await repository.GetLotsAsync(machine.MachineId, date, date.AddDays(1).AddHours(6));
        var lots = filter.Apply(allLots, teamSchedule);
        var trsTarget = settings.TrsTarget(machine.MachineId);

        var mainId = postes[^1];
        return new MachineDaySlideViewModel
        {
            SectorLabel = sector,
            MachineName = machine.MachineName,
            PeriodLabel = previousShift ? "Poste precedent" : "",
            IsConnected = status?.IsConnected ?? false,
            BeltRunning = status?.BeltRunning ?? false,
            CurrentProduction = status?.CurrentProduction,
            CurrentProductionLabel = FormatProduction(status?.CurrentProduction, allLots),
            FilterLabel = filter.Describe(),
            Main = ShiftSummaryViewModel.Build(mainId, date, withCharts: true,
                isRunning: !previousShift && mainId == currentShiftId, lots, teamSchedule, now, trsTarget),
            PreviousShifts = postes.Take(postes.Count - 1)
                .Select(id => ShiftSummaryViewModel.Build(id, date, false, false, lots, teamSchedule, now, trsTarget))
                .ToList(),
        };
    }

    // Same French number format as the tiles (WPF bindings use fr-FR, see App).
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    internal static string FormatProduction(ProductionInfo? production, IReadOnlyCollection<ProductionBucket> lots)
    {
        if (production == null)
            return "Aucune donnee recue de StatsViewer depuis 6 h";

        var state = production.IsTerminated ? "Production terminee" : "Production en cours";
        // Lots of this production in the loaded period first (real lot
        // averages); otherwise Stiavelli's running average, if it looks sane.
        var avgWeight = KpiCalculator.ProductionAverageWeight(lots, production.ProductionId, production.TargetNetWeight) ?? production.AvgWeight;
        var weight = avgWeight is { } avg ? string.Create(French, $" - moyen {avg:0.0} g / cible {production.TargetNetWeight:0} g") : "";
        var quality = production.TotalBags > 0 ? string.Create(French, $"{production.QualityPercent:0.0} %") : "— %";
        return string.Create(French, $"{state} : {production.ProductName} depuis le {production.StartTime:dd/MM/yyyy HH:mm} - ") +
               string.Create(French, $"{production.TotalBags:N0} sacs, {quality} conformes{weight}");
    }
}
