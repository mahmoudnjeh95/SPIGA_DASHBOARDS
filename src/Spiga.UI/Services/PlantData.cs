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

    public string TeamOf(ProductionBucket lot) => teams.TeamOf(lot);

    public (string Team, int ShiftId) CurrentShift()
    {
        var now = Now;
        var shift = Shifts.At(now);
        return (TeamOnShift(Shifts.ProductionDate(now), shift), shift);
    }

    // Whether this poste is worked by the actor's team (always true for an
    // account covering several teams, or when the roster is unavailable).
    public bool IsOwnShift(Actor actor, DateTime productionDate, int shiftId)
    {
        if (actor.Team == null)
            return true;
        var team = TeamOnShift(productionDate, shiftId);
        return string.IsNullOrEmpty(team) || actor.IsOwnTeam(team);
    }

    // Whether the actor's team works this poste on at least one production
    // day of [from, to) (the rotation moves the teams from one poste to
    // another). The last day is the one before `to` (a period ends at 06:00
    // the morning after its last day, or at midnight for calendar days).
    public bool WorksShift(Actor actor, DateTime from, DateTime to, int shiftId)
    {
        if (actor.Team == null)
            return true;
        var last = to.AddDays(-1).Date;
        for (var d = from.Date; d <= last && d < from.Date.AddDays(28); d = d.AddDays(1))
            if (IsOwnShift(actor, d, shiftId))
                return true;
        return false;
    }

    // "Le poste Nuit du 18/11 est tenu par Equipe A : vous ne voyez que les postes de Equipe B."
    public string ForeignShiftMessage(Actor actor, DateTime productionDate, int shiftId)
    {
        var team = TeamOnShift(productionDate, shiftId);
        return $"Ce poste ne concerne pas votre équipe : le poste {Shifts.Label(shiftId)} du {productionDate:dd/MM} est tenu par {team}. "
               + $"Vous ne voyez et ne saisissez que les postes de {actor.Team}.";
    }

    // The machines where the actor holds `permission` (any team).
    public async Task<IReadOnlyList<PlantMachine>> MachinesFor(Actor actor, string permission, string? sector)
        => (await GetMachinesAsync())
            .Where(m => actor.Allows(permission, m.Sector, m.Id) && (sector == null || m.Sector == sector))
            .ToList();

    // Lots of the given machines over [from, to), narrowed to a poste and to
    // what the actor's assignments cover (a team-scoped role sees its team's
    // postes only).
    public async Task<List<ProductionBucket>> LotsAsync(
        Actor actor, string permission, IReadOnlyCollection<PlantMachine> machines, DateTime from, DateTime to, int? shiftId)
    {
        var sectorOf = machines.ToDictionary(m => m.Id, m => m.Sector);
        var lots = await repository.GetLotsForMachinesAsync(machines.Select(m => m.Id).ToList(), from, to);
        return lots
            .Where(l => shiftId == null || l.ShiftId == shiftId)
            .Where(l => sectorOf.TryGetValue(l.MachineId, out var sector) && actor.Allows(permission, sector, l.MachineId, teams.TeamOf(l)))
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

    // Lines stopped right now, each with the start of its stop (same rules
    // and cache as the dashboard's stop banner).
    public Task<IReadOnlyDictionary<string, OngoingStop>> OngoingStopsAsync()
        => OngoingStopService.GetAsync(repository, settings);

    public double AverageTrsTarget(IEnumerable<PlantMachine> machines)
    {
        var targets = machines.Select(m => settings.TrsTarget(m.Id)).ToList();
        return targets.Count == 0 ? AppSettings.DefaultTrsTarget : targets.Average();
    }

    public void ForgetMachines() => _machines = null;
}
