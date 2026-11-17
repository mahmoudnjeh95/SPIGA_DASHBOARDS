using Spiga.Core.Audit;
using Spiga.Core.Plant;
using Spiga.Core.Security;

namespace Spiga.Core.Waste;

// Interfaces A1 / A2 of the specification: daily waste entries with a
// two-step check (entered, then validated by someone else), the user's
// data scope enforced on every read and write.
public sealed class WasteService(
    IWasteStore store, IPlantReferential plant, ISettingsStore settings, IAuditLog audit, TimeProvider clock, ClientInfo client)
{
    // Entries older than this need the Valider right (late entries are a
    // supervisor's call, not an operator's).
    public const int LateEntryDays = 7;
    private const double MaxQuantityKg = 100_000;

    private DateTime Now => clock.GetLocalNow().DateTime;

    public Task<IReadOnlyList<WasteReason>> ReasonsAsync(WasteKind kind) => store.ListReasonsAsync(kind);

    // The machines the actor may enter waste for.
    public async Task<IReadOnlyList<PlantMachine>> MachinesFor(Actor actor)
        => (await plant.GetMachinesAsync()).Where(m => actor.Scope.AllowsMachine(m.Id, m.Sector)).ToList();

    public IReadOnlyList<string> SectorsFor(Actor actor) => Sectors.All.Where(actor.Scope.AllowsSector).ToList();

    public async Task<IReadOnlyList<WasteEntry>> ListAsync(Actor actor, WasteKind kind, DateTime productionDate, int? shiftId)
    {
        actor.Demand(Modules.SaisieDechets, AppAction.Voir);
        var machines = await plant.GetMachinesAsync();
        return (await store.ListAsync(kind, productionDate.Date, productionDate.Date))
            .Where(e => shiftId == null || e.ShiftId == shiftId)
            .Where(e => InScope(actor, e, machines))
            .OrderBy(e => e.ShiftId).ThenBy(e => e.CreatedAt)
            .ToList();
    }

    // Read-only totals for the dashboards (all statuses, flagged by caller).
    public async Task<IReadOnlyList<WasteEntry>> ForDashboardAsync(Actor actor, DateTime fromDate, DateTime toDate)
    {
        if (!actor.Permissions.CanSeeAny(new[] { Modules.VueEnsemble, Modules.Conditionnement, Modules.Dechets, Modules.SaisieDechets }))
            throw new AccessDeniedException("Votre rôle ne permet pas de consulter les déchets.");
        var machines = await plant.GetMachinesAsync();
        return (await store.ListAsync(null, fromDate.Date, toDate.Date)).Where(e => InScope(actor, e, machines)).ToList();
    }

    public async Task<int> PendingValidationCountAsync(Actor actor, DateTime fromDate, DateTime toDate)
    {
        if (!actor.Can(Modules.SaisieDechets, AppAction.Valider))
            return 0;
        var machines = await plant.GetMachinesAsync();
        return (await store.ListAsync(null, fromDate.Date, toDate.Date))
            .Count(e => e.Status == WasteStatus.Brouillon && InScope(actor, e, machines) && CanValidate(actor, e, null));
    }

    public async Task<WasteEntry> AddAsync(Actor actor, WasteDraft draft)
    {
        actor.Demand(Modules.SaisieDechets, AppAction.Saisir);
        var entry = new WasteEntry { Kind = draft.Kind, Status = WasteStatus.Brouillon, CreatedBy = actor.Login, CreatedAt = Now };
        await Apply(actor, entry, draft);
        entry.Id = await store.InsertAsync(entry);
        await Write(actor, AuditActions.EntryCreated, entry, "", null, entry.Describe());
        return entry;
    }

    // Draft: its author, or a validator, may edit it. Validated: only a
    // validator, with a reason that goes to the audit trail.
    public async Task UpdateAsync(Actor actor, long id, WasteDraft draft, string? correctionReason)
    {
        actor.Demand(Modules.SaisieDechets, AppAction.Saisir);
        var entry = await Load(actor, id);
        var before = entry.Describe();
        if (entry.Status == WasteStatus.Validee)
        {
            actor.Demand(Modules.SaisieDechets, AppAction.Valider);
            if (string.IsNullOrWhiteSpace(correctionReason))
                throw new RuleViolationException("Une saisie validée ne peut être corrigée qu'avec un motif de correction.");
        }
        else if (entry.CreatedBy != actor.Login && !actor.Can(Modules.SaisieDechets, AppAction.Valider))
        {
            throw new AccessDeniedException("Seul l'auteur de la saisie ou un validateur peut la modifier.");
        }

        await Apply(actor, entry, draft);
        entry.UpdatedBy = actor.Login;
        entry.UpdatedAt = Now;
        await store.UpdateAsync(entry);
        await Write(actor, entry.Status == WasteStatus.Validee ? AuditActions.EntryCorrected : AuditActions.EntryUpdated,
            entry, correctionReason?.Trim() ?? "", before, entry.Describe());
    }

    public async Task ValidateAsync(Actor actor, long id)
    {
        actor.Demand(Modules.SaisieDechets, AppAction.Valider);
        var entry = await Load(actor, id);
        if (entry.Status == WasteStatus.Validee)
            return;
        var policy = await settings.GetAsync<SecurityPolicy>(SettingKeys.SecurityPolicy) ?? new SecurityPolicy();
        if (!CanValidate(actor, entry, policy))
            throw new RuleViolationException("Une saisie doit être validée par une autre personne que son auteur.");
        entry.Status = WasteStatus.Validee;
        entry.ValidatedBy = actor.Login;
        entry.ValidatedAt = Now;
        await store.UpdateAsync(entry);
        await Write(actor, AuditActions.EntryValidated, entry, "", null, entry.Describe());
    }

    public async Task DeleteAsync(Actor actor, long id)
    {
        actor.Demand(Modules.SaisieDechets, AppAction.Saisir);
        var entry = await Load(actor, id);
        if (entry.Status == WasteStatus.Validee)
            throw new RuleViolationException("Une saisie validée ne peut pas être supprimée : corrigez-la avec un motif.");
        if (entry.CreatedBy != actor.Login && !actor.Can(Modules.SaisieDechets, AppAction.Valider))
            throw new AccessDeniedException("Seul l'auteur de la saisie ou un validateur peut la supprimer.");
        await store.DeleteAsync(id, actor.Login, Now);
        await Write(actor, AuditActions.EntryDeleted, entry, "", entry.Describe(), null);
    }

    public bool CanValidate(Actor actor, WasteEntry entry, SecurityPolicy? policy)
        => actor.Can(Modules.SaisieDechets, AppAction.Valider)
           && entry.Status == WasteStatus.Brouillon
           && !((policy?.RequireSecondValidator ?? true) && entry.CreatedBy == actor.Login);

    public bool CanEdit(Actor actor, WasteEntry entry)
        => entry.Status == WasteStatus.Brouillon
            ? actor.Can(Modules.SaisieDechets, AppAction.Saisir) && (entry.CreatedBy == actor.Login || actor.Can(Modules.SaisieDechets, AppAction.Valider))
            : actor.Can(Modules.SaisieDechets, AppAction.Valider);

    private async Task<WasteEntry> Load(Actor actor, long id)
    {
        var entry = await store.GetAsync(id) ?? throw new RuleViolationException("Saisie introuvable (supprimée entre-temps ?).");
        if (!InScope(actor, entry, await plant.GetMachinesAsync()))
            throw new AccessDeniedException("Cette saisie est hors de votre périmètre.");
        return entry;
    }

    private async Task Apply(Actor actor, WasteEntry entry, WasteDraft draft)
    {
        if (draft.Kind != entry.Kind)
            throw new RuleViolationException("Le type de déchet ne peut pas changer.");
        var date = draft.ProductionDate.Date;
        var today = Shifts.ProductionDate(Now);
        if (date > today)
            throw new RuleViolationException("La date de production ne peut pas être dans le futur.");
        if (date < today.AddDays(-LateEntryDays) && !actor.Can(Modules.SaisieDechets, AppAction.Valider))
            throw new RuleViolationException($"Une saisie de plus de {LateEntryDays} jours doit être faite par un validateur.");
        if (!Shifts.All.Contains(draft.ShiftId))
            throw new RuleViolationException("Choisissez le poste.");
        if (!WasteUnits.All.Contains(draft.Unit))
            throw new RuleViolationException("Unité inconnue.");
        if (!(draft.Quantity > 0) || WasteUnits.ToKg(draft.Quantity, draft.Unit) > MaxQuantityKg)
            throw new RuleViolationException("La quantité doit être supérieure à 0 et inférieure à 100 t.");
        var reasons = await store.ListReasonsAsync(draft.Kind);
        if (string.IsNullOrWhiteSpace(draft.Reason) || !reasons.Any(r => r.Label == draft.Reason))
            throw new RuleViolationException("Choisissez un motif dans la liste.");
        if ((draft.Comment ?? "").Length > 500)
            throw new RuleViolationException("Le commentaire est limité à 500 caractères.");

        string sector;
        string? machineId = null;
        if (draft.Kind == WasteKind.Emballage)
        {
            var machine = (await plant.GetMachinesAsync()).FirstOrDefault(m => m.Id == draft.MachineId)
                ?? throw new RuleViolationException("Choisissez la machine.");
            if (!actor.Scope.AllowsMachine(machine.Id, machine.Sector))
                throw new AccessDeniedException($"La machine {machine.Id} est hors de votre périmètre.");
            sector = machine.Sector;
            machineId = machine.Id;
        }
        else
        {
            sector = Sectors.All.FirstOrDefault(s => s == draft.Sector) ?? throw new RuleViolationException("Choisissez le secteur.");
            if (!actor.Scope.AllowsSector(sector))
                throw new AccessDeniedException($"Le secteur {Sectors.Label(sector)} est hors de votre périmètre.");
        }

        var team = plant.TeamOnShift(date, draft.ShiftId);
        if (!actor.Scope.AllowsTeam(team))
            throw new AccessDeniedException($"Ce poste a été travaillé par {team} : hors de votre périmètre ({actor.Scope.Team}).");

        entry.ProductionDate = date;
        entry.ShiftId = draft.ShiftId;
        entry.Team = team;
        entry.Sector = sector;
        entry.MachineId = machineId;
        entry.Quantity = Math.Round(draft.Quantity, 3);
        entry.Unit = draft.Unit;
        entry.Reason = draft.Reason;
        entry.Comment = (draft.Comment ?? "").Trim();
    }

    private static bool InScope(Actor actor, WasteEntry e, IReadOnlyList<PlantMachine> machines)
    {
        if (!actor.Scope.AllowsTeam(e.Team))
            return false;
        return e.MachineId == null ? actor.Scope.AllowsSector(e.Sector) : actor.Scope.AllowsMachine(e.MachineId, e.Sector);
    }

    private Task Write(Actor actor, string action, WasteEntry entry, string details, string? oldValue, string? newValue)
        => audit.WriteAsync(new AuditEntry
        {
            At = Now,
            UserLogin = actor.Login,
            Module = Modules.SaisieDechets,
            Action = action,
            Target = $"{(entry.Kind == WasteKind.Emballage ? "Déchet emballage" : "Déchet produit")} n°{entry.Id}",
            Details = details,
            OldValue = oldValue,
            NewValue = newValue,
            Workstation = client.Workstation,
        });
}
