using Spiga.Core.Security;
using Spiga.Data;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace Spiga.UI.Services;

// Maintenance > Arrêts (web and desktop): the production stops per poste and
// team over a range of days (StopDetector), with the cause given in
// Qualification des arrêts, totals per team and the printable sheet each
// team fills in (StopSheetPdf). Same list as the former WPF page
// (StopsViewModel), read through repositories limited to the account's
// machines for the page's right; a team-bound account sees its team only.
public sealed class StopListService(
    IDbConnectionFactory production, AppDbOptions appDb, AppSettings settings, TeamScheduleService teams, TimeProvider clock)
{
    public static readonly string Permission = NativePages.Permission(NativePage.Stops);

    private DateTime Now => clock.GetLocalNow().DateTime;

    // The production day now (the night poste belongs to the day it started).
    public DateTime Today => Spiga.Core.Plant.Shifts.ProductionDate(Now);

    // A search may cover at most this many days (one query per search).
    public const int MaxDays = 92;

    public static string? TeamFor(Actor actor, string? chosen) => actor.Team ?? chosen;

    // Every team of the roster, and the stops outside it.
    public IReadOnlyList<string> Teams
        => teams.TeamNames.Contains(TeamScheduleService.NoTeam) ? teams.TeamNames : teams.TeamNames.Append(TeamScheduleService.NoTeam).ToList();

    // "Seuil : 5 min · pauses déduites : 10:00 (20 min), …"
    public string SettingsText
        => $"Seuil : {settings.StopThresholdMinutes} min · pauses planifiées déduites : {BreakSchedule.Describe(settings.PlannedBreaks)} (modifiables dans Paramètres)";

    // The account's active lines for this page, couscous first.
    public async Task<List<Machine>> MachinesAsync(Actor actor)
    {
        var (repository, _) = ScopedData.For(actor, Permission, production, appDb, settings);
        return (await repository.GetMachinesAsync())
            .Where(m => m.IsActive)
            .OrderBy(m => Spiga.Core.Plant.Sectors.All.ToList().IndexOf(m.Sector) is var i && i < 0 ? 99 : i).ThenBy(m => m.MachineId)
            .ToList();
    }

    public async Task<StopList> SearchAsync(Actor actor, StopsFilter filter)
    {
        var (from, to) = StopMatching.PostesOf(filter.From, filter.To);
        if ((to - from).TotalDays > MaxDays + 1)
            throw new RuleViolationException($"Choisissez au plus {MaxDays} jours.");
        var (repository, planning) = ScopedData.For(actor, Permission, production, appDb, settings);
        var all = await MachinesAsync(actor);
        var machines = filter.MachineId == null ? all : all.Where(m => m.MachineId == filter.MachineId).ToList();
        var team = TeamFor(actor, filter.Team);
        if (machines.Count == 0)
            return new StopList(new(), new(), from, to);

        var names = machines.ToDictionary(m => m.MachineId, m => m.MachineName);
        // Production database down: the stops given a cause still show.
        List<ProductionStop> detected;
        string? error = null;
        try
        {
            var segments = await repository.GetLineSegmentsAsync(names.Keys.ToList(), from, to);
            detected = StopDetector.Detect(segments, from, to, settings, teams, names);
        }
        catch (Exception ex)
        {
            AppLogger.Log("Arrêts : base de production illisible : " + ex.Message);
            detected = new();
            error = "Base de production injoignable : seuls les arrêts saisis dans Qualification des arrêts sont listés. " + ex.Message;
        }
        var stops = StopMatching.WithQualifications(detected, await QualificationsAsync(planning, names, from, to), names, from, to)
            .Where(s => team == null || s.Team == team)
            .ToList();
        return Narrow(new StopList(stops, TeamStopTotal.Of(stops), from, to, error), actor, filter);
    }

    // The poste and team of the filter applied to a list already read (the
    // page changes them without reading the production database again).
    public static StopList Narrow(StopList list, Actor actor, StopsFilter filter)
    {
        var team = TeamFor(actor, filter.Team);
        var stops = list.Stops
            .Where(s => filter.ShiftId == null || s.ShiftId == filter.ShiftId)
            .Where(s => team == null || s.Team == team)
            .ToList();
        return list with { Stops = stops, Teams = TeamStopTotal.Of(stops) };
    }

    // The sheet of the stops shown, one page per poste and team.
    public byte[] Sheet(IReadOnlyList<ProductionStop> stops) => StopSheetPdf.Build(stops, settings, Now);

    // The causes given to these machines' stops; none (and a log line) if
    // they can't be read, the stops still show.
    private static async Task<List<StopQualification>> QualificationsAsync(PlanningRepository planning, IReadOnlyDictionary<string, string> names,
        DateTime from, DateTime to)
    {
        try
        {
            return (await planning.GetQualificationsAsync(from.AddHours(-1), to.AddHours(1)))
                .Where(q => names.ContainsKey(q.MachineId))
                .ToList();
        }
        catch (Exception ex)
        {
            AppLogger.Log("Arrêts : causes des arrêts illisibles : " + ex.Message);
            return new();
        }
    }
}

// Days (both included, any order), one line or all, one poste (1-3) or all, one team or all.
public sealed record StopsFilter(DateTime From, DateTime To, string? MachineId = null, int? ShiftId = null, string? Team = null);

public sealed record StopList(List<ProductionStop> Stops, List<TeamStopTotal> Teams, DateTime From, DateTime To, string? ProductionError = null)
{
    public double NetMinutes => Stops.Sum(s => s.NetMinutes);
}
