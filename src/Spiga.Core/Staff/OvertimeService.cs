using Spiga.Core.Audit;
using Spiga.Core.Plant;
using Spiga.Core.Security;

namespace Spiga.Core.Staff;

// One person's overtime over a month (the chef de production's screen).
public sealed record OvertimeRow(
    Employee Employee, EmployeePosting? Posting, int MonthMinutes, IReadOnlyDictionary<DateTime, int> Days, OvertimeState State);

// The rules of the heures supplémentaires, kept by the chef de production
// (right « Gérer les heures supplémentaires »): who may not do any, the
// monthly limit per person, and the statistics to decide on. The pointage
// sheet asks it what each person may still do (OvertimeBook).
public sealed class OvertimeService(
    IOvertimeStore store, IAttendanceStore sheets, IStaffStore staff, ISettingsStore settings, IPlantReferential plant,
    IAuditLog audit, TimeProvider clock, ClientInfo client)
{
    public const int MaxLimitHours = 200;
    private const int MaxReason = 200;

    private DateTime Now => clock.GetLocalNow().DateTime;

    public bool CanManage(Actor actor) => actor.Can(Perm.HeuresSup);

    public async Task<OvertimeRule> RuleAsync() => await settings.GetAsync<OvertimeRule>(SettingKeys.OvertimeRule) ?? new OvertimeRule();

    // Every sheet of the month of `date` and the decisions: what the checks
    // of a pointage sheet need.
    public async Task<OvertimeBook> BookAsync(DateTime date)
    {
        var month = Overtime.MonthStart(date);
        var rule = await RuleAsync();
        var list = await sheets.ListAsync(month, Overtime.MonthEnd(month));
        var decisions = await store.ListAsync();
        return new OvertimeBook(month, list, decisions, rule.MonthlyLimitHours * 60, plant);
    }

    // ---------- the chef de production's screen ----------

    public async Task<(OvertimeBook Book, IReadOnlyList<OvertimeRow> Rows)> MonthAsync(Actor actor, DateTime month)
    {
        actor.Demand(Perm.HeuresSup);
        month = Overtime.MonthStart(month);
        var book = await BookAsync(month);
        var byDay = book.ByDay();
        // Status as of today in the current month, else at the month's end.
        var today = Shifts.ProductionDate(Now);
        var at = today >= month && today <= Overtime.MonthEnd(month) ? today : Overtime.MonthEnd(month);
        var rows = new List<OvertimeRow>();
        foreach (var e in await staff.ListEmployeesAsync())
        {
            var posting = e.PostingAt(at) ?? e.Current(at);
            if (posting == null || !actor.AllowsTeam(Perm.HeuresSup, posting.Team))
                continue;
            var days = byDay.GetValueOrDefault(e.Id) ?? new Dictionary<DateTime, int>();
            var state = book.State(e.Id, at);
            if (!e.IsActive && days.Count == 0 && state.Ban == null)
                continue;
            rows.Add(new OvertimeRow(e, posting, days.Values.Sum(), days, state));
        }
        return (book, rows
            .OrderByDescending(r => r.MonthMinutes)
            .ThenBy(r => r.Employee.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList());
    }

    public async Task<IReadOnlyList<OvertimeDecision>> DecisionsAsync(Actor actor)
    {
        actor.Demand(Perm.HeuresSup);
        return (await store.ListAsync()).OrderByDescending(d => d.CreatedAt).ToList();
    }

    public async Task SetLimitAsync(Actor actor, int monthlyLimitHours)
    {
        actor.Demand(Perm.HeuresSup);
        if (monthlyLimitHours < 0 || monthlyLimitHours > MaxLimitHours)
            throw new RuleViolationException($"La limite se donne en heures par mois, de 0 (sans limite) à {MaxLimitHours}.");
        var before = await RuleAsync();
        if (before.MonthlyLimitHours == monthlyLimitHours)
            return;
        await settings.SetAsync(SettingKeys.OvertimeRule, new OvertimeRule { MonthlyLimitHours = monthlyLimitHours }, actor.Login);
        await Write(actor, AuditActions.OvertimeRuleChanged, "Limite mensuelle d'heures sup.", "",
            Describe(before.MonthlyLimitHours), Describe(monthlyLimitHours));
    }

    private static string Describe(int hours) => hours == 0 ? "sans limite" : $"{hours} h par mois et par personne";

    // Forbids overtime to a person from `from` on (until `to`, or until lifted).
    public async Task<OvertimeDecision> BanAsync(Actor actor, int employeeId, string reason, DateTime? from = null, DateTime? to = null)
    {
        actor.Demand(Perm.HeuresSup);
        var e = await EmployeeAsync(actor, employeeId);
        var start = (from ?? Shifts.ProductionDate(Now)).Date;
        if (to is { } end && end.Date < start)
            throw new RuleViolationException("La fin de l'interdiction doit être après son début.");
        reason = Reason(reason);
        var all = await store.ListAsync();
        if (all.Any(d => d.EmployeeId == e.Id && d.Kind == OvertimeDecisionKind.Interdiction && d.IsActive
                         && (d.ToDate == null || d.ToDate.Value.Date >= start) && (to == null || d.FromDate.Date <= to.Value.Date)))
            throw new RuleViolationException($"{e.FullName} est déjà interdit d'heures supplémentaires sur ces dates.");
        var decision = new OvertimeDecision
        {
            EmployeeId = e.Id, Kind = OvertimeDecisionKind.Interdiction, FromDate = start, ToDate = to?.Date, Reason = reason,
            CreatedBy = actor.Login, CreatedAt = Now,
        };
        decision.Id = await store.InsertAsync(decision);
        await Write(actor, AuditActions.OvertimeBanned, e.FullName, reason, null,
            $"Interdit d'heures sup. du {start:dd/MM/yyyy}" + (to is { } t ? $" au {t:dd/MM/yyyy}" : ""));
        return decision;
    }

    // Lets a person who reached the limit do more, for one month.
    public async Task<OvertimeDecision> AllowOverLimitAsync(Actor actor, int employeeId, DateTime month, string reason)
    {
        actor.Demand(Perm.HeuresSup);
        var e = await EmployeeAsync(actor, employeeId);
        reason = Reason(reason);
        if (reason.Length == 0)
            throw new RuleViolationException("Une dérogation se donne avec un motif : indiquez-le.");
        month = Overtime.MonthStart(month);
        if (month < Overtime.MonthStart(Shifts.ProductionDate(Now)))
            throw new RuleViolationException("Une dérogation se donne pour le mois en cours ou un mois à venir.");
        var all = await store.ListAsync();
        if (all.Any(d => d.EmployeeId == e.Id && d.Kind == OvertimeDecisionKind.Derogation && d.Covers(month)))
            throw new RuleViolationException($"{e.FullName} a déjà une dérogation pour {month:MM/yyyy}.");
        var decision = new OvertimeDecision
        {
            EmployeeId = e.Id, Kind = OvertimeDecisionKind.Derogation, FromDate = month, ToDate = Overtime.MonthEnd(month), Reason = reason,
            CreatedBy = actor.Login, CreatedAt = Now,
        };
        decision.Id = await store.InsertAsync(decision);
        await Write(actor, AuditActions.OvertimeDerogation, e.FullName, reason, null, $"Peut dépasser la limite d'heures sup. en {month:MM/yyyy}");
        return decision;
    }

    // Ends a ban or a dérogation.
    public async Task LiftAsync(Actor actor, long decisionId)
    {
        actor.Demand(Perm.HeuresSup);
        var d = (await store.ListAsync()).FirstOrDefault(x => x.Id == decisionId)
                ?? throw new RuleViolationException("Décision introuvable (supprimée entre-temps ?).");
        var e = await EmployeeAsync(actor, d.EmployeeId);
        if (!d.IsActive)
            return;
        if (!await store.LiftAsync(d.Id, actor.Login, Now))
            throw new RuleViolationException("Cette décision a été levée entre-temps.");
        await Write(actor, d.Kind == OvertimeDecisionKind.Interdiction ? AuditActions.OvertimeBanLifted : AuditActions.OvertimeDerogationLifted,
            e.FullName, "", d.Kind == OvertimeDecisionKind.Interdiction ? "Interdit d'heures sup." : "Dérogation à la limite", "Levée");
    }

    private async Task<Employee> EmployeeAsync(Actor actor, int employeeId)
    {
        var e = await staff.GetEmployeeAsync(employeeId) ?? throw new RuleViolationException("Employé introuvable.");
        var posting = e.Current(Shifts.ProductionDate(Now));
        if (posting != null && !actor.AllowsTeam(Perm.HeuresSup, posting.Team))
            throw new AccessDeniedException($"{e.FullName} est de {posting.Team} : hors de votre périmètre.");
        return e;
    }

    private static string Reason(string? reason)
    {
        var r = reason?.Trim() ?? "";
        if (r.Length > MaxReason)
            throw new RuleViolationException($"Le motif est limité à {MaxReason} caractères.");
        return r;
    }

    private Task Write(Actor actor, string action, string target, string details, string? before, string? after)
        => audit.WriteAsync(new AuditEntry
        {
            At = Now, UserLogin = actor.Login, Module = Modules.Pointage, Action = action, Target = target, Details = details,
            OldValue = before, NewValue = after, Workstation = client.Workstation,
        });
}
