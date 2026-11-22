using Spiga.Core.Audit;
using Spiga.Core.Plant;
using Spiga.Core.Security;

namespace Spiga.Core.Staff;

// Personnel et lignes (Référentiels): the employees of the packaging
// service with their team, function and post, and the lignes of the chefs
// de ligne. Read by everyone who sees the pointage; changed with
// "Modifier les référentiels". Every change goes to the audit trail.
public sealed class StaffService(IStaffStore store, IPlantReferential plant, IAuditLog audit, TimeProvider clock, ClientInfo client)
{
    private const int MaxName = 80;

    private DateTime Now => clock.GetLocalNow().DateTime;

    private static readonly string[] Readers =
    {
        Perm.View(Modules.Referentiels), Perm.PointageVoir, Perm.View(Modules.Rh), Perm.View(Modules.RhNominatif),
    };

    public bool CanEdit(Actor actor) => actor.Can(Perm.ReferentielsSaisir);

    public async Task<IReadOnlyList<Employee>> EmployeesAsync(Actor actor)
    {
        if (!Readers.Any(actor.Can))
            throw new AccessDeniedException("Vos rôles ne permettent pas de consulter le personnel.");
        return (await store.ListEmployeesAsync()).OrderBy(e => e.FullName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<ProductionLine>> LinesAsync(Actor actor)
    {
        if (!Readers.Any(actor.Can))
            throw new AccessDeniedException("Vos rôles ne permettent pas de consulter les lignes.");
        return (await store.ListLinesAsync()).OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---------- employees ----------

    public async Task<Employee> AddEmployeeAsync(Actor actor, EmployeeDraft draft, PostingDraft posting)
    {
        actor.Demand(Perm.ReferentielsSaisir);
        var all = await store.ListEmployeesAsync();
        var employee = new Employee { IsActive = true };
        ApplyDraft(employee, draft, all);
        var row = await CheckPosting(posting, 0);
        row.CreatedBy = actor.Login;
        row.CreatedAt = Now;
        employee.Id = await store.InsertEmployeeAsync(employee, row);
        row.EmployeeId = employee.Id;
        employee.Postings.Add(row);
        await Write(actor, AuditActions.EmployeeCreated, employee.FullName, "", null, await DescribeAsync(row));
        return employee;
    }

    // inactiveFrom: the day he left (default today), when made inactive.
    // He stays on the sheets of the days before.
    public async Task UpdateEmployeeAsync(Actor actor, int id, EmployeeDraft draft, bool isActive, DateTime? inactiveFrom = null)
    {
        actor.Demand(Perm.ReferentielsSaisir);
        var employee = await Load(id);
        var before = State(employee);
        ApplyDraft(employee, draft, (await store.ListEmployeesAsync()).Where(e => e.Id != id).ToList());
        if (isActive)
            employee.InactiveFrom = null;
        else
        {
            var left = (inactiveFrom ?? (employee.IsActive ? null : employee.InactiveFrom) ?? Shifts.ProductionDate(Now)).Date;
            if (employee.Postings.Count > 0 && left < employee.Postings.Min(p => p.ValidFrom).Date)
                throw new RuleViolationException($"{employee.FullName} est employé depuis le {employee.Postings.Min(p => p.ValidFrom):dd/MM/yyyy} : il ne peut pas être parti avant.");
            employee.InactiveFrom = left;
        }
        employee.IsActive = isActive;
        await store.UpdateEmployeeAsync(employee);
        await Write(actor, AuditActions.EmployeeUpdated, employee.FullName, "", before, State(employee));
    }

    private static string State(Employee e)
        => $"{e.FullName} · {e.Matricule} · " + (e.IsActive ? "actif" : e.InactiveFrom is { } left ? $"inactif depuis le {left:dd/MM/yyyy}" : "inactif");

    // A transfer, a promotion or a new post, from a date on. The sheets
    // already pointed keep what they recorded.
    public async Task SavePostingAsync(Actor actor, int employeeId, PostingDraft draft)
    {
        actor.Demand(Perm.ReferentielsSaisir);
        var employee = await Load(employeeId);
        var row = await CheckPosting(draft, employeeId);
        row.EmployeeId = employeeId;
        row.CreatedBy = actor.Login;
        row.CreatedAt = Now;
        var before = employee.Postings.FirstOrDefault(p => p.ValidFrom.Date == row.ValidFrom.Date);
        await store.SavePostingAsync(row);
        await Write(actor, AuditActions.PostingSaved, employee.FullName, "",
            before == null ? null : await DescribeAsync(before), await DescribeAsync(row));
    }

    public async Task DeletePostingAsync(Actor actor, int employeeId, long postingId)
    {
        actor.Demand(Perm.ReferentielsSaisir);
        var employee = await Load(employeeId);
        var row = employee.Postings.FirstOrDefault(p => p.Id == postingId)
                  ?? throw new RuleViolationException("Affectation introuvable (supprimée entre-temps ?).");
        if (employee.Postings.Count == 1)
            throw new RuleViolationException("C'est la seule affectation de cet employé : modifiez-la, ou rendez l'employé inactif.");
        await store.DeletePostingAsync(postingId);
        await Write(actor, AuditActions.PostingDeleted, employee.FullName, "", await DescribeAsync(row), null);
    }

    // ---------- import (rows copied from Excel) ----------

    public sealed record ImportRow(int LineNo, string Raw, EmployeeDraft? Employee, PostingDraft? Posting, string? Error)
    {
        public bool Ok => Error == null;
    }

    // One employee per line: Nom et prénom, Matricule, Équipe, Fonction,
    // Machine ou ligne; columns separated by tabs (a copy from Excel) or
    // semicolons. A first line of titles is skipped. Nothing is saved:
    // the screen shows each row, OK or with what is wrong.
    public async Task<IReadOnlyList<ImportRow>> ParseImportAsync(string text, DateTime validFrom)
    {
        var existing = (await store.ListEmployeesAsync()).Select(e => e.FullName).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var machines = await plant.GetMachinesAsync();
        var lines = (await store.ListLinesAsync()).Where(l => l.IsActive).ToList();
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var rows = new List<ImportRow>();
        var no = 0;
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            no++;
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var cells = raw.Split(raw.Contains('\t') ? '\t' : ';').Select(c => c.Trim().Trim('"')).ToArray();
            string Cell(int i) => i < cells.Length ? cells[i] : "";
            if (rows.Count == 0 && no <= 1 && Fold(Cell(0)).StartsWith("nom", StringComparison.Ordinal))
                continue;
            ImportRow Bad(string why) => new(no, raw, null, null, why);
            var name = string.Join(' ', Cell(0).Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (name.Length < 3)
            {
                rows.Add(Bad("nom manquant"));
                continue;
            }
            if (existing.Contains(name) || !seen.Add(name))
            {
                rows.Add(Bad("existe déjà"));
                continue;
            }
            var team = TeamOf(Cell(2));
            if (team == null)
            {
                rows.Add(Bad($"équipe « {Cell(2)} » inconnue ({string.Join(", ", plant.Teams)})"));
                continue;
            }
            if (FunctionOf(Cell(3)) is not { } function)
            {
                rows.Add(Bad($"fonction « {Cell(3)} » inconnue (chef de conditionnement, chef de ligne, conducteur, emballage, nettoyage)"));
                continue;
            }
            string? machine = null;
            int? lineId = null;
            if (StaffFunctions.HoldsMachine(function))
            {
                // The name shown on the screens (Paramètres > Nom affiché) or the id.
                machine = (machines.FirstOrDefault(m => string.Equals(m.Name, Cell(4), StringComparison.OrdinalIgnoreCase))
                           ?? machines.FirstOrDefault(m => string.Equals(m.Id, Cell(4), StringComparison.OrdinalIgnoreCase)))?.Id;
                if (machine == null)
                {
                    rows.Add(Bad($"machine « {Cell(4)} » inconnue"));
                    continue;
                }
            }
            else if (StaffFunctions.HoldsLine(function))
            {
                lineId = lines.FirstOrDefault(l => Fold(l.Name) == Fold(Cell(4)))?.Id;
                if (lineId == null)
                {
                    rows.Add(Bad($"ligne « {Cell(4)} » inconnue ({string.Join(", ", lines.Select(l => l.Name))})"));
                    continue;
                }
            }
            rows.Add(new ImportRow(no, raw, new EmployeeDraft(name, Cell(1)), new PostingDraft(validFrom, team, function, machine, lineId), null));
        }
        return rows;
    }

    // Adds the rows without error; returns how many were added.
    public async Task<int> ImportAsync(Actor actor, IEnumerable<ImportRow> rows)
    {
        actor.Demand(Perm.ReferentielsSaisir);
        var added = 0;
        foreach (var r in rows.Where(r => r.Ok))
        {
            await AddEmployeeAsync(actor, r.Employee!, r.Posting!);
            added++;
        }
        return added;
    }

    // "D", "Equipe D", "équipe d" -> "Equipe D".
    private string? TeamOf(string text)
    {
        var t = Fold(text);
        return plant.Teams.FirstOrDefault(x => Fold(x) == t || Fold(x).EndsWith(" " + t, StringComparison.Ordinal));
    }

    private static StaffFunction? FunctionOf(string text)
    {
        var t = Fold(text);
        if (t.Length == 0) return null;
        if (t.Contains("cond") && t.Contains("chef") || t == "cc") return StaffFunction.ChefConditionnement;
        if (t.Contains("chef")) return StaffFunction.ChefLigne;
        if (t.StartsWith("conduc", StringComparison.Ordinal)) return StaffFunction.Conducteur;
        if (t.StartsWith("emball", StringComparison.Ordinal)) return StaffFunction.Emballage;
        if (t.StartsWith("nett", StringComparison.Ordinal)) return StaffFunction.Nettoyage;
        return null;
    }

    // Lower case, no accents, single spaces.
    private static string Fold(string text)
    {
        var d = (text ?? "").Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var chars = d.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);
        return string.Join(' ', new string(chars.ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // ---------- lignes ----------

    public async Task<ProductionLine> SaveLineAsync(Actor actor, int id, string name, IReadOnlyCollection<string> machines, bool isActive)
    {
        actor.Demand(Perm.ReferentielsSaisir);
        var lines = await store.ListLinesAsync();
        var line = id == 0 ? new ProductionLine() : lines.FirstOrDefault(l => l.Id == id)
            ?? throw new RuleViolationException("Ligne introuvable (supprimée entre-temps ?).");
        var before = id == 0 ? null : $"{line.Name} : {string.Join(", ", line.Machines)}";
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 40)
            throw new RuleViolationException("Donnez un nom à la ligne (40 caractères au plus).");
        if (lines.Any(l => l.Id != id && string.Equals(l.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new RuleViolationException($"Une ligne s'appelle déjà « {name} ».");
        var plantMachines = await plant.GetMachinesAsync();
        var known = plantMachines.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string NameOf(string machineId) => plantMachines.FirstOrDefault(m => string.Equals(m.Id, machineId, StringComparison.OrdinalIgnoreCase))?.Name ?? machineId;
        var chosen = machines.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (chosen.Count == 0)
            throw new RuleViolationException("Choisissez au moins une machine.");
        if (chosen.FirstOrDefault(m => !known.Contains(m)) is { } unknown)
            throw new RuleViolationException($"Machine inconnue : {unknown}.");
        foreach (var other in lines.Where(l => l.Id != id && l.IsActive))
            if (chosen.FirstOrDefault(m => other.Machines.Contains(m, StringComparer.OrdinalIgnoreCase)) is { } taken)
                throw new RuleViolationException($"{NameOf(taken)} est déjà dans la ligne « {other.Name} » : une machine n'est que dans une ligne.");
        line.Name = name;
        line.Machines = chosen.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        line.IsActive = isActive;
        line.Id = await store.SaveLineAsync(line);
        await Write(actor, AuditActions.LineSaved, $"Ligne {line.Name}", "", before, $"{line.Name} : {string.Join(", ", line.Machines)}");
        return line;
    }

    public async Task DeleteLineAsync(Actor actor, int id)
    {
        actor.Demand(Perm.ReferentielsSaisir);
        var line = (await store.ListLinesAsync()).FirstOrDefault(l => l.Id == id)
                   ?? throw new RuleViolationException("Ligne introuvable (supprimée entre-temps ?).");
        if ((await store.ListEmployeesAsync()).SelectMany(e => e.Postings).Any(p => p.LineId == id))
            throw new RuleViolationException("Des chefs de ligne y sont ou y ont été affectés : rendez-la inactive au lieu de la supprimer.");
        await store.DeleteLineAsync(id);
        await Write(actor, AuditActions.LineDeleted, $"Ligne {line.Name}", "", string.Join(", ", line.Machines), null);
    }

    // "Equipe B · Conducteur de ligne · CC02 · dès le 01/12/2026"
    public async Task<string> DescribeAsync(EmployeePosting p)
    {
        var post = p.MachineId ?? (p.LineId is { } l ? ProductionLine.LabelOf((await store.ListLinesAsync()).FirstOrDefault(x => x.Id == l)?.Name ?? l.ToString()) : null);
        return $"{p.Team} · {StaffFunctions.Label(p.Function)}{(post == null ? "" : " · " + post)} · dès le {p.ValidFrom:dd/MM/yyyy}";
    }

    // ---------- internals ----------

    private static void ApplyDraft(Employee employee, EmployeeDraft draft, IReadOnlyList<Employee> others)
    {
        var name = string.Join(' ', (draft.FullName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length < 3 || name.Length > MaxName)
            throw new RuleViolationException($"Indiquez le nom et le prénom (3 à {MaxName} caractères).");
        var matricule = (draft.Matricule ?? "").Trim();
        if (matricule.Length > 20)
            throw new RuleViolationException("Le matricule est limité à 20 caractères.");
        if (others.Any(o => string.Equals(o.FullName, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new RuleViolationException($"« {name} » existe déjà : ajoutez le prénom complet ou le matricule pour les distinguer.");
        if (matricule.Length > 0 && others.Any(o => string.Equals(o.Matricule, matricule, StringComparison.OrdinalIgnoreCase)))
            throw new RuleViolationException($"Le matricule {matricule} est déjà donné à un autre employé.");
        employee.FullName = name;
        employee.Matricule = matricule;
    }

    private async Task<EmployeePosting> CheckPosting(PostingDraft d, int employeeId)
    {
        if (!plant.Teams.Contains(d.Team ?? "", StringComparer.OrdinalIgnoreCase))
            throw new RuleViolationException("Choisissez l'équipe.");
        var team = plant.Teams.First(t => string.Equals(t, d.Team, StringComparison.OrdinalIgnoreCase));
        if (!StaffFunctions.All.Contains(d.Function))
            throw new RuleViolationException("Choisissez la fonction.");
        if (d.ValidFrom.Year < 2020 || d.ValidFrom.Date > Now.Date.AddYears(1))
            throw new RuleViolationException("Date d'effet invalide.");
        string? machine = null;
        int? lineId = null;
        if (StaffFunctions.HoldsMachine(d.Function))
        {
            machine = (await plant.GetMachinesAsync()).FirstOrDefault(m => string.Equals(m.Id, d.MachineId, StringComparison.OrdinalIgnoreCase))?.Id
                      ?? throw new RuleViolationException($"Un poste « {StaffFunctions.Label(d.Function)} » tient une machine : choisissez-la.");
        }
        else if (StaffFunctions.HoldsLine(d.Function))
        {
            var line = (await store.ListLinesAsync()).FirstOrDefault(l => l.Id == d.LineId && l.IsActive)
                       ?? throw new RuleViolationException("Un chef de ligne tient une ligne : choisissez-la (Personnel et lignes › Lignes).");
            lineId = line.Id;
        }
        return new EmployeePosting
        {
            EmployeeId = employeeId, ValidFrom = d.ValidFrom.Date, Team = team, Function = d.Function, MachineId = machine, LineId = lineId,
        };
    }

    private async Task<Employee> Load(int id)
        => await store.GetEmployeeAsync(id) ?? throw new RuleViolationException("Employé introuvable (supprimé entre-temps ?).");

    private Task Write(Actor actor, string action, string target, string details, string? oldValue, string? newValue)
        => audit.WriteAsync(new AuditEntry
        {
            At = Now, UserLogin = actor.Login, Module = Modules.Referentiels, Action = action, Target = target, Details = details,
            OldValue = oldValue, NewValue = newValue, Workstation = client.Workstation,
        });
}
