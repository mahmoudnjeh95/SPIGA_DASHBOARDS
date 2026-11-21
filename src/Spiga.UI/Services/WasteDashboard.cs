using Spiga.Core.Plant;
using Spiga.Core.Security;
using Spiga.Core.Waste;
using SpigaDashboards.App.Models;

namespace Spiga.UI.Services;

// Filters of the Déchets screen. To is exclusive (calendar days [From, To)).
// A machine narrows to packaging waste: product waste has no machine.
public sealed record WasteFilter(
    DateTime From, DateTime To, string? Sector = null, string? MachineId = null, string? Team = null, int? ShiftId = null,
    bool ValidatedOnly = false);

// Waste of one day / machine / sector / team / poste / reason, beside the
// production packed there (production is 0 where it does not apply).
public sealed record WasteGroup(
    string Key, string Label, string? Sub, double PackagingKg, double ProductKg, double ProductionKg, int Entries, int Drafts)
{
    public double TotalKg => PackagingKg + ProductKg;
    public double? PackagingPercent => WastePercent.Of(PackagingKg, ProductionKg);
    public double? ProductPercent => WastePercent.Of(ProductKg, ProductionKg);
    public double? TotalPercent => WastePercent.Of(TotalKg, ProductionKg);
}

public static class WastePercent
{
    // Same formula as the Vue d'ensemble tile: waste / (packed + waste).
    public static double? Of(double wasteKg, double productionKg)
        => productionKg > 0 ? wasteKg / (productionKg + wasteKg) * 100 : null;
}

public sealed class WasteReport
{
    public required WasteFilter Filter { get; init; }
    public string? ProductionError { get; init; }
    public required WasteGroup Total { get; init; }
    public int PackagingEntries { get; init; }
    public int ProductEntries { get; init; }
    public List<WasteGroup> Days { get; init; } = new();
    public List<WasteGroup> Machines { get; init; } = new();
    public List<WasteGroup> Sectors { get; init; } = new();
    public List<WasteGroup> Teams { get; init; } = new();
    public List<WasteGroup> Shifts { get; init; } = new();
    public List<WasteGroup> Reasons { get; init; } = new();
    public List<WasteEntry> Latest { get; init; } = new();
}

// Figures of the Déchets screen: the waste entries (interfaces A1 / A2) and
// the conditioned production of the production database, inside the actor's
// "Déchets : voir" scope.
public sealed class WasteDashboard(PlantData plant, WasteService waste)
{
    public static readonly string View = Perm.View(Modules.Dechets);
    private const int LatestCount = 20;

    public async Task<WasteReport> LoadAsync(Actor actor, WasteFilter f)
    {
        actor.Demand(View);

        var entries = (await waste.ForDashboardAsync(actor, f.From, f.To.AddDays(-1)))
            .Where(e => WasteService.Covers(actor, View, e))
            .Where(e => f.Sector == null || e.Sector == f.Sector)
            .Where(e => f.MachineId == null || e.MachineId == f.MachineId)
            .Where(e => f.Team == null || e.Team == f.Team)
            .Where(e => f.ShiftId == null || e.ShiftId == f.ShiftId)
            .Where(e => !f.ValidatedOnly || e.Status == WasteStatus.Validee)
            .ToList();

        IReadOnlyList<PlantMachine> machines = Array.Empty<PlantMachine>();
        List<ProductionBucket> lots = new();
        string? error = null;
        try
        {
            machines = (await plant.MachinesFor(actor, View, f.Sector)).Where(m => f.MachineId == null || m.Id == f.MachineId).ToList();
            lots = (await plant.LotsAsync(actor, View, machines, f.From, f.To, f.ShiftId))
                .Where(l => f.Team == null || plant.TeamOf(l) == f.Team)
                .ToList();
        }
        catch (Exception ex) when (ex is not AccessDeniedException)
        {
            error = "Base de production injoignable : les pourcentages ne sont pas calculés. " + ex.Message;
        }

        var sectorOf = machines.ToDictionary(m => m.Id, m => m.Sector);

        var days = new List<WasteGroup>();
        for (var d = f.From.Date; d < f.To.Date; d = d.AddDays(1))
        {
            var day = d;
            days.Add(Group(day.ToString("yyyy-MM-dd"), day.ToString("dd/MM", Fmt.French), null,
                entries.Where(e => e.ProductionDate.Date == day), lots.Where(l => Core.Plant.Shifts.ProductionDate(l.StartTime) == day)));
        }

        // Machines of the scope, and those that only appear in the entries
        // (production database down, machine renamed).
        var machineIds = machines.Select(m => m.Id)
            .Concat(entries.Where(e => e.MachineId != null).Select(e => e.MachineId!))
            .Distinct().OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        var machineRows = machineIds
            .Select(id => Group(id, plant.NameOf(id),
                Core.Plant.Sectors.Label(sectorOf.GetValueOrDefault(id) ?? entries.First(e => e.MachineId == id).Sector),
                entries.Where(e => e.Kind == WasteKind.Emballage && e.MachineId == id), lots.Where(l => l.MachineId == id)))
            .ToList();

        var sectorRows = Core.Plant.Sectors.All
            .Where(s => (f.Sector == null || s == f.Sector) && actor.Allows(View, s))
            .Select(s => Group(s, Core.Plant.Sectors.Label(s), null,
                entries.Where(e => e.Sector == s), lots.Where(l => sectorOf.GetValueOrDefault(l.MachineId) == s)))
            .ToList();

        var teamRows = plant.Teams
            .Where(t => f.Team == null || t == f.Team)
            .Select(t => Group(t, t, null, entries.Where(e => e.Team == t), lots.Where(l => plant.TeamOf(l) == t)))
            .Where(g => g.Entries > 0 || g.ProductionKg > 0 || f.Team != null)
            .ToList();

        var shiftRows = Core.Plant.Shifts.All
            .Where(s => f.ShiftId == null || s == f.ShiftId)
            .Select(s => Group(s.ToString(), Core.Plant.Shifts.Label(s), Core.Plant.Shifts.Hours(s),
                entries.Where(e => e.ShiftId == s), lots.Where(l => l.ShiftId == s)))
            .ToList();

        // Reasons carry no production: their share is of the waste itself.
        var reasonRows = entries
            .GroupBy(e => e.Reason)
            .Select(g => Group(g.Key, g.Key, null, g, Enumerable.Empty<ProductionBucket>()))
            .OrderByDescending(g => g.TotalKg)
            .ToList();

        return new WasteReport
        {
            Filter = f,
            ProductionError = error,
            Total = Group("total", "Total", null, entries, lots),
            PackagingEntries = entries.Count(e => e.Kind == WasteKind.Emballage),
            ProductEntries = entries.Count(e => e.Kind == WasteKind.Produit),
            Days = days,
            Machines = machineRows,
            Sectors = sectorRows,
            Teams = teamRows,
            Shifts = shiftRows,
            Reasons = reasonRows,
            Latest = entries.OrderByDescending(e => e.ProductionDate).ThenByDescending(e => e.ShiftId).ThenByDescending(e => e.CreatedAt)
                .Take(LatestCount).ToList(),
        };
    }

    private static WasteGroup Group(string key, string label, string? sub, IEnumerable<WasteEntry> entries, IEnumerable<ProductionBucket> lots)
    {
        var list = entries.ToList();
        var lotList = lots.ToList();
        return new WasteGroup(key, label, sub,
            list.Where(e => e.Kind == WasteKind.Emballage).Sum(e => e.QuantityKg),
            list.Where(e => e.Kind == WasteKind.Produit).Sum(e => e.QuantityKg),
            lotList.Count == 0 ? 0 : PlantData.Total(lotList).ProductionKg,
            list.Count,
            list.Count(e => e.Status != WasteStatus.Validee));
    }
}

// Links to an entry screen at a given poste, with an entry open in the form.
public static class WasteQuery
{
    public static string EntryLink(WasteEntry e)
        => $"{(e.Kind == WasteKind.Emballage ? "production/dechets/emballage" : "production/dechets/produit")}"
           + $"?date={e.ProductionDate:yyyy-MM-dd}&poste={e.ShiftId}&saisie={e.Id}";

    public static DateTime? Date(string? value)
        => DateTime.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;
}
