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
    IAttendanceStore store, IStaffStore staff, IPlantReferential plant, IAuditLog audit, TimeProvider clock, ClientInfo client,
    OvertimeService overtime, IPermutationStore permutations, RulesService rules)
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

    // ---------- the rows of a plateforme ----------

    // A team's sheet holds the people of every plateforme. An account given
    // plateformes changes only the rows of their machines: the people whose
    // machine, ligne or replaced post is on one of them, and those with no
    // machine (chef de conditionnement, nettoyage, an emballage worker not
    // placed yet). The other rows are shown, read only: the chef of the
    // other plateforme points them.
    public async Task<RowRule> RowsAsync(Actor actor, AttendanceSheet sheet)
    {
        var permission = sheet.Status == SheetStatus.Validee ? Perm.PointageCorriger : Perm.PointageSaisir;
        return new RowRule(actor, permission, sheet.Team, await plant.GetMachinesAsync(), await staff.ListLinesAsync());
    }

    public sealed class RowRule
    {
        private readonly Actor _actor;
        private readonly string _permission;
        private readonly string _team;
        private readonly Dictionary<string, PlantMachine> _machines;
        private readonly Dictionary<int, ProductionLine> _lines;

        internal RowRule(Actor actor, string permission, string team, IEnumerable<PlantMachine> machines, IEnumerable<ProductionLine> lines)
        {
            _actor = actor;
            _permission = permission;
            _team = team;
            _machines = machines.DistinctBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
            _lines = lines.ToDictionary(l => l.Id);
        }

        private IEnumerable<string> MachinesOf(AttendanceLine l)
        {
            if (l.MachineId != null)
                yield return l.MachineId;
            if (l.ActingMachineId != null)
                yield return l.ActingMachineId;
            foreach (var id in new[] { l.LineId, l.ActingLineId })
                if (id is { } lid && _lines.TryGetValue(lid, out var line))
                    foreach (var m in line.Machines)
                        yield return m;
        }

        // The machine may be chosen for a row (a floating emballage worker).
        public bool AllowsMachine(string machineId)
            => _machines.TryGetValue(machineId, out var m) && _actor.Allows(_permission, m.Sector, m.Id, _team);

        public bool Allows(AttendanceLine l)
        {
            var machines = MachinesOf(l).ToList();
            return machines.Count == 0 || machines.Any(AllowsMachine);
        }

        // "CC03 · CC04" in display names: where a row the actor may not change works.
        public string Where(AttendanceLine l)
            => string.Join(", ", MachinesOf(l).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(m => _machines.TryGetValue(m, out var pm) ? pm.Name : m));
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
        var swaps = await PermutationsOnAsync(date);
        if (sheet.Status == SheetStatus.Brouillon)
            ApplyPermutations(sheet, swaps, employees.ToDictionary(e => e.Id));
        else
            MarkPermutations(sheet, swaps);
        if (sheet.Status == SheetStatus.Brouillon)
        {
            // A member forbidden overtime who already works another poste
            // that day was moved there: not expected on his own poste.
            var book = await overtime.BookAsync(date);
            foreach (var l in sheet.Lines.Where(l => l.Status == LineStatus.AConfirmer && !l.IsRenfort && l.PermutationId == null))
                if (book.State(l.EmployeeId, date, sheet).Restricted && book.WorksElsewhere(l.EmployeeId, sheet))
                {
                    l.Status = LineStatus.Ailleurs;
                    l.EntryAt = l.ExitAt = null;
                }
        }
        sheet.Lines = Order(sheet.Lines);
        return sheet;
    }

    // What each person of the sheet may still do in overtime (bans, the
    // monthly limit): the screen checks the rows with it as the chef types.
    public async Task<OvertimeBook> OvertimeAsync(Actor actor, AttendanceSheet sheet)
    {
        actor.Demand(Perm.PointageVoir);
        return await overtime.BookAsync(sheet.ProductionDate);
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
            .Where(e => e.ActiveOn(sheet.ProductionDate) && sheet.Lines.All(l => l.EmployeeId != e.Id))
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
                    // A permuted renfort who did not come is absent here: his own poste shows him permuted.
                    if (l.IsRenfort && l.PermutationId == null)
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
        var swaps = await PermutationsOnAsync(sheet.ProductionDate);
        MarkPermutations(sheet, swaps);
        if (stored != null)
            MarkPermutations(stored, swaps);
        CheckPermutations(sheet, swaps, sheet.Status == SheetStatus.Validee);
        await CheckRowsAsync(actor, stored ?? draft, stored, sheet);
        await TransferredAwayAsync(sheet);
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
        await SyncTransfersAsync(actor, sheet, stored);
        var saved = (await store.GetAsync(sheet.Id))!;
        MarkPermutations(saved, swaps);
        return saved;
    }

    // ---------- transfers ----------

    // Pointed absent on his poste (A, M or C) but present on another poste
    // of the same production day: he was moved there. His absent row
    // becomes "en renfort ailleurs" (keeping the code it had, to go back to
    // it if the other poste is undone). An accident de travail stays.
    public static readonly IReadOnlyList<AbsenceCode> TransferCodes = new[] { AbsenceCode.A, AbsenceCode.M, AbsenceCode.C };

    private static bool SameSheet(AttendanceSheet a, AttendanceSheet b)
        => a.Id != 0 && a.Id == b.Id
           || a.ProductionDate.Date == b.ProductionDate.Date && a.ShiftId == b.ShiftId && string.Equals(a.Team, b.Team, StringComparison.OrdinalIgnoreCase);

    private async Task<List<AttendanceSheet>> SameDayAsync(AttendanceSheet sheet)
        => (await store.ListAsync(sheet.ProductionDate, sheet.ProductionDate)).Where(s => !SameSheet(s, sheet)).ToList();

    private static AttendanceSheet? PresentIn(IEnumerable<AttendanceSheet> sheets, int employeeId)
        => sheets.FirstOrDefault(s => s.Lines.Any(l => l.EmployeeId == employeeId && l.Status == LineStatus.Present));

    private static string Moved(AttendanceLine l, AbsenceCode was, AttendanceSheet to)
        => $"{l.FullName} : {was} ({AbsenceCodes.Label(was)}) → transféré au poste {Shifts.Label(to.ShiftId)} du {to.ProductionDate:dd/MM} · {to.Team}";

    // The sheet being saved: its absents already present on another poste.
    private async Task TransferredAwayAsync(AttendanceSheet sheet)
    {
        var absents = sheet.Lines.Where(l => l.Status == LineStatus.Absent && l.Absence is { } a && TransferCodes.Contains(a)).ToList();
        if (absents.Count == 0)
            return;
        var others = await SameDayAsync(sheet);
        foreach (var l in absents)
            if (PresentIn(others, l.EmployeeId) != null)
            {
                l.Status = LineStatus.Ailleurs;
                l.EntryAt = l.ExitAt = null;
                ClearActingOf(l);
            }
    }

    private static void ClearActingOf(AttendanceLine l)
    {
        l.ReplacesEmployeeId = null;
        l.ActingFunction = null;
        l.ActingMachineId = null;
        l.ActingLineId = null;
        l.ActingFrom = null;
    }

    // After a save: the other sheets of the day follow. Present here and
    // absent (A, M, C) there = transferred; no longer present here and
    // transferred there = absent again with the code he had.
    private async Task SyncTransfersAsync(Actor actor, AttendanceSheet sheet, AttendanceSheet? stored)
    {
        var present = sheet.Lines.Where(l => l.Status == LineStatus.Present).Select(l => l.EmployeeId).ToHashSet();
        var concerned = present.Concat(stored?.Lines.Where(l => l.Status == LineStatus.Present).Select(l => l.EmployeeId) ?? Array.Empty<int>()).ToHashSet();
        if (concerned.Count == 0)
            return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var others = await SameDayAsync(sheet);
            var conflict = false;
            foreach (var other in others.Where(o => o.Lines.Any(l => concerned.Contains(l.EmployeeId))))
            {
                var copy = other.Clone();
                var changes = new List<string>();
                foreach (var l in copy.Lines.Where(l => concerned.Contains(l.EmployeeId)))
                {
                    if (l.Status == LineStatus.Absent && l.Absence is { } was && TransferCodes.Contains(was) && present.Contains(l.EmployeeId))
                    {
                        l.Status = LineStatus.Ailleurs;
                        l.EntryAt = l.ExitAt = null;
                        ClearActingOf(l);
                        changes.Add(Moved(l, was, sheet));
                    }
                    else if (l.Status == LineStatus.Ailleurs && l.Absence is { } code && !present.Contains(l.EmployeeId)
                             && PresentIn(others.Where(o => o.Id != other.Id), l.EmployeeId) == null)
                    {
                        l.Status = LineStatus.Absent;
                        changes.Add($"{l.FullName} : transfert au poste {Shifts.Label(sheet.ShiftId)} · {sheet.Team} annulé → {code} ({AbsenceCodes.Label(code)})");
                    }
                }
                if (changes.Count == 0)
                    continue;
                copy.Version = other.Version + 1;
                copy.UpdatedBy = actor.Login;
                copy.UpdatedAt = Now;
                var revision = new AttendanceRevision
                {
                    SheetId = other.Id, Version = copy.Version, At = Now, UserLogin = actor.Login, Action = AuditActions.SheetTransfer,
                    Changes = string.Join("\n", changes), Note = $"Automatique : pointage {sheet.Poste}",
                };
                if (!await store.UpdateAsync(copy, other.Version, revision))
                {
                    conflict = true;
                    break;
                }
                await audit.WriteAsync(new AuditEntry
                {
                    At = Now, UserLogin = actor.Login, Module = Modules.Pointage, Action = AuditActions.SheetTransfer,
                    Target = $"Pointage {other.Poste}", Details = revision.Note, OldValue = other.Summary(),
                    NewValue = copy.Summary() + " · " + string.Join(" ; ", changes), Workstation = client.Workstation,
                });
            }
            if (!conflict)
                return;
        }
    }

    // People pointed "accident de travail" on another poste of the day: not
    // transferred, the sheet warns.
    public async Task<IReadOnlyDictionary<int, AttendanceSheet>> AccidentElsewhereAsync(Actor actor, AttendanceSheet sheet)
    {
        actor.Demand(Perm.PointageVoir);
        var result = new Dictionary<int, AttendanceSheet>();
        foreach (var s in await SameDayAsync(sheet))
            foreach (var l in s.Lines.Where(l => l.Status == LineStatus.Absent && l.Absence == AbsenceCode.AT))
                result[l.EmployeeId] = s;
        return result;
    }

    // ---------- permutations ----------

    // A member of the sheet's team swaps postes, for this production day,
    // with someone of another team working another poste that day: each one
    // goes to the other's poste. Recorded at once (not with the sheet); the
    // drafts already saved of both postes follow, the others when they open.
    public sealed record PermutationCandidate(Employee Employee, EmployeePosting Posting, int ShiftId, string? Refusal);

    public Task<PermutationRule> PermutationRuleAsync() => rules.PermutationAsync();

    private async Task<List<AttendancePermutation>> PermutationsOnAsync(DateTime date)
        => (await permutations.ListAsync(date.Date, date.Date)).Where(p => p.IsActive).ToList();

    // The permutations in force on this sheet's poste.
    public async Task<IReadOnlyList<AttendancePermutation>> PermutationsAsync(Actor actor, AttendanceSheet sheet)
    {
        actor.Demand(Perm.PointageVoir);
        return (await PermutationsOnAsync(sheet.ProductionDate)).Where(p => p.Touches(sheet.ProductionDate, sheet.ShiftId, sheet.Team)).ToList();
    }

    // Why this row cannot be permuted (null = it can), and the people it can
    // be permuted with, those refused greyed with the reason.
    public async Task<(string? Refusal, IReadOnlyList<PermutationCandidate> Candidates)> PermutationCandidatesAsync(
        Actor actor, AttendanceSheet sheet, int employeeId)
    {
        actor.Demand(Perm.PointageVoir);
        var ctx = await PermutationContextAsync(sheet.ProductionDate);
        var line = sheet.Lines.FirstOrDefault(l => l.EmployeeId == employeeId);
        var refusal = await LineRefusalAsync(actor, sheet, line, ctx.Swaps);
        if (refusal != null || line == null)
            return (refusal, Array.Empty<PermutationCandidate>());
        var list = new List<PermutationCandidate>();
        foreach (var e in ctx.Employees.Values.Where(e => e.ActiveOn(sheet.ProductionDate)))
        {
            if (e.PostingAt(sheet.ProductionDate) is not { } posting || string.Equals(posting.Team, sheet.Team, StringComparison.OrdinalIgnoreCase))
                continue;
            var shift = PosteOfTeam(sheet.ProductionDate, posting.Team, sheet.ShiftId);
            if (shift == 0)
                continue;
            list.Add(new PermutationCandidate(e, posting, shift, CandidateRefusal(sheet, line, e, posting, shift, ctx)));
        }
        return (null, list
            .OrderBy(c => c.Refusal != null)
            .ThenBy(c => c.Posting.Team, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.Employee.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList());
    }

    public async Task<AttendancePermutation> PermuteAsync(Actor actor, AttendanceSheet sheet, int employeeA, int employeeB)
    {
        actor.Demand(Perm.PointageSaisir);
        if (!sheet.IsNew && (await store.GetAsync(sheet.Id))?.Version != sheet.Version)
            throw Changed();
        var date = sheet.ProductionDate.Date;
        var ctx = await PermutationContextAsync(date);
        var line = sheet.Lines.FirstOrDefault(l => l.EmployeeId == employeeA);
        if (await LineRefusalAsync(actor, sheet, line, ctx.Swaps) is { } refusal)
            throw new RuleViolationException(refusal);
        if (!ctx.Employees.TryGetValue(employeeB, out var b) || !b.ActiveOn(date) || b.PostingAt(date) is not { } posting
            || string.Equals(posting.Team, sheet.Team, StringComparison.OrdinalIgnoreCase))
            throw new RuleViolationException("Choisissez une personne d'une autre équipe, employée ce jour-là.");
        var shift = PosteOfTeam(date, posting.Team, sheet.ShiftId);
        if (shift == 0)
            throw new RuleViolationException($"{posting.Team} ne travaille pas un autre poste le {date:dd/MM} : pas de permutation possible.");
        if (CandidateRefusal(sheet, line!, b, posting, shift, ctx) is { } why)
            throw new RuleViolationException($"{b.FullName} : {why}.");

        var p = new AttendancePermutation
        {
            ProductionDate = date,
            EmployeeA = line!.EmployeeId, NameA = line.FullName, TeamA = sheet.Team, ShiftA = sheet.ShiftId,
            EmployeeB = b.Id, NameB = b.FullName, TeamB = posting.Team, ShiftB = shift,
            CreatedBy = actor.Login, CreatedAt = Now,
        };
        p.Id = await permutations.InsertAsync(p);
        var swaps = ctx.Swaps.Append(p).ToList();
        foreach (var (s, team) in new[] { (p.ShiftA, p.TeamA), (p.ShiftB, p.TeamB) })
            await FollowAsync(actor, date, s, team, copy => ApplyPermutations(copy, swaps, ctx.Employees), AuditActions.Permuted, p.Describe());
        await audit.WriteAsync(new AuditEntry
        {
            At = Now, UserLogin = actor.Login, Module = Modules.Pointage, Action = AuditActions.Permuted,
            Target = $"Pointage du {date:dd/MM/yyyy}", NewValue = p.Describe(), Workstation = client.Workstation,
        });
        return p;
    }

    public async Task CancelPermutationAsync(Actor actor, long id)
    {
        actor.Demand(Perm.PointageSaisir);
        var p = await permutations.GetAsync(id) ?? throw new RuleViolationException("Permutation introuvable.");
        if (!p.IsActive)
            return;
        if (!actor.AllowsTeam(Perm.PointageSaisir, p.TeamA) && !actor.AllowsTeam(Perm.PointageSaisir, p.TeamB))
            throw new AccessDeniedException("Cette permutation concerne des équipes hors de votre périmètre.");
        if (!InWindow(actor, p.ProductionDate))
            throw new AccessDeniedException("Vous pointez les postes d'hier et d'aujourd'hui seulement (droit « Pointer et corriger avant hier » pour les autres).");
        foreach (var (s, team) in new[] { (p.ShiftA, p.TeamA), (p.ShiftB, p.TeamB) })
            if ((await store.FindAsync(p.ProductionDate, s, team))?.Status == SheetStatus.Validee)
                throw new RuleViolationException(
                    $"La feuille {Shifts.Label(s)} · {team} est déjà validée : la permutation ne s'annule plus (corrigez les feuilles avec un motif).");
        if (!await permutations.CancelAsync(id, actor.Login, Now))
            return;
        foreach (var (s, team) in new[] { (p.ShiftA, p.TeamA), (p.ShiftB, p.TeamB) })
            await FollowAsync(actor, p.ProductionDate, s, team, copy => UndoPermutation(copy, p), AuditActions.PermutationCancelled, p.Describe());
        await audit.WriteAsync(new AuditEntry
        {
            At = Now, UserLogin = actor.Login, Module = Modules.Pointage, Action = AuditActions.PermutationCancelled,
            Target = $"Pointage du {p.ProductionDate:dd/MM/yyyy}", OldValue = p.Describe(), Workstation = client.Workstation,
        });
    }

    private sealed record PermutationContext(
        List<AttendancePermutation> Swaps, IReadOnlyList<AttendanceSheet> Sheets, Dictionary<int, Employee> Employees, PermutationRule Rule);

    private async Task<PermutationContext> PermutationContextAsync(DateTime date)
        => new(await PermutationsOnAsync(date), await store.ListAsync(date.Date.AddDays(-1), date.Date.AddDays(1)),
            (await staff.ListEmployeesAsync()).ToDictionary(e => e.Id), await rules.PermutationAsync());

    // The other poste the team works that day, 0 = none.
    private int PosteOfTeam(DateTime date, string team, int notShift)
        => Shifts.All.FirstOrDefault(s => s != notShift && string.Equals(plant.TeamOnShift(date.Date, s), team, StringComparison.OrdinalIgnoreCase));

    private async Task<string?> LineRefusalAsync(Actor actor, AttendanceSheet sheet, AttendanceLine? line, IEnumerable<AttendancePermutation> swaps)
    {
        if (!CanEnter(actor, sheet))
            return "La permutation se fait sur une feuille en brouillon de votre équipe, d'hier ou d'aujourd'hui.";
        if (line == null)
            return "Cette personne n'est pas sur la feuille.";
        if (line.IsRenfort)
            return $"{line.FullName} est un renfort : seuls les membres de l'équipe se permutent.";
        if (swaps.Any(p => p.Involves(line.EmployeeId)))
            return $"{line.FullName} est déjà permuté ce jour-là : annulez d'abord cette permutation.";
        if (line.Status == LineStatus.Ailleurs)
            return $"{line.FullName} est pointé dans une autre équipe.";
        var rows = await RowsAsync(actor, sheet);
        if (!rows.Allows(line))
            return $"{line.FullName} travaille sur {rows.Where(line)} : hors de vos plateformes.";
        return null;
    }

    private static string? CandidateRefusal(AttendanceSheet sheet, AttendanceLine line, Employee b, EmployeePosting posting, int shift,
        PermutationContext ctx)
    {
        var date = sheet.ProductionDate.Date;
        if (ctx.Rule.SameFunction && posting.Function != line.Function)
            return $"autre fonction ({StaffFunctions.Label(posting.Function)})";
        if (ctx.Swaps.Any(p => p.Involves(b.Id)))
            return "déjà permuté ce jour-là";
        var home = ctx.Sheets.FirstOrDefault(s => s.ProductionDate.Date == date && s.ShiftId == shift
                                                  && string.Equals(s.Team, posting.Team, StringComparison.OrdinalIgnoreCase));
        if (home?.Status == SheetStatus.Validee)
            return $"la feuille de son poste ({Shifts.Label(shift)}) est déjà validée";
        if (home?.Lines.FirstOrDefault(l => l.EmployeeId == b.Id) is { Status: LineStatus.Present })
            return $"déjà pointé présent à son poste ({Shifts.Label(shift)})";
        var otherStart = Shifts.Start(date, shift);
        var otherEnd = otherStart.AddHours(8);
        foreach (var s in ctx.Sheets.Where(s => !SameSheet(s, sheet)))
        {
            if (s != home && s.Lines.Any(l => l.EmployeeId == b.Id && l.Status == LineStatus.Present && l.EntryAt < sheet.End && l.ExitAt > sheet.Start))
                return $"déjà pointé {s.Team} à ces heures";
            if (s.Lines.Any(l => l.EmployeeId == line.EmployeeId && l.Status == LineStatus.Present && l.EntryAt < otherEnd && l.ExitAt > otherStart))
                return $"{line.FullName} est déjà pointé {s.Team} sur le poste {Shifts.Label(shift)}";
        }
        return null;
    }

    // Marks the rows of the permutations of the sheet's poste (not stored).
    private static void MarkPermutations(AttendanceSheet sheet, IEnumerable<AttendancePermutation> swaps)
    {
        foreach (var l in sheet.Lines)
        {
            l.PermutationId = null;
            l.PermutedWith = null;
        }
        foreach (var p in swaps.Where(p => p.ProductionDate.Date == sheet.ProductionDate.Date))
        {
            if (p.On(sheet.ShiftId, sheet.Team) is not { } s)
                continue;
            foreach (var l in sheet.Lines)
                if (l.EmployeeId == s.Out && !l.IsRenfort || l.EmployeeId == s.In && l.IsRenfort)
                {
                    l.PermutationId = p.Id;
                    l.PermutedWith = l.EmployeeId == s.Out ? s.InName : s.OutName;
                }
        }
    }

    // A draft follows the permutations of its poste: who left is "ailleurs",
    // who came is a renfort row at the poste's hours, on the post of the one
    // he replaces. Returns what changed (the revision).
    private static List<string> ApplyPermutations(AttendanceSheet sheet, IEnumerable<AttendancePermutation> swaps, IReadOnlyDictionary<int, Employee> employees)
    {
        var changes = new List<string>();
        var list = swaps.ToList();
        foreach (var p in list.Where(p => p.ProductionDate.Date == sheet.ProductionDate.Date))
        {
            if (p.On(sheet.ShiftId, sheet.Team) is not { } s)
                continue;
            var gone = sheet.Lines.FirstOrDefault(l => l.EmployeeId == s.Out && !l.IsRenfort);
            if (gone != null && gone.Status != LineStatus.Ailleurs)
            {
                var (toShift, toTeam) = p.PosteOf(s.Out);
                gone.Status = LineStatus.Ailleurs;
                gone.Absence = null;
                gone.EntryAt = gone.ExitAt = null;
                ClearActingOf(gone);
                foreach (var cover in sheet.Lines.Where(l => l.ReplacesEmployeeId == s.Out))
                    ClearActingOf(cover);
                changes.Add($"{gone.FullName} : permuté avec {s.InName} → poste {Shifts.Label(toShift)} · {toTeam}");
            }
            if (sheet.Lines.All(l => l.EmployeeId != s.In) && employees.TryGetValue(s.In, out var e) && e.PostingAt(sheet.ProductionDate) is { } posting)
            {
                var came = NewLine(sheet, e, posting, renfort: true, new());
                if (gone != null && posting.Function == gone.Function)
                {
                    came.MachineId = gone.MachineId ?? came.MachineId;
                    came.LineId = gone.LineId ?? came.LineId;
                }
                sheet.Lines.Add(came);
                changes.Add($"{came.FullName} (renfort {came.HomeTeam}) : permuté avec {s.OutName}, à confirmer");
            }
        }
        sheet.Lines = Order(sheet.Lines);
        MarkPermutations(sheet, list);
        return changes;
    }

    private static List<string> UndoPermutation(AttendanceSheet sheet, AttendancePermutation p)
    {
        var changes = new List<string>();
        if (p.On(sheet.ShiftId, sheet.Team) is not { } s)
            return changes;
        if (sheet.Lines.FirstOrDefault(l => l.EmployeeId == s.Out && !l.IsRenfort) is { Status: LineStatus.Ailleurs } gone)
        {
            gone.Status = LineStatus.AConfirmer;
            gone.EntryAt = sheet.Start;
            gone.ExitAt = sheet.End;
            changes.Add($"{gone.FullName} : permutation avec {s.InName} annulée → de retour sur son poste, à confirmer");
        }
        if (sheet.Lines.FirstOrDefault(l => l.EmployeeId == s.In && l.IsRenfort) is { } came)
        {
            sheet.Lines.Remove(came);
            foreach (var cover in sheet.Lines.Where(l => l.ReplacesEmployeeId == s.In))
                ClearActingOf(cover);
            changes.Add($"{came.FullName} : permutation avec {s.OutName} annulée → retiré de la feuille");
        }
        return changes;
    }

    // Who left must stay "ailleurs"; who came must be on the sheet to validate it.
    private static void CheckPermutations(AttendanceSheet sheet, IEnumerable<AttendancePermutation> swaps, bool validating)
    {
        foreach (var p in swaps.Where(p => p.ProductionDate.Date == sheet.ProductionDate.Date))
        {
            if (p.On(sheet.ShiftId, sheet.Team) is not { } s)
                continue;
            if (sheet.Lines.FirstOrDefault(l => l.EmployeeId == s.Out && !l.IsRenfort) is { } gone && gone.Status != LineStatus.Ailleurs)
                throw new RuleViolationException($"{gone.FullName} est permuté avec {s.InName} ce jour-là : annulez la permutation pour le pointer sur ce poste.");
            if (validating && sheet.Lines.All(l => l.EmployeeId != s.In))
                throw new RuleViolationException($"{s.InName} est permuté sur ce poste à la place de {s.OutName} : il doit figurer sur la feuille, présent ou absent.");
        }
    }

    // A stored draft of that poste changed by `change`, with a revision; a
    // validated sheet or one not saved yet is left alone (it follows when opened).
    private async Task FollowAsync(Actor actor, DateTime date, int shiftId, string team, Func<AttendanceSheet, List<string>> change, string action, string note)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stored = await store.FindAsync(date.Date, shiftId, team);
            if (stored == null || stored.Status != SheetStatus.Brouillon)
                return;
            var copy = stored.Clone();
            var changes = change(copy);
            if (changes.Count == 0)
                return;
            copy.Version = stored.Version + 1;
            copy.UpdatedBy = actor.Login;
            copy.UpdatedAt = Now;
            var revision = new AttendanceRevision
            {
                SheetId = stored.Id, Version = copy.Version, At = Now, UserLogin = actor.Login, Action = action,
                Changes = string.Join("\n", changes), Note = note,
            };
            if (await store.UpdateAsync(copy, stored.Version, revision))
                return;
        }
        throw Changed();
    }

    // ---------- internals ----------

    private static RuleViolationException Changed()
        => new("Cette feuille a été modifiée entre-temps par quelqu'un d'autre. Elle est rechargée : vérifiez-la, puis recommencez.");

    private static IEnumerable<Employee> Members(IEnumerable<Employee> employees, DateTime date, string team)
        => employees.Where(e => e.ActiveOn(date) && e.PostingAt(date) is { } p && string.Equals(p.Team, team, StringComparison.OrdinalIgnoreCase));

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
                // An "ailleurs" row keeps the code it had before a transfer.
                Absence = d.Status is LineStatus.Absent or LineStatus.Ailleurs ? d.Absence : null,
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

    // Only the rows of the actor's plateformes change: a row he may not
    // touch must stay as stored (as the sheet opened, for a new sheet).
    private async Task CheckRowsAsync(Actor actor, AttendanceSheet scopeOf, AttendanceSheet? stored, AttendanceSheet sheet)
    {
        var rule = await RowsAsync(actor, scopeOf);
        var before = stored?.Lines.ToDictionary(l => l.EmployeeId) ?? new Dictionary<int, AttendanceLine>();
        var employees = (await staff.ListEmployeesAsync()).ToDictionary(e => e.Id);
        foreach (var l in sheet.Lines)
        {
            // "En renfort ailleurs" rows follow the other sheets, not the chef.
            if (l.Status == LineStatus.Ailleurs)
                continue;
            var old = before.GetValueOrDefault(l.EmployeeId)
                      ?? (!l.IsRenfort && employees.TryGetValue(l.EmployeeId, out var e) && e.PostingAt(sheet.ProductionDate) is { } p
                          ? NewLine(sheet, e, p, renfort: false, new())
                          : null);
            if (old != null && Same(old, l))
                continue;
            if (!rule.Allows(l) || old != null && !rule.Allows(old))
                throw new AccessDeniedException(
                    $"{l.FullName} travaille sur {rule.Where(old != null && !rule.Allows(old) ? old : l)} : hors de vos plateformes, " +
                    "son pointage revient au chef de conditionnement de cette plateforme.");
        }
        foreach (var o in before.Values.Where(o => sheet.Lines.All(l => l.EmployeeId != o.EmployeeId)))
            if (!rule.Allows(o))
                throw new AccessDeniedException($"{o.FullName} travaille sur {rule.Where(o)} : hors de vos plateformes, vous ne pouvez pas le retirer.");
    }

    private static bool Same(AttendanceLine a, AttendanceLine b)
        => a.Status == b.Status && a.Absence == b.Absence && a.EntryAt == b.EntryAt && a.ExitAt == b.ExitAt
           && a.MachineId == b.MachineId && a.LineId == b.LineId && a.ReplacesEmployeeId == b.ReplacesEmployeeId && a.ActingFrom == b.ActingFrom;

    private async Task CheckAsync(AttendanceSheet sheet, bool validating)
    {
        var problems = Problems(sheet, validating).Concat((await overtime.BookAsync(sheet.ProductionDate)).Problems(sheet)).ToList();
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
            else if (l.Status == LineStatus.Ailleurs && l.PermutationId == null)
            {
                // Over these hours, or moved to another poste of the same day.
                var away = others.Any(s => s.Lines.Any(x => x.EmployeeId == l.EmployeeId && x.Status == LineStatus.Present
                                                           && (x.EntryAt < sheet.End && x.ExitAt > sheet.Start || s.ProductionDate == sheet.ProductionDate)));
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
