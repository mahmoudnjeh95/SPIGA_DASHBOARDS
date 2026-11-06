using System.Globalization;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels.Slides;

// "Aujourd'hui" / "Poste precedent" slide: one machine, one poste in full
// (the poste running now, or the one before it), and under it the postes
// already worked on the same shift day (Matin; Matin + Après-midi). The poste and
// team filters pick which postes are shown; the weight filter restricts the
// buckets the figures come from.
public class MachineDaySlideViewModel
{
    public string SectorLabel { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string PeriodLabel { get; init; } = "";
    public bool IsConnected { get; init; }
    public bool BeltRunning { get; init; }
    public SlotKind? PlannedSlot { get; init; }
    public bool IsPlannedStop => PlannedSlot == SlotKind.PlannedStop;
    public bool IsMaintenance => PlannedSlot == SlotKind.Maintenance;
    public string? PlannedLabel => RunStateText.Planned(PlannedSlot);
    // The line's stop in progress, if any: red banner under the header.
    public OngoingStop? OngoingStop { get; init; }
    // A declared panne / planned stop / maintenance going on now: one big
    // panel instead of the KPIs and charts until the line runs again.
    public OngoingStop? DeclaredStop => OngoingStop is { IsDeclared: true } && PeriodLabel.Length == 0 ? OngoingStop : null;
    public string RunStateLabel => PlannedLabel ?? (BeltRunning ? "En marche" : "À l'arrêt");
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
                date, date.AddDays(1), previousShift ? "Poste précédent" : "Aujourd'hui", filter);
        }

        var status = await repository.GetLatestStatusAsync(machine.MachineId);
        // The Nuit poste ends at 06:00 the next calendar day.
        var allLots = await repository.GetLotsAsync(machine.MachineId, date, date.AddDays(1).AddHours(6));
        var lots = filter.Apply(allLots, teamSchedule);
        var trsBand = settings.TrsBandFor(machine.MachineId);

        var mainId = postes[^1];
        var mainStart = date.AddHours(KpiCalculator.ShiftStartHour(mainId));
        // 2 h past the poste end: lots that complete its last 1000-bag group
        // (see DashboardCharts.GroupLots); the chart clips them at the end.
        var lotSpans = (await repository.GetLotSpansAsync(machine.MachineId, mainStart, mainStart.AddHours(10)))
            ?.Where(l => filter.MatchesWeight(l.Target)).ToList();
        return new MachineDaySlideViewModel
        {
            SectorLabel = Machine.SectorLabel(sector),
            MachineName = machine.MachineName,
            PeriodLabel = previousShift ? "Poste précédent" : "",
            IsConnected = status?.IsConnected ?? false,
            BeltRunning = status?.BeltRunning ?? false,
            PlannedSlot = (await repository.GetActiveSlotKindsAsync()).TryGetValue(machine.MachineId, out var slot) ? slot : null,
            OngoingStop = (await OngoingStopService.GetAsync(repository, settings)).GetValueOrDefault(machine.MachineId),
            CurrentProduction = status?.CurrentProduction,
            CurrentProductionLabel = FormatProduction(status?.CurrentProduction, allLots),
            FilterLabel = filter.Describe(),
            Main = ShiftSummaryViewModel.Build(mainId, date, withCharts: true,
                isRunning: !previousShift && mainId == currentShiftId, lots, teamSchedule, now, trsBand, lotSpans),
            PreviousShifts = postes.Take(postes.Count - 1)
                .Select(id => ShiftSummaryViewModel.Build(id, date, false, false, lots, teamSchedule, now, trsBand))
                .ToList(),
        };
    }

    // Same French number format as the tiles (WPF bindings use fr-FR, see App).
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    internal static string FormatProduction(ProductionInfo? production, IReadOnlyCollection<ProductionBucket> lots)
    {
        if (production == null)
            return "Aucune donnée reçue de StatsViewer depuis 6 h";

        var state = production.IsTerminated ? "Terminée" : "En cours";
        // Lots of this production in the loaded period first (real lot
        // averages); otherwise Stiavelli's running average, if it looks sane.
        var avgWeight = KpiCalculator.ProductionAverageWeight(lots, production.ProductionId, production.TargetNetWeight) ?? production.AvgWeight;
        var weight = avgWeight is { } avg ? string.Create(French, $"  ·  moyen {avg:0.0} g") : "";
        var quality = production.TotalBags > 0 ? string.Create(French, $"{production.QualityPercent:0.0} %") : "— %";
        var since = production.StartTime.Date == DateTime.Now.Date
            ? $"depuis {production.StartTime:HH:mm}"
            : $"depuis le {production.StartTime:dd/MM HH:mm}";
        return string.Create(French, $"{state} : {production.ProductName} {production.TargetNetWeight:0} g  ·  {since}  ·  ") +
               string.Create(French, $"{production.TotalBags:N0} sacs  ·  {quality} conformes{weight}").Replace(' ', ' ');
    }
}
