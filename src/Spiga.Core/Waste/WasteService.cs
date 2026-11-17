using Spiga.Core.Audit;
using Spiga.Core.Plant;
using Spiga.Core.Security;

namespace Spiga.Core.Waste;

// Interfaces A1 / A2 of the specification: daily waste entries with a
// two-step check (entered, then validated by someone else). Each operation
// needs its permission at the entry's place (sector, machine, team on shift).
public sealed class WasteService(
    IWasteStore store, IPlantReferential plant, ISettingsStore settings, IAuditLog audit, TimeProvider clock, ClientInfo client)
{
    // Entries older than this need "Saisir au-delà de 7 jours".
    public const int LateEntryDays = 7;
    private const double MaxQuantityKg = 100_000;

    private static readonly string ViewEntries = Perm.View(Modules.SaisieDechets);

    private static readonly string[] DashboardViews =
        { Perm.View(Modules.VueEnsemble), Perm.View(Modules.Conditionnement), Perm.View(Modules.Dechets), ViewEntries };

    private DateTime Now => clock.GetLocalNow().DateTime;

    public Task<IReadOnlyList<WasteReason>> ReasonsAsync(WasteKind kind) => store.ListReasonsAsync(kind);

    // The machines / sectors the actor may enter waste for.
    public async Task<IReadOnlyList<PlantMachine>> MachinesFor(Actor actor)
        => (await plant.GetMachinesAsync()).Where(m => actor.Allows(Perm.DechetsSaisir, m.Sector, m.Id)).ToList();

    public IReadOnlyList<string> SectorsFor(Actor actor) => Sectors.All.Where(s => actor.Allows(Perm.DechetsSaisir, s)).ToList();

    public async Task<IReadOnlyList<WasteEntry>> ListAsync(Actor actor, WasteKind kind, DateTime productionDate, int? shiftId)
    {
        actor.Demand(ViewEntries);
        return (await store.ListAsync(kind, productionDate.Date, productionDate.Date))
            .Where(e => shiftId == null || e.ShiftId == shiftId)
            .Where(e => At(actor, ViewEntries, e))
            .OrderBy(e => e.ShiftId).ThenBy(e => e.CreatedAt)
            .ToList();
    }

    // Read-only totals for the dashboards (all statuses, flagged by caller).
    public async Task<IReadOnlyList<WasteEntry>> ForDashboardAsync(Actor actor, DateTime fromDate, DateTime toDate)
    {
        if (!DashboardViews.Any(actor.Can))
            throw new AccessDeniedException("Vos rôles ne permettent pas de consulter les déchets.");
        return (await store.ListAsync(null, fromDate.Date, toDate.Date))
            .Where(e => actor.AllowsAny(DashboardViews, e.Sector, e.MachineId, e.Team))
            .ToList();
    }

    public async Task<int> PendingValidationCountAsync(Actor actor, DateTime fromDate, DateTime toDate)
    {
        if (!actor.Can(Perm.DechetsValider))
            return 0;
        var policy = await Policy();
        return (await store.ListAsync(null, fromDate.Date, toDate.Date)).Count(e => CanValidate(actor, e, policy));
    }

    public async Task<WasteEntry> AddAsync(Actor actor, WasteDraft draft)
    {
        actor.Demand(Perm.DechetsSaisir);
        var entry = new WasteEntry { Kind = draft.Kind, Status = WasteStatus.Brouillon, CreatedBy = actor.Login, CreatedAt = Now };
        await Apply(actor, entry, draft, Perm.DechetsSaisir);
        entry.Id = await store.InsertAsync(entry);
        await Write(actor, AuditActions.EntryCreated, entry, "", null, entry.Describe());
        return entry;
    }

    // Own draft: "Saisir". Someone else's draft: "Modifier le brouillon d'un
    // autre". Validated entry: "Corriger", with a reason kept in the journal.
    public async Task UpdateAsync(Actor actor, long id, WasteDraft draft, string? correctionReason)
    {
        var entry = await Load(id);
        var permission = EditPermission(actor, entry);
        if (!At(actor, permission, entry))
            throw new AccessDeniedException(entry.Status == WasteStatus.Validee
                ? "Vos rôles ne permettent pas de corriger une saisie validée à cet endroit."
                : "Seul l'auteur de la saisie, ou un rôle autorisé à modifier les brouillons d'autrui, peut la modifier.");
        if (entry.Status == WasteStatus.Validee && string.IsNullOrWhiteSpace(correctionReason))
            throw new RuleViolationException("Une saisie validée ne peut être corrigée qu'avec un motif de correction.");

        var before = entry.Describe();
        await Apply(actor, entry, draft, permission);
        entry.UpdatedBy = actor.Login;
        entry.UpdatedAt = Now;
        await store.UpdateAsync(entry);
        await Write(actor, entry.Status == WasteStatus.Validee ? AuditActions.EntryCorrected : AuditActions.EntryUpdated,
            entry, correctionReason?.Trim() ?? "", before, entry.Describe());
    }

    public async Task ValidateAsync(Actor actor, long id)
    {
        actor.Demand(Perm.DechetsValider);
        var entry = await Load(id);
        if (entry.Status == WasteStatus.Validee)
            return;
        if (!At(actor, Perm.DechetsValider, entry))
            throw new AccessDeniedException("Cette saisie est hors du périmètre où vous pouvez valider.");
        if (!CanValidate(actor, entry, await Policy()))
            throw new RuleViolationException("Une saisie doit être validée par une autre personne que son auteur.");
        entry.Status = WasteStatus.Validee;
        entry.ValidatedBy = actor.Login;
        entry.ValidatedAt = Now;
        await store.UpdateAsync(entry);
        await Write(actor, AuditActions.EntryValidated, entry, "", null, entry.Describe());
    }

    public async Task DeleteAsync(Actor actor, long id)
    {
        var entry = await Load(id);
        if (entry.Status == WasteStatus.Validee)
            throw new RuleViolationException("Une saisie validée ne peut pas être supprimée : corrigez-la avec un motif.");
        if (!CanDelete(actor, entry))
            throw new AccessDeniedException("Seul l'auteur de la saisie, ou un rôle autorisé à modifier les brouillons d'autrui, peut la supprimer.");
        await store.DeleteAsync(id, actor.Login, Now);
        await Write(actor, AuditActions.EntryDeleted, entry, "", entry.Describe(), null);
    }

    public bool CanValidate(Actor actor, WasteEntry entry, SecurityPolicy? policy)
        => entry.Status == WasteStatus.Brouillon
           && At(actor, Perm.DechetsValider, entry)
           && !((policy?.RequireSecondValidator ?? true) && entry.CreatedBy == actor.Login);

    public bool CanEdit(Actor actor, WasteEntry entry) => At(actor, EditPermission(actor, entry), entry);

    public bool CanDelete(Actor actor, WasteEntry entry) => entry.Status == WasteStatus.Brouillon && CanEdit(actor, entry);

    private static string EditPermission(Actor actor, WasteEntry entry)
        => entry.Status == WasteStatus.Validee ? Perm.DechetsCorriger
            : entry.CreatedBy == actor.Login ? Perm.DechetsSaisir
            : Perm.DechetsAutrui;

    private static bool At(Actor actor, string permission, WasteEntry e) => actor.Allows(permission, e.Sector, e.MachineId, e.Team);

    private async Task<SecurityPolicy> Policy()
        => await settings.GetAsync<SecurityPolicy>(SettingKeys.SecurityPolicy) ?? new SecurityPolicy();

    private async Task<WasteEntry> Load(long id)
        => await store.GetAsync(id) ?? throw new RuleViolationException("Saisie introuvable (supprimée entre-temps ?).");

    // Checks the draft and writes it into the entry; `permission` must hold
    // at the new place too (an entry cannot be moved out of one's scope).
    private async Task Apply(Actor actor, WasteEntry entry, WasteDraft draft, string permission)
    {
        if (draft.Kind != entry.Kind)
            throw new RuleViolationException("Le type de déchet ne peut pas changer.");
        var date = draft.ProductionDate.Date;
        var today = Shifts.ProductionDate(Now);
        if (date > today)
            throw new RuleViolationException("La date de production ne peut pas être dans le futur.");
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
            sector = machine.Sector;
            machineId = machine.Id;
        }
        else
        {
            sector = Sectors.All.FirstOrDefault(s => s == draft.Sector) ?? throw new RuleViolationException("Choisissez le secteur.");
        }
        var team = plant.TeamOnShift(date, draft.ShiftId);
        var place = machineId ?? Sectors.Label(sector);
        if (!actor.Allows(permission, sector, machineId))
            throw new AccessDeniedException($"{place} est hors de votre périmètre.");
        if (!actor.Allows(permission, sector, machineId, team))
            throw new AccessDeniedException($"Ce poste a été travaillé par {team} : hors de votre périmètre.");
        if (date < today.AddDays(-LateEntryDays) && !actor.Allows(Perm.DechetsTardif, sector, machineId, team))
            throw new RuleViolationException($"Une saisie de plus de {LateEntryDays} jours demande le droit « Saisir au-delà de 7 jours ».");

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
