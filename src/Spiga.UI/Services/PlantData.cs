using System.Collections.Concurrent;
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

    // Lots read from stats, kept a few seconds: the overview, Mon poste and
    // Conditionnement ask for the same machines and period one after the
    // other (and several sessions at once); the query is the slow part.
    private static readonly TimeSpan LotsLifetime = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<string, (DateTime At, Task<List<ProductionBucket>> Lots)> _lots = new();

    public AppSettings Settings => settings;

    private DateTime Now => clock.GetLocalNow().DateTime;

    public async Task<IReadOnlyList<PlantMachine>> GetMachinesAsync()
    {
        if (_machines != null && Now - _machinesLoadedAt < TimeSpan.FromMinutes(5))
            return _machines;
        var rows = await repository.GetMachinesAsync();
        // The screens never show a machine id: the name set in Paramètres
        // (Nom affiché), the id only while no name is known.
        _names = rows.GroupBy(m => m.MachineId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => DisplayName(g.First()), StringComparer.OrdinalIgnoreCase);
        _machines = rows.Where(m => m.IsActive)
            .OrderBy(m => Sectors.All.ToList().IndexOf(m.Sector) is var i && i < 0 ? 99 : i).ThenBy(m => m.MachineId)
            .Select(m => new PlantMachine(m.MachineId, DisplayName(m), m.Sector))
            .ToList();
        _machinesLoadedAt = Now;
        return _machines;
    }

    private static string DisplayName(Machine m) => string.IsNullOrWhiteSpace(m.MachineName) ? m.MachineId : m.MachineName.Trim();

    private IReadOnlyDictionary<string, string> _names = new Dictionary<string, string>();

    // The name shown for a machine id (Paramètres > Machines > Nom affiché),
    // inactive machines included. Known once GetMachinesAsync has run.
    public string NameOf(string? machineId)
        => string.IsNullOrEmpty(machineId) ? "" : _names.TryGetValue(machineId, out var name) ? name : machineId;

    // A text recorded with machine ids (audit values, entry history), as the
    // screens show it: each id replaced by its name.
    public string WithNames(string? text) => MachineNames.Replace(text, _names);

    // For the screens that show machines without listing them: the names
    // loaded, or the ids while the production database is down.
    public async Task LoadNamesAsync()
    {
        try
        {
            await GetMachinesAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Log("Noms des machines illisibles, identifiants affichés : " + ex.Message);
        }
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
        var lots = await SharedLotsAsync(machines.Select(m => m.Id).OrderBy(id => id, StringComparer.Ordinal).ToList(), from, to);
        return lots
            .Where(l => shiftId == null || l.ShiftId == shiftId)
            .Where(l => sectorOf.TryGetValue(l.MachineId, out var sector) && actor.Allows(permission, sector, l.MachineId, teams.TeamOf(l)))
            .ToList();
    }

    private async Task<List<ProductionBucket>> SharedLotsAsync(List<string> machineIds, DateTime from, DateTime to)
    {
        var now = Now;
        var key = $"{string.Join(",", machineIds)}|{from:O}|{to:O}";
        if (_lots.Count > 64)
            foreach (var old in _lots.Where(e => now - e.Value.At >= LotsLifetime).ToList())
                _lots.TryRemove(old);
        // Started on the thread pool, not on the asking session's Blazor
        // context: other sessions await the same task and must not wait
        // behind that session's work (or for a session that has closed).
        Task<List<ProductionBucket>> Query() => Task.Run(() => repository.GetLotsForMachinesAsync(machineIds, from, to));
        var entry = _lots.AddOrUpdate(key,
            _ => (now, Query()),
            (_, cached) => now - cached.At < LotsLifetime && !cached.Lots.IsFaulted ? cached : (now, Query()));
        try
        {
            return await entry.Lots;
        }
        catch
        {
            _lots.TryRemove(new KeyValuePair<string, (DateTime, Task<List<ProductionBucket>>)>(key, entry));
            throw;
        }
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

// Machine ids inside a recorded text ("Machine : PC01 · Tâche : …") replaced
// by the names the screens show.
public static class MachineNames
{
    public static string Replace(string? text, IReadOnlyDictionary<string, string> names)
    {
        if (string.IsNullOrEmpty(text) || names.Count == 0)
            return text ?? "";
        var pattern = @"\b(" + string.Join("|", names.Keys.OrderByDescending(k => k.Length).Select(System.Text.RegularExpressions.Regex.Escape)) + @")\b";
        return System.Text.RegularExpressions.Regex.Replace(text, pattern, m =>
        {
            if (!names.TryGetValue(m.Value, out var name) || name.Length == 0)
                return m.Value;
            // Already written under its name ("Controle CC01"): left as is.
            var at = name.IndexOf(m.Value, StringComparison.OrdinalIgnoreCase);
            var start = m.Index - at;
            return at >= 0 && start >= 0 && start + name.Length <= text.Length
                   && string.Compare(text, start, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
                ? m.Value
                : name;
        }, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
