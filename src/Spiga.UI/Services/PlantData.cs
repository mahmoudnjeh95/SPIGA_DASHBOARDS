using Spiga.Core.Plant;
using Spiga.Core.Security;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace Spiga.UI.Services;

// Figures of one machine over a period, computed exactly as the
// SPIGA Dashboards slides compute them (KpiCalculator).
public sealed record MachineFigures(PlantMachine Machine, KpiSummary Kpi, double TrsTarget, KpiSeverity TrsSeverity, KpiSeverity WeightSeverity)
{
    public bool HasProduction => Kpi.TotalBags > 0;
}

public sealed record MachineState(bool Connected, bool Running, DateTime? LastData, string? Product);

// Read-only access to the production database (Stiavelli "stats"):
// machines, team roster, KPIs. Also the plant referential the core
// services use. Shared by all sessions.
public sealed class PlantData(ProductionRepository repository, AppSettings settings, TeamScheduleService teams, TimeProvider clock)
    : IPlantReferential
{
    private IReadOnlyList<PlantMachine>? _machines;
    private DateTime _machinesLoadedAt;

    public AppSettings Settings => settings;

    private DateTime Now => clock.GetLocalNow().DateTime;

    public async Task<IReadOnlyList<PlantMachine>> GetMachinesAsync()
    {
        if (_machines != null && Now - _machinesLoadedAt < TimeSpan.FromMinutes(5))
            return _machines;
        var rows = await repository.GetMachinesAsync();
        _machines = rows.Where(m => m.IsActive)
            .OrderBy(m => Sectors.All.ToList().IndexOf(m.Sector) is var i && i < 0 ? 99 : i).ThenBy(m => m.MachineId)
            .Select(m => new PlantMachine(m.MachineId, m.MachineName, m.Sector))
            .ToList();
        _machinesLoadedAt = Now;
        return _machines;
    }

    public IReadOnlyList<string> Teams => teams.TeamNames;

    public string TeamOnShift(DateTime productionDate, int shiftId) => teams.GetTeamForShift(productionDate, shiftId);

    public (string Team, int ShiftId) CurrentShift()
    {
        var now = Now;
        var shift = Shifts.At(now);
        return (TeamOnShift(Shifts.ProductionDate(now), shift), shift);
    }

    public async Task<IReadOnlyList<PlantMachine>> MachinesFor(Actor actor, string? sector)
        => (await GetMachinesAsync())
            .Where(m => actor.Scope.AllowsMachine(m.Id, m.Sector) && (sector == null || m.Sector == sector))
            .ToList();

    // Lots of the given machines over [from, to), narrowed to a poste and to
    // the actor's team when their scope has one.
    public async Task<List<ProductionBucket>> LotsAsync(Actor actor, IReadOnlyCollection<PlantMachine> machines, DateTime from, DateTime to, int? shiftId)
    {
        var lots = await repository.GetLotsForMachinesAsync(machines.Select(m => m.Id).ToList(), from, to);
        return lots
            .Where(l => shiftId == null || l.ShiftId == shiftId)
            .Where(l => actor.Scope.AllowsTeam(teams.TeamOf(l)))
            .ToList();
    }

    public MachineFigures Figures(PlantMachine machine, IReadOnlyCollection<ProductionBucket> lots)
    {
        var mine = lots.Where(l => l.MachineId == machine.Id).ToList();
        var trsBand = settings.TrsBandFor(machine.Id);
        var weightBand = settings.WeightBandFor(machine.Id);
        var kpi = KpiCalculator.Summarize(mine, KpiCalculator.AvailabilityPercent(mine), trsBand, weightBand);
        return new MachineFigures(machine, kpi, settings.TrsTarget(machine.Id),
            trsBand.Severity(kpi.TrsPercent), KpiCalculator.WeightDeviationSeverity(kpi.WeightDeviationPercent, weightBand));
    }

    public static KpiSummary Total(IReadOnlyCollection<ProductionBucket> lots)
        => KpiCalculator.Summarize(lots, KpiCalculator.AvailabilityPercent(lots));

    public async Task<MachineState?> StateAsync(string machineId)
    {
        var status = await repository.GetLatestStatusAsync(machineId);
        return status == null
            ? null
            : new MachineState(status.IsConnected, status.IsConnected && status.BeltRunning, status.Timestamp,
                status.CurrentProduction?.ProductName);
    }

    public double AverageTrsTarget(IEnumerable<PlantMachine> machines)
    {
        var targets = machines.Select(m => settings.TrsTarget(m.Id)).ToList();
        return targets.Count == 0 ? AppSettings.DefaultTrsTarget : targets.Average();
    }

    public void ForgetMachines() => _machines = null;
}
