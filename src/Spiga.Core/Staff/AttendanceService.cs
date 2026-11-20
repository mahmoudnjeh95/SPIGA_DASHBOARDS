using Spiga.Core.Audit;
using Spiga.Core.Plant;
using Spiga.Core.Security;

namespace Spiga.Core.Staff;

// The pointage sheet of the packaging service: one per team and poste,
// filled by the chef de conditionnement of the team.
//
// The sheet opens with the team's members, each with the hours of the poste;
// the chef confirms each row as it is, changes the hours, or gives an
// absence code (A, M, AT, C). He adds renforts from other teams and moves a
// present member onto the post of an absent one (temporary assignment).
// Nothing invalid is saved: hours on 5-minute steps inside the poste's
// window, no one twice at the same time, an absence always with its code.
//
// Without "Pointer et corriger avant hier" only the postes of yesterday and
// today can be seen and changed. A validated sheet is changed with
// "Corriger un pointage validé" and a reason. Every save writes a revision
// with what changed, and an audit entry.
public sealed class AttendanceService(
    IAttendanceStore store, IStaffStore staff, IPlantReferential plant, IAuditLog audit, TimeProvider clock, ClientInfo client)
{
    public const int StepMinutes = 5;

    // An entry from 2 h before the poste, an exit up to 4 h after its end
    // (overtime).
    public const int EarlyHours = 2;
    public const int OvertimeHours = 4;

    // A poste ended more than this long ago without a validated sheet is
    // flagged as not pointed.
    public const int ReminderMinutes = 60;

    // A sheet is validated at the end of its poste: from this long before
    // it ends (the exits are known). Before, it is saved as a draft.
    public const int ValidateBeforeEndMinutes = 60;

    private const int MaxNote = 300;

    private DateTime Now => clock.GetLocalNow().DateTime;

    public DateTime Today => Shifts.ProductionDate(Now);

    public static DateTime Earliest(AttendanceSheet s) => s.Start.AddHours(-EarlyHours);

    public static DateTime Latest(AttendanceSheet s) => s.End.AddHours(OvertimeHours);

    // ---------- what the actor may open ----------

    public bool CanSeeOld(Actor actor) => actor.Can(Perm.PointageAnciens);

    // Oldest production date the actor may see and change.
    public DateTime FirstDate(Actor actor) => CanSeeOld(actor) ? new DateTime(2020, 1, 1) : Today.AddDays(-1);

    public bool CanEnter(Actor actor, AttendanceSheet sheet)
        => sheet.Status == SheetStatus.Brouillon && actor.AllowsTeam(Perm.PointageSaisir, sheet.Team) && InWindow(actor, sheet.ProductionDate);

    public bool CanCorrect(Actor actor, AttendanceSheet sheet)
        => sheet.Status == SheetStatus.Validee && actor.AllowsTeam(Perm.PointageCorriger, sheet.Team) && InWindow(actor, sheet.ProductionDate);

    public DateTime ValidationOpensAt(AttendanceSheet sheet) => sheet.End.AddMinutes(-ValidateBeforeEndMinutes);

    public bool CanValidateNow(AttendanceSheet sheet) => Now >= ValidationOpensAt(sheet);

    // Days the "postes non pointés" reminder looks back over.
    public DateTime ReminderFrom(Actor actor) => Today.AddDays(CanSeeOld(actor) ? -6 : -1);

    private bool InWindow(Actor actor, DateTime date) => date.Date >= FirstDate(actor) && date.Date <= Today;

    // The postes of that day the actor may open: started, worked by a team
    // the actor covers.
    public IReadOnlyList<(int ShiftId, string Team)> PostesOn(Actor actor, DateTime date)
    {
        if (!InWindow(actor, date))
            return Array.Empty<(int, string)>();
        return Shifts.All
            .Where(s => Shifts.Start(date, s) <= Now)
            .Select(s => (s, plant.TeamOnShift(date, s)))
            .Where(p => !string.IsNullOrEmpty(p.Item2) && actor.AllowsTeam(Perm.PointageVoir, p.Item2))
            .ToList();
    }

    // The poste the page opens on: the actor's poste in progress, else the
    // last one of the two days.
    public (DateTime Date, int ShiftId)? DefaultPoste(Actor actor)
    {
        var date = Today;
        var shift = Shifts.At(Now);
        for (var i = 0; i < 6; i++)
        {
            if (date < FirstDate(actor))
                break;
            var team = plant.TeamOnShift(date, shift);
            if (!string.IsNullOrEmpty(team) && actor.AllowsTeam(Perm.PointageVoir, team))
                return (date, shift);
            (date, shift) = shift switch { 1 => (date.AddDays(-1), 3), 2 => (date, 1), _ => (date, 2) };
        }
        return null;
    }

    // ---------- reading ----------

    // The stored sheet, or a new one prefilled with the team (not saved
    // until the chef saves it). A draft gets the members who joined the team
    // since it was saved.
    public async Task<AttendanceSheet> OpenAsync(Actor actor, DateTime date, int shiftId)
    {
        actor.Demand(Perm.PointageVoir);
        date = date.Date;
        if (!Shifts.All.Contains(shiftId))
            throw new RuleViolationException("Choisissez le poste.");
        if (date > Today || Shifts.Start(date, shiftId) > Now)
            throw new RuleViolationException("Ce poste n'a pas encore commencé.");
        if (!InWindow(actor, date))
            throw new AccessDeniedException("Vous ne voyez que les postes d'hier et d'aujourd'hui (droit « Pointer et corriger avant hier » pour les autres).");
        var team = plant.TeamOnShift(date, shiftId);
        if (string.IsNullOrEmpty(team))
            throw new RuleViolationException("Aucune équipe n'est prévue sur ce poste (roulement des équipes).");
        if (!actor.AllowsTeam(Perm.PointageVoir, team))
            throw new AccessDeniedException($"Le poste {Shifts.Label(shiftId)} du {date:dd/MM} est tenu par {team} : vous ne voyez que les postes de {actor.Team}.");

        var sheet = await store.FindAsync(date, shiftId, team);
        var employees = await staff.ListEmployeesAsync();
        var elsewhere = await ElsewhereAsync(date, shiftId, team, sheet?.Id ?? 0);
        if (sheet == null)
        {
            sheet = new AttendanceSheet { ProductionDate = date, ShiftId = shiftId, Team = team, Status = SheetStatus.Brouillon };
            foreach (var e in Members(employees, date, team))
                sheet.Lines.Add(NewLine(sheet, e, e.PostingAt(date)!, renfort: false, elsewhere));
        }
        else if (sheet.Status == SheetStatus.Brouillon)
        {
            foreach (var e in Members(employees, date, team).Where(e => sheet.Lines.All(l => l.EmployeeId != e.Id)))
                sheet.Lines.Add(NewLine(sheet, e, e.PostingAt(date)!, renfort: false, elsewhere));
        }
        sheet.Lines = Order(sheet.Lines);
        return sheet;
    }

    public static List<AttendanceLine> Order(IEnumerable<AttendanceLine> lines)
        => lines.OrderBy(l => l.IsRenfort)
            .ThenByDescending(l => StaffFunctions.Rank(l.Function))
            .ThenBy(l => l.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // Employees of other teams (or of this team, removed by mistake) the
    // chef can add to the sheet.
    public async Task<IReadOnlyList<(Employee Employee, EmployeePosting Posting)>> RenfortCandidatesAsync(Actor actor, AttendanceSheet sheet)
    {
        actor.Demand(Perm.PointageVoir);
        return (await staff.ListEmployeesAsync())
            .Where(e => e.IsActive && sheet.Lines.All(l => l.EmployeeId != e.Id))
            .Select(e => (Employee: e, Posting: e.PostingAt(sheet.ProductionDate)))
            .Where(x => x.Posting != null)
            .Select(x => (x.Employee, x.Posting!))
            .OrderBy(x => x.Item2.Team).ThenBy(x => x.Employee.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<AttendanceLine> RenfortLineAsync(AttendanceSheet sheet, int employeeId)
    {
        var e = await staff.GetEmployeeAsync(employeeId) ?? throw new RuleViolationException("Employé introuvable.");
        var posting = e.PostingAt(sheet.ProductionDate) ?? throw new RuleViolationException($"{e.FullName} n'est pas encore employé à cette date.");
        var line = NewLine(sheet, e, posting, renfort: !string.Equals(posting.Team, sheet.Team, StringComparison.OrdinalIgnoreCase), new());
        return line;
    }

    public async Task<IReadOnlyList<AttendanceRevision>> HistoryAsync(Actor actor, AttendanceSheet sheet)
    {
        actor.Demand(Perm.PointageVoir);
        if (!actor.AllowsTeam(Perm.PointageVoir, sheet.Team))
            throw new AccessDeniedException("Cette feuille est hors de votre périmètre.");
        return sheet.IsNew ? Array.Empty<AttendanceRevision>() : await store.ListRevisionsAsync(sheet.Id);
    }

    // The members of `team` pointed as renfort in another team's sheet
    // between these dates (the home chef sees where his people worked).
    public async Task<IReadOnlyList<(AttendanceSheet Sheet, AttendanceLine Line)>> AwayAsync(Actor actor, string team, DateTime fromDate, DateTime toDate)
    {
        actor.Demand(Perm.PointageVoir);
        if (!actor.AllowsTeam(Perm.PointageVoir, team))
            return Array.Empty<(AttendanceSheet, AttendanceLine)>();
        return (await store.ListAsync(fromDate, toDate))
            .Where(s => !string.Equals(s.Team, team, StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => s.Lines.Where(l => l.IsRenfort && string.Equals(l.HomeTeam, team, StringComparison.OrdinalIgnoreCase)
                                                && l.Status == LineStatus.Present).Select(l => (s, l)))
            .OrderByDescending(x => x.s.ProductionDate).ThenByDescending(x => x.s.ShiftId)
            .ToList();
    }

    // Postes ended more than an hour ago, in the actor's teams, without a
    // validated sheet: from `fromDate` (at the earliest the first date the
    // actor may see) to today.
    public async Task<IReadOnlyList<(DateTime Date, int ShiftId, string Team, SheetStatus? Status)>> MissingAsync(Actor actor, DateTime fromDate)
    {
        if (!actor.Can(Perm.PointageVoir))
            return Array.Empty<(DateTime, int, string, SheetStatus?)>();
        var first = fromDate.Date < FirstDate(actor) ? FirstDate(actor) : fromDate.Date;
        var sheets = await store.ListAsync(first, Today);
        // Not before the pointage was in use: the first sheet ever entered.
        if ((await store.ListAsync(new DateTime(2020, 1, 1), first.AddDays(-1))).Count == 0)
        {
            if (sheets.Count == 0)
                return Array.Empty<(DateTime, int, string, SheetStatus?)>();
            first = sheets.Min(x => x.ProductionDate.Date);
        }
        var list = new List<(DateTime, int, string, SheetStatus?)>();
        for (var d = first; d <= Today; d = d.AddDays(1))
            foreach (var s in Shifts.All)
            {
                if (Shifts.Start(d, s).AddHours(8).AddMinutes(ReminderMinutes) > Now)
                    continue;
                var team = plant.TeamOnShift(d, s);
                if (string.IsNullOrEmpty(team) || !actor.AllowsTeam(Perm.PointageVoir, team))
                    continue;
                var sheet = sheets.FirstOrDefault(x => x.ProductionDate.Date == d && x.ShiftId == s && x.Team == team);
                if (sheet?.Status != SheetStatus.Validee)
                    list.Add((d, s, team, sheet?.Status));
            }
        return list.OrderByDescending(x => x.Item1).ThenByDescending(x => x.Item2).ToList();
    }

    // Every sheet, drafts included, of the teams where the actor holds
    // `permission` (the presence calendar).
    public async Task<IReadOnlyList<AttendanceSheet>> SheetsAsync(Actor actor, string permission, DateTime fromDate, DateTime toDate)
    {
        actor.Demand(permission);
        return (await store.ListAsync(fromDate.Date, toDate.Date))
            .Where(s => actor.AllowsTeam(permission, s.Team))
            .OrderBy(s => s.ProductionDate).ThenBy(s => s.ShiftId)
            .ToList();
    }

    // Validated sheets of the teams where the actor holds `permission`
    // (reports: absences, polyvalence, payroll export).
    public async Task<IReadOnlyList<AttendanceSheet>> ValidatedAsync(Actor actor, string permission, DateTime fromDate, DateTime toDate)
    {
        actor.Demand(permission);
        return (await store.ListAsync(fromDate.Date, toDate.Date))
            .Where(s => s.Status == SheetStatus.Validee && actor.AllowsTeam(permission, s.Team))
            .OrderBy(s => s.ProductionDate).ThenBy(s => s.ShiftId)
            .ToList();
    }

    // The paper sheet of a day (the three postes side by side, as the
    // printed form): what was pointed when the actor may see it, else the
    // teams' members with blank hours (a sheet printed in advance, the
    // fallback when a PC is down).
    public async Task<IReadOnlyList<(int ShiftId, string Team, AttendanceSheet Sheet, bool Filled)>> PaperAsync(Actor actor, DateTime date)
    {
        actor.Demand(Perm.PointageVoir);
        date = date.Date;
        var employees = await staff.ListEmployeesAsync();
        var list = new List<(int, string, AttendanceSheet, bool)>();
        foreach (var shift in Shifts.All)
        {
            var team = plant.TeamOnShift(date, shift);
            if (string.IsNullOrEmpty(team) || !actor.AllowsTeam(Perm.PointageVoir, team))
                continue;
            var stored = InWindow(actor, date) ? await store.FindAsync(date, shift, team) : null;
            if (stored != null)
            {
                list.Add((shift, team, stored, true));
                continue;
            }
            var blank = new AttendanceSheet { ProductionDate = date, ShiftId = shift, Team = team };
            foreach (var e in Members(employees, date, team))
            {
                var line = NewLine(blank, e, e.PostingAt(date)!, renfort: false, new());
                line.EntryAt = line.ExitAt = null;
                blank.Lines.Add(line);
            }
            blank.Lines = Order(blank.Lines);
            list.Add((shift, team, blank, false));
        }
        return list;
    }

    // ---------- checks the screen shows as you type ----------

    // What blocks validation, row by row (employee id 0 = the whole sheet).
    public static IReadOnlyList<(int EmployeeId, string Message)> Problems(AttendanceSheet sheet, bool validating)
    {
        var list = new List<(int, string)>();
        var earliest = Earliest(sheet);
        var latest = Latest(sheet);
        foreach (var l in sheet.Lines)
        {
            switch (l.Status)
            {
                case LineStatus.AConfirmer:
                    if (validating)
                        list.Add((l.EmployeeId, $"{l.FullName} : confirmez la présence (✓) ou choisissez une absence."));
                    break;
                case LineStatus.Present:
                    if (l.EntryAt is not { } entry || l.ExitAt is not { } exit)
                    {
                        list.Add((l.EmployeeId, $"{l.FullName} : indiquez l'heure d'entrée et de sortie."));
                        break;
                    }
                    if (!OnStep(entry) || !OnStep(exit))
                        list.Add((l.EmployeeId, $"{l.FullName} : les heures se donnent par pas de {StepMinutes} minutes."));
                    if (entry < earliest || exit > latest || entry > latest || exit < earliest)
                        list.Add((l.EmployeeId, $"{l.FullName} : heures hors du poste ({earliest:HH:mm} – {latest:HH:mm} au plus, heures supplémentaires comprises)."));
                    if (exit <= entry)
                        list.Add((l.EmployeeId, $"{l.FullName} : la sortie doit être après l'entrée."));
                    if (l.IsActing)
                    {
                        if (l.ActingFrom is not { } from || !OnStep(from) || from < entry || from >= exit)
                            list.Add((l.EmployeeId, $"{l.FullName} : le remplacement commence entre son entrée et sa sortie."));
                        var replaced = sheet.Lines.FirstOrDefault(x => x.EmployeeId == l.ReplacesEmployeeId);
                        if (replaced == null || replaced.EmployeeId == l.EmployeeId)
                            list.Add((l.EmployeeId, $"{l.FullName} : la personne remplacée n'est pas sur la feuille."));
                        else if (!(replaced.Status == LineStatus.Absent || replaced.Status == LineStatus.Present && replaced.IsActing))
                            list.Add((l.EmployeeId, $"{l.FullName} remplace {replaced.FullName}, qui est présent à son poste : retirez le remplacement."));
                        else if (sheet.Lines.Count(x => x.ReplacesEmployeeId == replaced.EmployeeId) > 1)
                            list.Add((l.EmployeeId, $"{replaced.FullName} est remplacé par plusieurs personnes : une seule par poste."));
                    }
                    break;
                case LineStatus.Absent:
                    if (l.Absence == null)
                        list.Add((l.EmployeeId, $"{l.FullName} : choisissez le code d'absence (A, M, AT ou C)."));
                    if (l.IsRenfort)
                        list.Add((l.EmployeeId, $"{l.FullName} est un renfort : s'il n'est pas venu, retirez-le de la feuille."));
                    break;
            }
            if (l.Status != LineStatus.Absent && l.Status != LineStatus.Ailleurs && StaffFunctions.HoldsMachine(l.Function) && l.MachineId == null)
                list.Add((l.EmployeeId, $"{l.FullName} : choisissez sa machine pour ce poste."));
        }
        foreach (var dup in sheet.Lines.GroupBy(l => l.EmployeeId).Where(g => g.Count() > 1))
            list.Add((dup.Key, $"{dup.First().FullName} est deux fois sur la feuille."));
        return list;
    }

    // Posts left with no one: an absent (or moved) holder of a machine or a
    // ligne nobody replaces. A warning, not a refusal.
    public static IReadOnlyList<(AttendanceLine Line, string Post)> Uncovered(AttendanceSheet sheet)
        => sheet.Lines
            .Where(l => l.Status == LineStatus.Absent || l.Status == LineStatus.Present && l.IsActing)
            .Where(l => StaffFunctions.HoldsMachine(l.Function) && l.MachineId != null || StaffFunctions.HoldsLine(l.Function) && l.LineId != null)
            .Where(l => !Covered(sheet, l))
            .Select(l => (l, l.MachineId ?? "ligne"))
            .ToList();

    // Someone replaces him, or another present person of the same function
    // works the same machine (two conducteurs on CC01) or ligne.
    private static bool Covered(AttendanceSheet sheet, AttendanceLine l)
        => sheet.Lines.Any(x => x.Status == LineStatus.Present
                                && (x.ReplacesEmployeeId == l.EmployeeId
                                    || x.EmployeeId != l.EmployeeId && !x.IsActing && x.Function == l.Function
                                       && (l.MachineId != null && x.MachineId == l.MachineId || l.LineId != null && x.LineId == l.LineId)));

    public static bool OnStep(DateTime t) => t.Second == 0 && t.Millisecond == 0 && t.Minute % StepMinutes == 0;

    // ---------- saving ----------

    // validate: the chef validates the sheet (every row confirmed). A
    // validated sheet stays validated; changing it is a correction, with a
    // reason.
    public async Task<AttendanceSheet> SaveAsync(Actor actor, AttendanceSheet draft, bool validate, string? correctionReason)
    {
        var stored = draft.IsNew ? null : await store.GetAsync(draft.Id) ?? throw new RuleViolationException("Feuille introuvable (supprimée entre-temps ?).");
        if (stored == null && await store.FindAsync(draft.ProductionDate.Date, draft.ShiftId, draft.Team) != null)
            throw Changed();
        if (stored != null && stored.Version != draft.Version)
            throw Changed();
        var team = plant.TeamOnShift(draft.ProductionDate.Date, draft.ShiftId);
        if (!string.Equals(team, draft.Team, StringComparison.OrdinalIgnoreCase) || stored != null && stored.Team != draft.Team)
            throw new RuleViolationException("Le roulement des équipes a changé pour ce poste : rechargez la feuille.");

        var correcting = stored?.Status == SheetStatus.Validee;
        var permission = correcting ? Perm.PointageCorriger : Perm.PointageSaisir;
        actor.Demand(permission);
        if (!actor.AllowsTeam(permission, team))
            throw new AccessDeniedException($"Ce poste est tenu par {team} : hors de votre périmètre.");
        if (!InWindow(actor, draft.ProductionDate))
            throw new AccessDeniedException("Vous pointez et corrigez les postes d'hier et d'aujourd'hui seulement (droit « Pointer et corriger avant hier » pour les autres).");
        if (draft.Start > Now)
            throw new RuleViolationException("Ce poste n'a pas encore commencé.");
        if (validate && !correcting && !CanValidateNow(draft))
            throw new RuleViolationException(
                $"Le pointage se valide en fin de poste, à partir de {ValidationOpensAt(draft):HH:mm} : d'ici là, enregistrez-le (brouillon).");
        var reason = correctionReason?.Trim() ?? "";
        if (correcting && reason.Length == 0)
            throw new RuleViolationException("Un pointage validé se corrige avec un motif : indiquez-le.");
        if (reason.Length > MaxNote)
            throw new RuleViolationException($"Le motif est limité à {MaxNote} caractères.");

        var sheet = new AttendanceSheet
        {
            Id = stored?.Id ?? 0,
            ProductionDate = draft.ProductionDate.Date,
            ShiftId = draft.ShiftId,
            Team = team,
            Status = correcting || validate ? SheetStatus.Validee : SheetStatus.Brouillon,
            Version = stored?.Version ?? 0,
            CreatedBy = stored?.CreatedBy ?? actor.Login,
            CreatedAt = stored?.CreatedAt ?? Now,
            UpdatedBy = stored == null ? null : actor.Login,
            UpdatedAt = stored == null ? null : Now,
            ValidatedBy = stored?.ValidatedBy,
            ValidatedAt = stored?.ValidatedAt,
        };
        if (sheet.Status == SheetStatus.Validee && !correcting)
        {
            sheet.ValidatedBy = actor.Login;
            sheet.ValidatedAt = Now;
        }
        sheet.Lines = await NormalizeAsync(sheet, draft.Lines);
        await CheckAsync(sheet, sheet.Status == SheetStatus.Validee);

        var action = correcting ? AuditActions.SheetCorrected : sheet.Status == SheetStatus.Validee ? AuditActions.SheetValidated : AuditActions.SheetSaved;
        var changes = Diff(stored, sheet);
        if (stored != null && changes.Count == 0 && stored.Status == sheet.Status)
            return stored;
        var revision = new AttendanceRevision
        {
            At = Now, UserLogin = actor.Login, Action = action, Note = reason,
            Changes = stored == null ? sheet.Summary() + (changes.Count > 0 ? "\n" + string.Join("\n", changes) : "") : string.Join("\n", changes),
        };
        if (stored == null)
        {
            sheet.Version = 1;
            revision.Version = 1;
            sheet.Id = await store.InsertAsync(sheet, revision);
        }
        else
        {
            sheet.Version = stored.Version + 1;
            revision.Version = sheet.Version;
            revision.SheetId = sheet.Id;
            if (!await store.UpdateAsync(sheet, stored.Version, revision))
                throw Changed();
        }
        await audit.WriteAsync(new AuditEntry
        {
            At = Now, UserLogin = actor.Login, Module = Modules.Pointage, Action = action,
            Target = $"Pointage {sheet.Poste}", Details = reason,
            OldValue = stored?.Summary(), NewValue = sheet.Summary() + (changes.Count == 0 ? "" : " · " + string.Join(" ; ", changes.Take(20))),
            Workstation = client.Workstation,
        });
        return (await store.GetAsync(sheet.Id))!;
    }

    // ---------- internals ----------

    private static RuleViolationException Changed()
        => new("Cette feuille a été modifiée entre-temps par quelqu'un d'autre. Elle est rechargée : vérifiez-la, puis recommencez.");

    private static IEnumerable<Employee> Members(IEnumerable<Employee> employees, DateTime date, string team)
        => employees.Where(e => e.IsActive && e.PostingAt(date) is { } p && string.Equals(p.Team, team, StringComparison.OrdinalIgnoreCase));

    private static AttendanceLine NewLine(AttendanceSheet sheet, Employee e, EmployeePosting p, bool renfort,
        Dictionary<int, (AttendanceSheet Sheet, AttendanceLine Line)> elsewhere)
    {
        var away = elsewhere.ContainsKey(e.Id) && !renfort;
        return new AttendanceLine
        {
            EmployeeId = e.Id,
            FullName = e.FullName,
            Function = p.Function,
            HomeTeam = p.Team,
            IsRenfort = renfort,
            Status = away ? LineStatus.Ailleurs : LineStatus.AConfirmer,
            EntryAt = away ? null : sheet.Start,
            ExitAt = away ? null : sheet.End,
            MachineId = p.MachineId,
            LineId = p.LineId,
        };
    }

    // Employees pointed present in another team's sheet over this poste.
    private async Task<Dictionary<int, (AttendanceSheet Sheet, AttendanceLine Line)>> ElsewhereAsync(DateTime date, int shiftId, string team, long sheetId)
    {
        var start = Shifts.Start(date, shiftId);
        var end = start.AddHours(8);
        var result = new Dictionary<int, (AttendanceSheet, AttendanceLine)>();
        foreach (var s in await store.ListAsync(date.AddDays(-1), date.AddDays(1)))
        {
            if (s.Id == sheetId || string.Equals(s.Team, team, StringComparison.OrdinalIgnoreCase) && s.ProductionDate == date && s.ShiftId == shiftId)
                continue;
            foreach (var l in s.Lines.Where(l => l.Status == LineStatus.Present && l.EntryAt < end && l.ExitAt > start))
                result[l.EmployeeId] = (s, l);
        }
        return result;
    }

    // Rebuilds each row from the employee list: names, functions and teams
    // come from the referential, never from the screen; the post of an
    // acting row comes from the person replaced.
    private async Task<List<AttendanceLine>> NormalizeAsync(AttendanceSheet sheet, IEnumerable<AttendanceLine> draft)
    {
        var employees = (await staff.ListEmployeesAsync()).ToDictionary(e => e.Id);
        var machines = (await plant.GetMachinesAsync()).Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lines = (await staff.ListLinesAsync()).Where(l => l.IsActive).Select(l => l.Id).ToHashSet();
        var result = new List<AttendanceLine>();
        foreach (var d in draft)
        {
            if (!employees.TryGetValue(d.EmployeeId, out var e))
                throw new RuleViolationException("Un employé de la feuille n'existe plus : rechargez la feuille.");
            var posting = e.PostingAt(sheet.ProductionDate)
                          ?? throw new RuleViolationException($"{e.FullName} n'est pas encore employé à cette date.");
            var renfort = !string.Equals(posting.Team, sheet.Team, StringComparison.OrdinalIgnoreCase);
            if (renfort && d.Status == LineStatus.Ailleurs)
                throw new RuleViolationException($"{e.FullName} est un renfort : pointez-le présent, ou retirez-le de la feuille.");
            var line = new AttendanceLine
            {
                EmployeeId = e.Id,
                FullName = e.FullName,
                Function = posting.Function,
                HomeTeam = posting.Team,
                IsRenfort = renfort,
                Status = d.Status,
                Absence = d.Status == LineStatus.Absent ? d.Absence : null,
                EntryAt = d.Status == LineStatus.Present || d.Status == LineStatus.AConfirmer ? d.EntryAt : null,
                ExitAt = d.Status == LineStatus.Present || d.Status == LineStatus.AConfirmer ? d.ExitAt : null,
                MachineId = StaffFunctions.HoldsMachine(posting.Function) ? d.MachineId ?? posting.MachineId : null,
                LineId = StaffFunctions.HoldsLine(posting.Function) ? d.LineId ?? posting.LineId : null,
            };
            if (line.MachineId != null && !machines.Contains(line.MachineId))
                throw new RuleViolationException($"{e.FullName} : machine inconnue ({line.MachineId}).");
            if (line.LineId is { } lid && !lines.Contains(lid))
                throw new RuleViolationException($"{e.FullName} : ligne inconnue ou inactive.");
            if (line.Status == LineStatus.Present && d.ReplacesEmployeeId != null)
            {
                line.ReplacesEmployeeId = d.ReplacesEmployeeId;
                line.ActingFrom = d.ActingFrom;
            }
            result.Add(line);
        }
        foreach (var l in result.Where(l => l.IsActing))
        {
            var replaced = result.FirstOrDefault(x => x.EmployeeId == l.ReplacesEmployeeId);
            if (replaced == null)
                continue;
            if (!StaffFunctions.HoldsMachine(replaced.Function) && !StaffFunctions.HoldsLine(replaced.Function))
                throw new RuleViolationException($"{replaced.FullName} ({StaffFunctions.Label(replaced.Function)}) ne tient ni machine ni ligne : rien à remplacer.");
            l.ActingFunction = replaced.Function;
            l.ActingMachineId = replaced.MachineId;
            l.ActingLineId = replaced.LineId;
        }
        return Order(result);
    }

    private async Task CheckAsync(AttendanceSheet sheet, bool validating)
    {
        var problems = Problems(sheet, validating);
        if (problems.Count > 0)
            throw new RuleViolationException(problems[0].Message + (problems.Count > 1 ? $" (et {problems.Count - 1} autre(s) point(s) à corriger)" : ""));

        if (validating)
        {
            var employees = await staff.ListEmployeesAsync();
            var missing = Members(employees, sheet.ProductionDate, sheet.Team).Where(e => sheet.Lines.All(l => l.EmployeeId != e.Id)).ToList();
            if (missing.Count > 0)
                throw new RuleViolationException($"{missing[0].FullName} est de l'équipe : il doit figurer sur la feuille (présent, absent ou en renfort ailleurs).");
        }

        // No one at two places at once, and "en renfort ailleurs" only when
        // another sheet says so.
        var others = (await store.ListAsync(sheet.ProductionDate.AddDays(-1), sheet.ProductionDate.AddDays(1)))
            .Where(s => s.Id != sheet.Id && !(s.ProductionDate == sheet.ProductionDate && s.ShiftId == sheet.ShiftId && s.Team == sheet.Team))
            .ToList();
        foreach (var l in sheet.Lines)
        {
            if (l.Status == LineStatus.Present)
            {
                var clash = others.SelectMany(s => s.Lines.Where(x => x.EmployeeId == l.EmployeeId && x.Status == LineStatus.Present
                                                                     && x.EntryAt < l.ExitAt && x.ExitAt > l.EntryAt).Select(x => (s, x)))
                    .FirstOrDefault();
                if (clash.s != null)
                    throw new RuleViolationException(
                        $"{l.FullName} est déjà pointé {clash.s.Team}, poste {Shifts.Label(clash.s.ShiftId)} du {clash.s.ProductionDate:dd/MM}, " +
                        $"de {clash.x.EntryAt:HH:mm} à {clash.x.ExitAt:HH:mm} : une personne ne peut pas être à deux endroits à la fois.");
            }
            else if (l.Status == LineStatus.Ailleurs)
            {
                var away = others.Any(s => s.Lines.Any(x => x.EmployeeId == l.EmployeeId && x.Status == LineStatus.Present
                                                           && x.EntryAt < sheet.End && x.ExitAt > sheet.Start));
                if (!away)
                    throw new RuleViolationException($"{l.FullName} n'est plus pointé dans une autre équipe : confirmez sa présence ou son absence ici.");
            }
        }
    }

    // "TRIKI Faez : présent 06:00–14:00 → A (Absent)"
    private static List<string> Diff(AttendanceSheet? before, AttendanceSheet after)
    {
        var changes = new List<string>();
        var old = before?.Lines.ToDictionary(l => l.EmployeeId) ?? new Dictionary<int, AttendanceLine>();
        foreach (var l in after.Lines)
        {
            if (!old.TryGetValue(l.EmployeeId, out var o))
            {
                if (before != null || l.IsRenfort || l.Status != LineStatus.Present || l.EntryAt != after.Start || l.ExitAt != after.End || l.IsActing)
                    changes.Add($"{l.FullName}{(l.IsRenfort ? $" (renfort {l.HomeTeam})" : "")} : {l.Describe()}");
                continue;
            }
            var was = o.Describe() + (o.MachineId != l.MachineId ? $" sur {o.MachineId}" : "");
            var now = l.Describe() + (o.MachineId != l.MachineId ? $" sur {l.MachineId}" : "");
            if (was != now)
                changes.Add($"{l.FullName} : {was} → {now}");
        }
        foreach (var o in old.Values.Where(o => after.Lines.All(l => l.EmployeeId != o.EmployeeId)))
            changes.Add($"{o.FullName} : retiré de la feuille (était {o.Describe()})");
        return changes;
    }
}
