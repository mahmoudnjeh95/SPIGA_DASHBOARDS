using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels.Slides;

// One photo of the run-state slide: the machine's state right now.
public class MachineRunTile
{
    public string MachineId { get; init; } = "";
    public string MachineName { get; init; } = "";
    public bool IsConnected { get; init; }
    public bool BeltRunning { get; init; }
    public SlotKind? PlannedSlot { get; init; }
    public bool IsPlannedStop => PlannedSlot == SlotKind.PlannedStop;
    public bool IsMaintenance => PlannedSlot == SlotKind.Maintenance;
    public string? PlannedLabel => RunStateText.Planned(PlannedSlot);
    public string RunStateLabel => PlannedLabel ?? (!IsConnected ? "Pas de données" : BeltRunning ? "En marche" : "À l'arrêt");

    // The line's stop in progress, and its cause pill under the name: only
    // once the stop has a cause (qualified in Planification > Pannes, or a
    // calendar slot). A stop nobody qualified shows nothing more than its
    // red photo.
    public OngoingStop? Stop { get; init; }
    public bool HasCause => Stop is { IsUnqualified: false };
    public string CauseLabel => Stop?.ShortCause ?? "";
    // Wrench: panne / maintenance; calendar: planned; info: any other cause (Segoe MDL2 Assets).
    public string CauseGlyph => Stop?.Kind switch
    {
        OngoingStopKind.Panne or OngoingStopKind.Maintenance => "\uE90F",
        OngoingStopKind.Planned => "\uE787",
        _ => "\uE946",
    };
    // Same colours as the scrolling stop bar (Parametres > Couleurs); a
    // maintenance in the yellow of its photo frame, with dark text.
    public string CauseColor => Stop == null ? "#00000000"
        : Stop.Kind == OngoingStopKind.Maintenance ? "#FAB219" : Stop.TickerColor;
    public string CauseTextColor => Stop?.Kind == OngoingStopKind.Maintenance ? "#1A1406" : "#FFFFFF";
    public string CauseDuration => Stop == null ? "" : "depuis " + Stop.DurationText;
    public string CauseReturn => Stop?.ShortReturnText is { Length: > 0 } text ? "  ·  " + text : "";
    public bool IsOverdue => Stop?.IsOverdue ?? false;
}

// Banner of a run-state photo while the line is in a slot of the
// Planification board (grey for a planned stop, yellow for a maintenance).
public static class RunStateText
{
    public static string? Planned(SlotKind? slot) => slot switch
    {
        SlotKind.PlannedStop => "ARRÊT PLANIFIÉ",
        SlotKind.Maintenance => "MAINTENANCE",
        _ => null,
    };
}

// First Cumul slide: nothing but the run-state photo of every machine,
// captioned with its name from Parametres, and how many run right now. Live
// state whatever the top-bar period (same snapshot as the machine headers),
// greyed / yellowed while the line is in a planned stop / maintenance.
public class RunStateSlideViewModel
{
    public string Title { get; init; } = "";
    public IReadOnlyList<MachineRunTile> Tiles { get; init; } = Array.Empty<MachineRunTile>();
    public int RunningCount => Tiles.Count(t => t.BeltRunning);
    // Every caption keeps room for the cause lines as soon as one tile has
    // them, so all the photos stay the same size.
    public bool HasCauses => Tiles.Any(t => t.HasCause);

    // Up to 5 photos on one row, more on two rows (10 machines = 2 x 5).
    public int TileColumns => Tiles.Count <= 5 ? Math.Max(1, Tiles.Count) : (Tiles.Count + 1) / 2;

    public static async Task<RunStateSlideViewModel> LoadAsync(ProductionRepository repository, AppSettings settings, string title, IReadOnlyList<Machine> machines)
    {
        var tiles = new List<MachineRunTile>();
        var slots = await repository.GetActiveSlotKindsAsync();
        // A line with a stop in progress is not running, even if a few bags
        // moved its counter: photo and count follow the stop alert.
        var stops = await OngoingStopService.GetAsync(repository, settings);
        foreach (var m in machines)
        {
            var status = await repository.GetLatestStatusAsync(m.MachineId);
            tiles.Add(new MachineRunTile
            {
                MachineId = m.MachineId,
                MachineName = m.MachineName,
                IsConnected = status?.IsConnected ?? false,
                BeltRunning = (status?.BeltRunning ?? false) && !stops.ContainsKey(m.MachineId),
                PlannedSlot = slots.TryGetValue(m.MachineId, out var slot) ? slot : null,
                Stop = stops.GetValueOrDefault(m.MachineId),
            });
        }
        return new RunStateSlideViewModel { Title = title, Tiles = tiles };
    }
}
