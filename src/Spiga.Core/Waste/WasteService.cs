using Spiga.Core.Audit;
using Spiga.Core.Plant;
using Spiga.Core.Security;

namespace Spiga.Core.Waste;

// Interfaces A1 / A2 of the specification: daily waste entries with a
// two-step check. The author enters (Brouillon); a validator validates it
// (Validee, locked) or sends it back with a comment (Renvoyee), the author
// corrects and submits it again. A validated entry can still be corrected
// or cancelled (Annulee), with a reason, by "Corriger ou annuler".
//
// Every operation needs its permission at the entry's place (sector,
// machine, team on shift). Product waste is entered per sector, so it needs
// a scope over the whole sector. Every action carries the version of the
// entry the user saw and writes a revision: nothing is validated, sent back
// or changed on the strength of a screen that was out of date.
public sealed class WasteService(
    IWasteStore store, IPlantReferential plant, ISettingsStore settings, IAuditLog audit, TimeProvider clock, ClientInfo client)
{
    // Entries older than this need "Saisir au-delà de 7 jours".
    public const int LateEntryDays = 7;

    // How far back the "renvoyées" and "à valider" lists look by default.
    public const int OpenLookbackDays = 31;
    private const double MaxQuantityKg = 100_000;
    private const int MaxNoteLength = 300;

    private static readonly string ViewEntries = Perm.View(Modules.SaisieDechets);

    private static readonly string[] DashboardViews =
        { Perm.View(Modules.VueEnsemble), Perm.View(Modules.Conditionnement), Perm.View(Modules.Dechets), ViewEntries };

    private DateTime Now => clock.GetLocalNow().DateTime;

    // ---------- places and postes ----------

    public static string EntryPermission(WasteKind kind) => kind == WasteKind.Emballage ? Perm.DechetsSaisir : Perm.DechetsProduit;

    // The permission at the entry's place. Product waste belongs to the whole
    // sector: a scope narrowed to some machines does not cover it.
    public static bool Covers(Actor actor, string permission, WasteEntry e)
        => e.Kind == WasteKind.Produit
            ? actor.AllowsSectorWide(permission, e.Sector, e.Team)
            : actor.Allows(permission, e.Sector, e.MachineId, e.Team);

    public bool CanEnter(Actor actor, WasteKind kind) => actor.Can(EntryPermission(kind));

    public Task<IReadOnlyList<WasteReason>> ReasonsAsync(WasteKind kind) => store.ListReasonsAsync(kind);

    // The machines / sectors the actor may enter waste for.
    public async Task<IReadOnlyList<PlantMachine>> MachinesFor(Actor actor)
        => (await plant.GetMachinesAsync()).Where(m => actor.Allows(Perm.DechetsSaisir, m.Sector, m.Id)).ToList();

    public IReadOnlyList<string> SectorsFor(Actor actor) => Sectors.All.Where(s => actor.AllowsSectorWide(Perm.DechetsProduit, s)).ToList();

    // Without "Saisir pour un autre poste", the date and poste are not chosen.
    public bool CanChoosePoste(Actor actor) => actor.Can(Perm.DechetsAutresPostes);

    // The poste a user without "Saisir pour un autre poste" enters for: the
    // poste in progress if their team works it, else their team's last poste
    // of the past 24 hours (the morning team entering its waste at 14:10).
    // null = their team has not worked in the last 24 hours.
    public (DateTime Date, int ShiftId)? CurrentPoste(Actor actor)
    {
        var date = Shifts.ProductionDate(Now);
        var shift = Shifts.At(Now);
        for (var i = 0; i < 3; i++)
        {
            var team = plant.TeamOnShift(date, shift);
            if (actor.Team == null || string.IsNullOrEmpty(team) || string.Equals(team, actor.Team, StringComparison.OrdinalIgnoreCase))
                return (date, shift);
            (date, shift) = shift switch { 1 => (date.AddDays(-1), 3), 2 => (date, 1), _ => (date, 2) };
        }
        return null;
    }

    // ---------- lists ----------

    // The entries of one poste (all statuses, the cancelled ones included).
    public async Task<IReadOnlyList<WasteEntry>> ListAsync(Actor actor, WasteKind kind, DateTime productionDate, int? shiftId)
    {
        actor.Demand(ViewEntries);
        return (await store.ListAsync(kind, productionDate.Date, productionDate.Date))
            .Where(e => shiftId == null || e.ShiftId == shiftId)
            .Where(e => Covers(actor, ViewEntries, e))
            .OrderBy(e => e.ShiftId).ThenBy(e => e.CreatedAt)
            .ToList();
    }

    // The actor's own entries sent back to them, whatever the date or poste.
    public async Task<IReadOnlyList<WasteEntry>> ReturnedToAsync(Actor actor)
    {
        var today = Shifts.ProductionDate(Now);
        return (await store.ListAsync(null, today.AddDays(-OpenLookbackDays), today))
            .Where(e => e.Status == WasteStatus.Renvoyee && e.CreatedBy == actor.Login)
            .OrderBy(e => e.ProductionDate).ThenBy(e => e.ShiftId)
            .ToList();
    }

    // Open entries (to validate, or sent back) where the actor validates.
    public async Task<IReadOnlyList<WasteEntry>> OpenAsync(Actor actor, DateTime fromDate, DateTime toDate)
    {
        actor.Demand(Perm.DechetsValider);
        return (await store.ListAsync(null, fromDate.Date, toDate.Date))
            .Where(e => e.IsOpen && Covers(actor, Perm.DechetsValider, e))
            .OrderBy(e => e.ProductionDate).ThenBy(e => e.ShiftId).ThenBy(e => e.CreatedAt)
            .ToList();
    }

    // Read-only totals for the dashboards: every entry that counts (not
    // cancelled); the caller flags those not validated yet.
    public async Task<IReadOnlyList<WasteEntry>> ForDashboardAsync(Actor actor, DateTime fromDate, DateTime toDate)
    {
        if (!DashboardViews.Any(actor.Can))
            throw new AccessDeniedException("Vos rôles ne permettent pas de consulter les déchets.");
        return (await store.ListAsync(null, fromDate.Date, toDate.Date))
            .Where(e => e.Counts && DashboardViews.Any(p => Covers(actor, p, e)))
            .ToList();
    }

    public async Task<int> PendingValidationCountAsync(Actor actor, DateTime fromDate, DateTime toDate)
    {
        if (!actor.Can(Perm.DechetsValider))
            return 0;
        var policy = await Policy();
        return (await store.ListAsync(null, fromDate.Date, toDate.Date)).Count(e => CanValidate(actor, e, policy));
    }

    // Every state of the entry, oldest first. Its author and whoever may see
    // the entries at its place can read it.
    public async Task<IReadOnlyList<WasteRevision>> HistoryAsync(Actor actor, long id)
    {
        var entry = await Load(id);
        if (entry.CreatedBy != actor.Login && !Covers(actor, ViewEntries, entry))
            throw new AccessDeniedException("Cette saisie est hors de votre périmètre.");
        return await store.ListRevisionsAsync(id);
    }

    // ---------- actions ----------

    public async Task<WasteEntry> AddAsync(Actor actor, WasteDraft draft)
    {
        var permission = EntryPermission(draft.Kind);
        actor.Demand(permission);
        var entry = new WasteEntry { Kind = draft.Kind, Status = WasteStatus.Brouillon, CreatedBy = actor.Login, CreatedAt = Now, Version = 1 };
        await Apply(actor, entry, draft, permission, isNew: true);
        entry.Id = await store.InsertAsync(entry, Revision(actor, entry, AuditActions.EntryCreated, ""));
        await Write(actor, AuditActions.EntryCreated, entry, "", null, entry.Describe());
        return entry;
    }

    // Own draft: the entry right. Someone else's draft: "Modifier le
    // brouillon d'un autre". Validated: "Corriger", with a reason. A draft
    // sent back becomes a draft again: submitted anew.
    public async Task UpdateAsync(Actor actor, long id, int version, WasteDraft draft, string? correctionReason)
    {
        var entry = await Load(id);
        CheckVersion(entry, version);
        if (entry.Status == WasteStatus.Annulee)
            throw new RuleViolationException("Une saisie annulée ne peut plus être modifiée.");
        var permission = EditPermission(actor, entry);
        if (!Covers(actor, permission, entry))
            throw new AccessDeniedException(entry.Status == WasteStatus.Validee
                ? "Vos rôles ne permettent pas de corriger une saisie validée à cet endroit."
                : "Seul l'auteur de la saisie, ou un rôle autorisé à modifier les brouillons d'autrui, peut la modifier.");
        var reason = correctionReason?.Trim() ?? "";
        if (entry.Status == WasteStatus.Validee && reason.Length == 0)
            throw new RuleViolationException("Une saisie validée ne peut être corrigée qu'avec un motif de correction.");
        if (reason.Length > MaxNoteLength)
            throw new RuleViolationException($"Le motif est limité à {MaxNoteLength} caractères.");

        var before = entry.Describe();
        var action = entry.Status switch
        {
            WasteStatus.Validee => AuditActions.EntryCorrected,
            WasteStatus.Renvoyee => AuditActions.EntryResubmitted,
            _ => AuditActions.EntryUpdated,
        };
        await Apply(actor, entry, draft, permission, isNew: false);
        if (entry.Status == WasteStatus.Renvoyee)
            entry.Status = WasteStatus.Brouillon;
        entry.UpdatedBy = actor.Login;
        entry.UpdatedAt = Now;
        await Save(actor, entry, version, action, reason);
        await Write(actor, action, entry, reason, before, entry.Describe());
    }

    public async Task ValidateAsync(Actor actor, long id, int version)
    {
        actor.Demand(Perm.DechetsValider);
        var entry = await Load(id);
        if (entry.Status == WasteStatus.Validee && entry.Version == version)
            return;
        CheckVersion(entry, version);
        if (entry.Status == WasteStatus.Renvoyee)
            throw new RuleViolationException("Cette saisie a été renvoyée à son auteur : elle se valide quand il l'a soumise à nouveau.");
        if (entry.Status != WasteStatus.Brouillon)
            throw new RuleViolationException("Seule une saisie à valider peut être validée.");
        if (!Covers(actor, Perm.DechetsValider, entry))
            throw new AccessDeniedException("Cette saisie est hors du périmètre où vous pouvez valider.");
        if (await SecondPersonRule(actor, entry) is { } refusal)
            throw new RuleViolationException(refusal);
        entry.Status = WasteStatus.Validee;
        entry.ValidatedBy = actor.Login;
        entry.ValidatedAt = Now;
        await Save(actor, entry, version, AuditActions.EntryValidated, "");
        await Write(actor, AuditActions.EntryValidated, entry, "", null, entry.Describe());
    }

    // Sends a draft back to its author with what to correct.
    public async Task ReturnAsync(Actor actor, long id, int version, string comment)
    {
        actor.Demand(Perm.DechetsValider);
        var entry = await Load(id);
        CheckVersion(entry, version);
        if (entry.Status != WasteStatus.Brouillon)
            throw new RuleViolationException("Seule une saisie à valider peut être renvoyée.");
        if (!Covers(actor, Perm.DechetsValider, entry))
            throw new AccessDeniedException("Cette saisie est hors du périmètre où vous pouvez valider.");
        if (entry.CreatedBy == actor.Login)
            throw new RuleViolationException("Vous ne pouvez pas vous renvoyer votre propre saisie : modifiez-la ou supprimez-la.");
        var note = Note(comment, "Indiquez à l'auteur ce qu'il doit corriger.");
        entry.Status = WasteStatus.Renvoyee;
        entry.ReturnedBy = actor.Login;
        entry.ReturnedAt = Now;
        entry.ReturnComment = note;
        await Save(actor, entry, version, AuditActions.EntryReturned, note);
        await Write(actor, AuditActions.EntryReturned, entry, note, null, entry.Describe());
    }

    // Withdraws a validated entry (entered twice, wrong poste...). It stays
    // in the lists and the history, and counts nowhere.
    public async Task CancelAsync(Actor actor, long id, int version, string reason)
    {
        var entry = await Load(id);
        CheckVersion(entry, version);
        if (entry.Status != WasteStatus.Validee)
            throw new RuleViolationException(entry.IsOpen
                ? "Une saisie non validée se supprime (ou se renvoie à son auteur) : l'annulation est pour les saisies validées."
                : "Cette saisie est déjà annulée.");
        if (!Covers(actor, Perm.DechetsCorriger, entry))
            throw new AccessDeniedException("Vos rôles ne permettent pas d'annuler une saisie validée à cet endroit.");
        var note = Note(reason, "Indiquez le motif de l'annulation.");
        entry.Status = WasteStatus.Annulee;
        entry.CancelledBy = actor.Login;
        entry.CancelledAt = Now;
        entry.CancelReason = note;
        await Save(actor, entry, version, AuditActions.EntryCancelled, note);
        await Write(actor, AuditActions.EntryCancelled, entry, note, entry.Describe(), null);
    }

    public async Task DeleteAsync(Actor actor, long id, int version)
    {
        var entry = await Load(id);
        CheckVersion(entry, version);
        if (entry.Status == WasteStatus.Validee)
            throw new RuleViolationException("Une saisie validée ne peut pas être supprimée : corrigez-la ou annulez-la avec un motif.");
        if (entry.Status == WasteStatus.Annulee)
            throw new RuleViolationException("Une saisie annulée reste dans l'historique : elle ne se supprime pas.");
        if (!CanDelete(actor, entry))
            throw new AccessDeniedException("Seul l'auteur de la saisie, ou un rôle autorisé à modifier les brouillons d'autrui, peut la supprimer.");
        var revision = Revision(actor, entry, AuditActions.EntryDeleted, "");
        revision.Version = entry.Version + 1;
        if (!await store.DeleteAsync(id, version, actor.Login, Now, revision))
            throw Changed();
        await Write(actor, AuditActions.EntryDeleted, entry, "", entry.Describe(), null);
    }

    // ---------- what the screens offer ----------

    public bool CanValidate(Actor actor, WasteEntry entry, SecurityPolicy? policy)
        => entry.Status == WasteStatus.Brouillon
           && Covers(actor, Perm.DechetsValider, entry)
           && SecondPersonRefusal(actor, entry, policy ?? new SecurityPolicy()) == null;

    public bool CanReturn(Actor actor, WasteEntry entry)
        => entry.Status == WasteStatus.Brouillon && entry.CreatedBy != actor.Login && Covers(actor, Perm.DechetsValider, entry);

    public bool CanEdit(Actor actor, WasteEntry entry) => entry.Status != WasteStatus.Annulee && Covers(actor, EditPermission(actor, entry), entry);

    public bool CanDelete(Actor actor, WasteEntry entry) => entry.IsOpen && CanEdit(actor, entry);

    public bool CanCancel(Actor actor, WasteEntry entry) => entry.Status == WasteStatus.Validee && Covers(actor, Perm.DechetsCorriger, entry);

    // Why the actor may not validate this entry themselves, or null.
    public string? SecondPersonRefusal(Actor actor, WasteEntry entry, SecurityPolicy policy)
    {
        if (!policy.RequireSecondValidator)
            return null;
        if (entry.CreatedBy == actor.Login)
            return "Une saisie doit être validée par une autre personne que son auteur.";
        if (entry.UpdatedBy == actor.Login)
            return "Vous avez modifié cette saisie : une autre personne doit la valider.";
        return null;
    }

    // ---------- internals ----------

    private static string EditPermission(Actor actor, WasteEntry entry)
        => entry.Status == WasteStatus.Validee ? Perm.DechetsCorriger
            : entry.CreatedBy == actor.Login ? EntryPermission(entry.Kind)
            : Perm.DechetsAutrui;

    private async Task<string?> SecondPersonRule(Actor actor, WasteEntry entry) => SecondPersonRefusal(actor, entry, await Policy());

    private async Task<SecurityPolicy> Policy()
        => await settings.GetAsync<SecurityPolicy>(SettingKeys.SecurityPolicy) ?? new SecurityPolicy();

    private async Task<WasteEntry> Load(long id)
        => await store.GetAsync(id) ?? throw new RuleViolationException("Saisie introuvable (supprimée entre-temps ?).");

    private static RuleViolationException Changed()
        => new("Cette saisie a été modifiée entre-temps par quelqu'un d'autre. La liste est rechargée : vérifiez-la, puis recommencez.");

    private static void CheckVersion(WasteEntry entry, int version)
    {
        if (entry.Version != version)
            throw Changed();
    }

    private static string Note(string? text, string missing)
    {
        var note = text?.Trim() ?? "";
        if (note.Length == 0)
            throw new RuleViolationException(missing);
        if (note.Length > MaxNoteLength)
            throw new RuleViolationException($"Le texte est limité à {MaxNoteLength} caractères.");
        return note;
    }

    private async Task Save(Actor actor, WasteEntry entry, int expected, string action, string note)
    {
        entry.Version = expected + 1;
        if (!await store.UpdateAsync(entry, expected, Revision(actor, entry, action, note)))
            throw Changed();
    }

    private WasteRevision Revision(Actor actor, WasteEntry entry, string action, string note) => new()
    {
        EntryId = entry.Id,
        Version = entry.Version,
        At = Now,
        UserLogin = actor.Login,
        Action = action,
        Status = entry.Status,
        Snapshot = entry.Describe(),
        Note = note,
    };

    // Checks the draft and writes it into the entry; `permission` must hold
    // at the new place too (an entry cannot be moved out of one's scope).
    // The date / poste rules apply to a new entry, or one moved to another
    // poste: a returned entry of an earlier poste can still be corrected.
    private async Task Apply(Actor actor, WasteEntry entry, WasteDraft draft, string permission, bool isNew)
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
        if (Math.Round(draft.Quantity, 3) != draft.Quantity)
            throw new RuleViolationException("La quantité a 3 décimales au plus (ex. 12,5 ou 0,125).");
        var reasons = await store.ListReasonsAsync(draft.Kind);
        if (string.IsNullOrWhiteSpace(draft.Reason) || !reasons.Any(r => r.Label == draft.Reason))
            throw new RuleViolationException("Choisissez un motif dans la liste.");
        if ((draft.Comment ?? "").Length > 500)
            throw new RuleViolationException("Le commentaire est limité à 500 caractères.");
        if (WasteReasons.NeedsComment(draft.Reason) && string.IsNullOrWhiteSpace(draft.Comment))
            throw new RuleViolationException("Motif « Autre » : précisez-le dans le commentaire.");

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
        bool At(string p, string? t)
            => draft.Kind == WasteKind.Produit ? actor.AllowsSectorWide(p, sector, t) : actor.Allows(p, sector, machineId, t);

        if (!At(permission, null))
            throw new AccessDeniedException(draft.Kind == WasteKind.Produit && actor.Allows(permission, sector)
                ? $"Le déchet produit se saisit pour tout le secteur {place} : votre périmètre est limité à certaines machines."
                : $"{place} est hors de votre périmètre.");
        if (!At(permission, team))
            throw new AccessDeniedException($"Ce poste a été travaillé par {team} : hors de votre périmètre.");

        var moved = isNew || date != entry.ProductionDate.Date || draft.ShiftId != entry.ShiftId;
        if (moved && !actor.Allows(Perm.DechetsAutresPostes, sector, machineId, team))
        {
            var current = CurrentPoste(actor);
            if (current == null)
                throw new RuleViolationException("Votre équipe n'a pas de poste en cours ou récent : la saisie se fait pendant ou juste après son poste.");
            if (current.Value != (date, draft.ShiftId))
                throw new RuleViolationException(
                    $"Vous saisissez pour le poste de votre équipe seulement : {Shifts.Label(current.Value.ShiftId)} du {current.Value.Date:dd/MM/yyyy}.");
        }
        if (moved && date < today.AddDays(-LateEntryDays) && !At(Perm.DechetsTardif, team))
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
