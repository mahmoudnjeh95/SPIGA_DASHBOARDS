using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.ViewModels.Slides;

// One photo of the run-state slide: the machine's state right now.
public class MachineRunTile
{
    public string MachineId { get; init; } = "";
    public string MachineName { get; init; } = "";
    public bool IsConnected { get; init; }
    public bool BeltRunning { get; init; }
    public string RunStateLabel => !IsConnected ? "Pas de donnees" : BeltRunning ? "En marche" : "A l'arret";
}

// First Cumul slide: nothing but the run-state photo of every machine,
// captioned with its name from Parametres, and how many run right now. Live
// state whatever the top-bar period (same snapshot as the machine headers).
public class RunStateSlideViewModel
{
    public string Title { get; init; } = "";
    public IReadOnlyList<MachineRunTile> Tiles { get; init; } = Array.Empty<MachineRunTile>();
    public int RunningCount => Tiles.Count(t => t.BeltRunning);

    // Up to 5 photos on one row, more on two rows (10 machines = 2 x 5).
    public int TileColumns => Tiles.Count <= 5 ? Math.Max(1, Tiles.Count) : (Tiles.Count + 1) / 2;

    public static async Task<RunStateSlideViewModel> LoadAsync(ProductionRepository repository, string title, IReadOnlyList<Machine> machines)
    {
        var tiles = new List<MachineRunTile>();
        foreach (var m in machines)
        {
            var status = await repository.GetLatestStatusAsync(m.MachineId);
            tiles.Add(new MachineRunTile
            {
                MachineId = m.MachineId,
                MachineName = m.MachineName,
                IsConnected = status?.IsConnected ?? false,
                BeltRunning = status?.BeltRunning ?? false,
            });
        }
        return new RunStateSlideViewModel { Title = title, Tiles = tiles };
    }
}
