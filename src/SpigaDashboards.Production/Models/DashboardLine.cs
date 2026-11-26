namespace SpigaDashboards.App.Models;

// A production line of the web dashboard (Paramètres > Lignes): the
// machines that pack side by side behind it, in display order. Its sector is
// its own: some machines are filed under a sector of their own in the
// plant database ("Pate" for the Ricciarelli ones). PlannedMachines are
// names of machines not in the database yet ("en préparation"): shown
// greyed on the line's slides, counted nowhere.
public sealed class DashboardLine
{
    public string Name { get; set; } = "";
    public string Sector { get; set; } = "";
    public List<string> MachineIds { get; set; } = new();
    public List<string> PlannedMachines { get; set; } = new();

    // "Ligne CC1".
    public string Label => LabelOf(Name);

    public static string LabelOf(string name) => "Ligne " + name.Trim();

    // The plant as organised on 2026-11-26 (as the machine names say: RICC A
    // / RICC B CC1, STV C / STV D CC2; Personnel's lines match).
    public static List<DashboardLine> Defaults() => new()
    {
        new() { Name = "CC1", Sector = "Couscous", MachineIds = { "CC03", "CC04" } },
        new() { Name = "CC2", Sector = "Couscous", MachineIds = { "CC01", "CC02" } },
        new() { Name = "PC1", Sector = "Pate courte", MachineIds = { "RIC1" }, PlannedMachines = { "RIC B PC" } },
        new() { Name = "PC2", Sector = "Pate courte", MachineIds = { "PC01", "PC02", "PC03" } },
        new() { Name = "PL1+2", Sector = "Pate longue", MachineIds = { "RIC2", "PL03", "PL02", "PL01" } },
    };
}
