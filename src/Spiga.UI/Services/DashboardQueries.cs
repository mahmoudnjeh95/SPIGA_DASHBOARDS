using Spiga.Core.Plant;
using Spiga.Core.Security;
using Spiga.Core.Waste;
using SpigaDashboards.App.Models;

namespace Spiga.UI.Services;

public sealed class MachineRow
{
    public required MachineFigures Figures { get; init; }
    public MachineState? State { get; set; }
    public double WasteKg { get; set; }
    public PlantMachine Machine => Figures.Machine;
}

public sealed class SectorRow
{
    public required string Sector { get; init; }
    public KpiSummary? Kpi { get; init; }
    public double TrsTarget { get; init; }
    public double PackagingWasteKg { get; init; }
    public double ProductWasteKg { get; init; }
    // Number of entries: none = nothing entered, not "0 kg of waste".
    public int PackagingEntries { get; init; }
    public int ProductEntries { get; init; }
    public int Machines { get; init; }
}

public sealed class PlantSnapshot
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public bool IsLive { get; init; }
    public string? ProductionError { get; init; }
    public KpiSummary? Total { get; init; }
    public double TrsTarget { get; init; }
    public List<MachineRow> Machines { get; init; } = new();
    public List<SectorRow> Sectors { get; init; } = new();
    public double PackagingWasteKg { get; init; }
    public double ProductWasteKg { get; init; }
    public int PackagingEntries { get; init; }
    public int ProductEntries { get; init; }
    public int DraftEntries { get; init; }

    public double? PackagingWastePercent
        => Total is { ProductionKg: > 0 } t ? PackagingWasteKg / (t.ProductionKg + PackagingWasteKg) * 100 : null;
}

// Assembles the figures of the dashboards from the production database
// (read only) and the waste entries, inside the actor's data scope.
public sealed class DashboardQueries(PlantData plant, WasteService waste, TimeProvider clock)
{
    // `permission`: the "voir" of the calling screen; its scope decides
    // which machines, sectors and postes are counted.
    public async Task<PlantSnapshot> LoadAsync(Actor actor, string permission, DateTime from, DateTime to, string? sector, int? shiftId)
    {
        var now = clock.GetLocalNow().DateTime;
        var live = now >= from && now < to;

        var entries = (await waste.ForDashboardAsync(actor, from, to.AddDays(-1)))
            .Where(e => sector == null || e.Sector == sector)
            .Where(e => shiftId == null || e.ShiftId == shiftId)
            .ToList();

        List<MachineRow> rows = new();
        KpiSummary? total = null;
        string? error = null;
        IReadOnlyList<PlantMachine> machines = Array.Empty<PlantMachine>();
        List<ProductionBucket> lots = new();
        try
        {
            machines = await plant.MachinesFor(actor, permission, sector);
            lots = await plant.LotsAsync(actor, permission, machines, from, to, shiftId);
            total = PlantData.Total(lots);
            foreach (var machine in machines)
            {
                var row = new MachineRow
                {
                    Figures = plant.Figures(machine, lots),
                    WasteKg = entries.Where(e => e.Kind == WasteKind.Emballage && e.MachineId == machine.Id).Sum(e => e.QuantityKg),
                };
                if (live)
                    row.State = await plant.StateAsync(machine.Id);
                rows.Add(row);
            }
        }
        catch (Exception ex) when (ex is not AccessDeniedException)
        {
            error = "Base de production injoignable : " + ex.Message;
        }

        var sectors = Core.Plant.Sectors.All
            .Where(s => (sector == null || s == sector) && actor.Allows(permission, s))
            .Select(s =>
            {
                var sectorMachines = machines.Where(m => m.Sector == s).ToList();
                var sectorLots = lots.Where(l => sectorMachines.Any(m => m.Id == l.MachineId)).ToList();
                return new SectorRow
                {
                    Sector = s,
                    Kpi = error == null && sectorMachines.Count > 0 ? PlantData.Total(sectorLots) : null,
                    TrsTarget = plant.AverageTrsTarget(sectorMachines),
                    Machines = sectorMachines.Count,
                    PackagingWasteKg = entries.Where(e => e.Kind == WasteKind.Emballage && e.Sector == s).Sum(e => e.QuantityKg),
                    ProductWasteKg = entries.Where(e => e.Kind == WasteKind.Produit && e.Sector == s).Sum(e => e.QuantityKg),
                    PackagingEntries = entries.Count(e => e.Kind == WasteKind.Emballage && e.Sector == s),
                    ProductEntries = entries.Count(e => e.Kind == WasteKind.Produit && e.Sector == s),
                };
            })
            .ToList();

        return new PlantSnapshot
        {
            From = from,
            To = to,
            IsLive = live,
            ProductionError = error,
            Total = total,
            TrsTarget = plant.AverageTrsTarget(machines),
            Machines = rows,
            Sectors = sectors,
            PackagingWasteKg = entries.Where(e => e.Kind == WasteKind.Emballage).Sum(e => e.QuantityKg),
            ProductWasteKg = entries.Where(e => e.Kind == WasteKind.Produit).Sum(e => e.QuantityKg),
            PackagingEntries = entries.Count(e => e.Kind == WasteKind.Emballage),
            ProductEntries = entries.Count(e => e.Kind == WasteKind.Produit),
            DraftEntries = entries.Count(e => e.Status != WasteStatus.Validee),
        };
    }
}
