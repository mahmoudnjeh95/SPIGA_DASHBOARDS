using Spiga.Core.Plant;
using Spiga.Core.Security;
using Spiga.Core.Waste;
using SpigaDashboards.App.Models;

namespace Spiga.UI.Services;

// One machine of the operator's poste, as it is now.
public sealed record MachineLive(
    PlantMachine Machine, MachineState? State, OngoingStop? Stop, MachineFigures? Figures, double WasteKg, int WasteEntries)
{
    public bool Running => State is { Connected: true, Running: true } && Stop == null;
    public bool Connected => State?.Connected == true;
    public double? WasteShare => Figures is { } f ? WastePercent.Of(WasteKg, f.Kpi.ProductionKg) : null;
}

public sealed record PosteView(
    DateTime Date, int ShiftId, string Team, DateTime Start, DateTime End, DateTime LoadedAt,
    IReadOnlyList<MachineLive> Machines, string? ProductionError)
{
    // Share of the poste already worked, 0..1.
    public double Elapsed => Math.Clamp((LoadedAt - Start).TotalMinutes / (End - Start).TotalMinutes, 0, 1);
}

// The operator's home: his team's current poste (or the one just ended), and
// each machine he enters waste for, live from the production database, with
// the packaging waste entered on it during the poste.
public sealed class PosteBoard(PlantData plant, WasteService waste, TimeProvider clock)
{
    public async Task<PosteView> LoadAsync(Actor actor)
    {
        var now = clock.GetLocalNow().DateTime;
        var (date, shift) = waste.CurrentPoste(actor) ?? (Shifts.ProductionDate(now), Shifts.At(now));
        var start = Shifts.Start(date, shift);
        var end = start.AddHours(8);
        var machines = await waste.MachinesFor(actor);
        var entries = (await waste.ListAsync(actor, WasteKind.Emballage, date, shift)).Where(e => e.Counts).ToList();

        var rows = new List<MachineLive>();
        string? error = null;
        try
        {
            var lots = await plant.LotsAsync(actor, Perm.DechetsSaisir, machines, start, end, shift);
            var stops = now < end ? await plant.OngoingStopsAsync() : new Dictionary<string, OngoingStop>();
            foreach (var m in machines)
            {
                var state = now < end ? await plant.StateAsync(m.Id) : null;
                rows.Add(new MachineLive(m, state, stops.GetValueOrDefault(m.Id), plant.Figures(m, lots),
                    entries.Where(e => e.MachineId == m.Id).Sum(e => e.QuantityKg), entries.Count(e => e.MachineId == m.Id)));
            }
        }
        catch (Exception ex) when (ex is not AccessDeniedException)
        {
            error = "Base de production injoignable : état des machines indisponible. " + ex.Message;
            rows = machines.Select(m => new MachineLive(m, null, null, null,
                entries.Where(e => e.MachineId == m.Id).Sum(e => e.QuantityKg), entries.Count(e => e.MachineId == m.Id))).ToList();
        }
        return new PosteView(date, shift, plant.TeamOnShift(date, shift), start, end, now, rows, error);
    }
}
