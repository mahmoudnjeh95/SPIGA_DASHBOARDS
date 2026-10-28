namespace SpigaDashboards.App.ViewModels;

// One entry of a machine drop-down (Historique, Arrets): shown by its
// name(s), filters by id. No id = every machine.
public sealed record MachinePick(string Label, IReadOnlyList<string> MachineIds)
{
    public override string ToString() => Label;
}
