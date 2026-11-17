using System.Text.RegularExpressions;
using Spiga.Core.Audit;

namespace Spiga.Core.Security;

public sealed record UserDraft(string Login, string DisplayName, IReadOnlyList<RoleAssignment> Assignments, bool IsActive = true);

public sealed record RoleChange(string Name, string Description, bool IsActive, PermissionSet Permissions, IReadOnlyList<int> AssignableRoleIds);

// Users, roles and security rules. Every method checks the caller's rights
// itself and writes the audit trail.
//
// Two levels of administration:
//  - "Gérer les rôles": defines roles and gives any role (Administrateur only
//    by an administrator);
//  - "Créer et modifier des comptes" alone (delegated): gives only the roles
//    its own roles list as assignable, only inside its own scope, and only
//    to accounts that hold nothing else.
// No change may leave the application without an active administrator.
public sealed partial class AccessAdminService(
    IUserStore users, IRoleStore roles, ISettingsStore settings, IAuditLog audit, TimeProvider clock, ClientInfo client)
{
    private DateTime Now => clock.GetLocalNow().DateTime;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,39}$")]
    private static partial Regex LoginPattern();

    public static string CheckLogin(string login)
    {
        login = (login ?? "").Trim().ToLowerInvariant();
        if (!LoginPattern().IsMatch(login))
            throw new RuleViolationException("L'identifiant doit faire 3 à 40 caractères : lettres minuscules, chiffres, point, tiret ou souligné.");
        return login;
    }

    // ---------- roles ----------

    public async Task<IReadOnlyList<Role>> ListRolesAsync(Actor actor)
    {
        actor.Demand(Perm.View(Modules.Utilisateurs));
        return await roles.ListAsync();
    }

    public async Task<Role> CreateRoleAsync(Actor actor, string name, string description, int? copyFromRoleId)
    {
        actor.Demand(Perm.RolesGerer);
        var existing = await roles.ListAsync();
        name = CheckRoleName(name, existing, null);
        var source = copyFromRoleId is { } id ? existing.FirstOrDefault(r => r.Id == id) : null;
        var role = new Role
        {
            Code = UniqueCode(name, existing),
            Name = name,
            Description = (description ?? "").Trim(),
            IsActive = true,
            Permissions = source?.EffectivePermissions.Clone() ?? new PermissionSet(),
            AssignableRoleIds = source?.AssignableRoleIds.ToList() ?? new List<int>(),
        };
        role.Id = await roles.InsertAsync(role);
        await Write(actor, AuditActions.RoleCreated, role.Name, source == null ? "Rôle vide." : $"Copie de {source.Name}.",
            null, role.Permissions.Describe());
        return role;
    }

    public async Task UpdateRoleAsync(Actor actor, int roleId, RoleChange change)
    {
        actor.Demand(Perm.RolesGerer);
        var all = await roles.ListAsync();
        var role = all.FirstOrDefault(r => r.Id == roleId) ?? throw new RuleViolationException("Rôle introuvable.");
        if (role.IsSystem)
            throw new RuleViolationException("Le rôle Administrateur est un rôle système : il ne peut pas être modifié.");
        var name = CheckRoleName(change.Name, all, roleId);
        var assignable = change.AssignableRoleIds.Distinct().ToList();
        if (assignable.Any(id => all.FirstOrDefault(r => r.Id == id) is not { IsSystem: false }))
            throw new RuleViolationException("Le rôle Administrateur ne peut pas être délégué ; choisissez des rôles existants.");

        var updated = new Role
        {
            Id = role.Id, Code = role.Code, Name = name, Description = (change.Description ?? "").Trim(),
            IsActive = change.IsActive, Permissions = change.Permissions.Clone(), AssignableRoleIds = assignable,
        };
        await EnsureAnAdministratorRemains(rolesOverride: r => r.Id == roleId ? updated : r);
        await roles.UpdateAsync(updated);

        var diff = PermissionSet.Diff(role.Permissions, updated.Permissions);
        if (diff.Length > 0)
            await Write(actor, AuditActions.RoleUpdated, updated.Name, "", null, diff);
        var before = Properties(role, all);
        var after = Properties(updated, all);
        if (before != after)
            await Write(actor, AuditActions.RoleRenamed, updated.Name, "", before, after);
    }

    public async Task DeleteRoleAsync(Actor actor, int roleId)
    {
        actor.Demand(Perm.RolesGerer);
        var role = await roles.GetAsync(roleId) ?? throw new RuleViolationException("Rôle introuvable.");
        if (role.IsSystem)
            throw new RuleViolationException("Le rôle Administrateur ne peut pas être supprimé.");
        if (role.UserCount > 0)
            throw new RuleViolationException($"{role.UserCount} compte(s) ont le rôle « {role.Name} » : retirez-le-leur ou désactivez le rôle.");
        await roles.DeleteAsync(roleId);
        await Write(actor, AuditActions.RoleDeleted, role.Name, "", role.Permissions.Describe(), null);
    }

    public async Task<int> AddTemplatesAsync(Actor actor)
    {
        actor.Demand(Perm.RolesGerer);
        var count = await InstallTemplatesAsync(roles);
        if (count > 0)
            await Write(actor, AuditActions.RoleCreated, "Modèles", $"{count} rôles types ajoutés.", null, null);
        return count;
    }

    // Creates the templates whose name is not taken yet; returns how many.
    public static async Task<int> InstallTemplatesAsync(IRoleStore roles)
    {
        var existing = await roles.ListAsync();
        var created = new List<(Role Role, RoleTemplates.Template Template)>();
        foreach (var t in RoleTemplates.All)
        {
            if (existing.Any(r => r.Code == t.Code || string.Equals(r.Name, t.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            var role = new Role { Code = t.Code, Name = t.Name, Description = t.Description, Permissions = new PermissionSet(t.Permissions) };
            role.Id = await roles.InsertAsync(role);
            created.Add((role, t));
        }
        var all = await roles.ListAsync();
        foreach (var (role, t) in created.Where(c => c.Template.Assignable.Length > 0))
        {
            role.AssignableRoleIds = all.Where(r => t.Assignable.Contains(r.Code)).Select(r => r.Id).ToList();
            await roles.UpdateAsync(role);
        }
        return created.Count;
    }

    // ---------- users ----------

    public async Task<IReadOnlyList<AppUser>> ListUsersAsync(Actor actor)
    {
        actor.Demand(Perm.View(Modules.Utilisateurs));
        return await users.ListAsync();
    }

    // The roles this actor may give.
    public async Task<IReadOnlyList<Role>> AssignableRolesAsync(Actor actor)
    {
        var all = await roles.ListAsync();
        if (actor.Can(Perm.RolesGerer))
            return all.Where(r => !r.IsSystem || actor.IsAdministrator).ToList();
        if (!actor.Can(Perm.ComptesGerer))
            return Array.Empty<Role>();
        var ids = actor.Grants.Where(g => g.Role.EffectivePermissions.Has(Perm.ComptesGerer))
            .SelectMany(g => g.Role.AssignableRoleIds).ToHashSet();
        return all.Where(r => ids.Contains(r.Id) && !r.IsSystem).ToList();
    }

    // Whether this actor may change this account (roles, state, password).
    public async Task<bool> CanManageAsync(Actor actor, AppUser user)
    {
        if (actor.Can(Perm.RolesGerer))
            return actor.IsAdministrator || !await HoldsSystemRole(user);
        if (!actor.Can(Perm.ComptesGerer))
            return false;
        var assignable = (await AssignableRolesAsync(actor)).Select(r => r.Id).ToHashSet();
        return user.Assignments.All(a => assignable.Contains(a.RoleId) && WithinDelegation(actor, a.Scope));
    }

    // Returns the temporary password to hand to the user; it must be
    // changed at the first sign-in.
    public async Task<string> CreateUserAsync(Actor actor, UserDraft draft)
    {
        DemandAccountRights(actor);
        var login = CheckLogin(draft.Login);
        if (await users.GetByLoginAsync(login) != null)
            throw new RuleViolationException($"L'identifiant « {login} » est déjà utilisé.");
        if (string.IsNullOrWhiteSpace(draft.DisplayName))
            throw new RuleViolationException("Indiquez le nom de l'utilisateur.");
        var assignments = await CheckAssignments(actor, draft.Assignments);

        var temporary = PasswordHasher.GenerateTemporary();
        var user = new AppUser
        {
            Login = login,
            DisplayName = draft.DisplayName.Trim(),
            PasswordHash = PasswordHasher.Hash(temporary),
            MustChangePassword = true,
            IsActive = draft.IsActive,
            CreatedAt = Now,
            Assignments = assignments,
        };
        user.Id = await users.InsertAsync(user);
        await Write(actor, AuditActions.UserCreated, login, "", null, Describe(user));
        return temporary;
    }

    public async Task UpdateUserAsync(Actor actor, int userId, UserDraft draft)
    {
        DemandAccountRights(actor);
        var user = await users.GetAsync(userId) ?? throw new RuleViolationException("Utilisateur introuvable.");
        if (!await CanManageAsync(actor, user))
            throw new AccessDeniedException("Ce compte a des rôles que vous ne pouvez pas attribuer : seul un gestionnaire des rôles peut le modifier.");
        if (string.IsNullOrWhiteSpace(draft.DisplayName))
            throw new RuleViolationException("Indiquez le nom de l'utilisateur.");
        var assignments = await CheckAssignments(actor, draft.Assignments);
        if (user.Id == actor.User.Id && (!draft.IsActive || !SameAssignments(user.Assignments, assignments)))
            throw new RuleViolationException("Vous ne pouvez pas désactiver votre propre compte ni changer vos propres rôles.");

        var before = Describe(user);
        user.DisplayName = draft.DisplayName.Trim();
        user.IsActive = draft.IsActive;
        user.Assignments = assignments;
        await EnsureAnAdministratorRemains(usersOverride: u => u.Id == user.Id ? user : u);

        await users.UpdateAsync(user);
        await users.SetAssignmentsAsync(user.Id, assignments);
        var after = Describe(user);
        if (after != before)
            await Write(actor, AuditActions.UserUpdated, user.Login, "", before, after);
    }

    public async Task<string> ResetPasswordAsync(Actor actor, int userId)
    {
        var user = await ManageableForUnlock(actor, userId);
        var temporary = PasswordHasher.GenerateTemporary();
        user.PasswordHash = PasswordHasher.Hash(temporary);
        user.MustChangePassword = true;
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        await users.UpdateAsync(user);
        await Write(actor, AuditActions.PasswordReset, user.Login, "Mot de passe temporaire, à changer à la connexion.", null, null);
        return temporary;
    }

    public async Task UnlockAsync(Actor actor, int userId)
    {
        var user = await ManageableForUnlock(actor, userId);
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        await users.UpdateAsync(user);
        await Write(actor, AuditActions.AccountUnlocked, user.Login, "", null, null);
    }

    // ---------- security rules ----------

    public async Task<SecurityPolicy> GetPolicyAsync(Actor actor)
    {
        actor.Demand(Perm.View(Modules.Utilisateurs));
        return await settings.GetAsync<SecurityPolicy>(SettingKeys.SecurityPolicy) ?? new SecurityPolicy();
    }

    public async Task SavePolicyAsync(Actor actor, SecurityPolicy policy)
    {
        actor.Demand(Perm.SecuriteGerer);
        var error = policy.CheckSelf();
        if (error != null)
            throw new RuleViolationException(error);
        var before = await settings.GetAsync<SecurityPolicy>(SettingKeys.SecurityPolicy) ?? new SecurityPolicy();
        await settings.SetAsync(SettingKeys.SecurityPolicy, policy, actor.Login);
        await Write(actor, AuditActions.PolicyUpdated, "Sécurité", "", before.Describe(), policy.Describe());
    }

    // ---------- checks ----------

    private static void DemandAccountRights(Actor actor)
    {
        if (!actor.Can(Perm.ComptesGerer) && !actor.Can(Perm.RolesGerer))
            actor.Demand(Perm.ComptesGerer);
    }

    private async Task<AppUser> ManageableForUnlock(Actor actor, int userId)
    {
        actor.Demand(Perm.ComptesDebloquer);
        var user = await users.GetAsync(userId) ?? throw new RuleViolationException("Utilisateur introuvable.");
        if (!actor.Can(Perm.RolesGerer) && !await CanManageAsync(actor, user))
            throw new AccessDeniedException("Ce compte est hors des rôles et du périmètre que vous gérez.");
        if (!actor.IsAdministrator && await HoldsSystemRole(user))
            throw new AccessDeniedException("Seul un administrateur peut débloquer ou réinitialiser un administrateur.");
        return user;
    }

    private async Task<List<RoleAssignment>> CheckAssignments(Actor actor, IReadOnlyList<RoleAssignment> requested)
    {
        if (requested.Count == 0)
            throw new RuleViolationException("Attribuez au moins un rôle : un compte sans rôle ne peut rien faire.");
        var all = (await roles.ListAsync()).ToDictionary(r => r.Id);
        var assignable = (await AssignableRolesAsync(actor)).Select(r => r.Id).ToHashSet();
        var result = new List<RoleAssignment>();
        foreach (var a in requested)
        {
            var role = all.GetValueOrDefault(a.RoleId) ?? throw new RuleViolationException("Un rôle attribué n'existe plus.");
            if (role.IsSystem && !actor.IsAdministrator)
                throw new AccessDeniedException("Seul un administrateur peut attribuer le rôle Administrateur.");
            if (!assignable.Contains(role.Id))
                throw new AccessDeniedException($"Vous ne pouvez pas attribuer le rôle « {role.Name} ».");
            if (!actor.Can(Perm.RolesGerer) && !WithinDelegation(actor, a.Scope))
                throw new AccessDeniedException($"Le périmètre « {a.Scope.Describe()} » dépasse le vôtre ({actor.ScopeOf(Perm.ComptesGerer)}).");
            if (a.ValidFrom is { } f && a.ValidTo is { } t && t.Date < f.Date)
                throw new RuleViolationException($"Rôle « {role.Name} » : la date de fin précède la date de début.");
            var copy = a.Clone();
            copy.RoleName = role.Name;
            copy.ValidFrom = a.ValidFrom?.Date;
            copy.ValidTo = a.ValidTo?.Date;
            result.Add(copy);
        }
        return result;
    }

    private static bool WithinDelegation(Actor actor, DataScope scope)
        => actor.Grants.Where(g => g.Role.EffectivePermissions.Has(Perm.ComptesGerer)).Any(g => scope.IsWithin(g.Scope));

    private async Task<bool> HoldsSystemRole(AppUser user)
    {
        var system = (await roles.ListAsync()).Where(r => r.IsSystem).Select(r => r.Id).ToHashSet();
        return user.Assignments.Any(a => system.Contains(a.RoleId));
    }

    // At least one active account keeps, today, an active role that can
    // manage both roles and accounts.
    private async Task EnsureAnAdministratorRemains(Func<AppUser, AppUser>? usersOverride = null, Func<Role, Role>? rolesOverride = null)
    {
        var allRoles = (await roles.ListAsync()).Select(r => rolesOverride?.Invoke(r) ?? r).ToDictionary(r => r.Id);
        bool Administers(Role r) => r.IsActive && (r.IsSystem || (r.Permissions.Has(Perm.RolesGerer) && r.Permissions.Has(Perm.ComptesGerer)));
        var admins = (await users.ListAsync())
            .Select(u => usersOverride?.Invoke(u) ?? u)
            .Count(u => u.IsActive && u.Assignments.Any(a => a.IsCurrent(Now) && allRoles.TryGetValue(a.RoleId, out var r) && Administers(r)));
        if (admins == 0)
            throw new RuleViolationException("Cette modification laisserait l'application sans aucun administrateur actif.");
    }

    private static bool SameAssignments(IReadOnlyList<RoleAssignment> a, IReadOnlyList<RoleAssignment> b)
        => a.Select(x => x.Describe()).OrderBy(x => x).SequenceEqual(b.Select(x => x.Describe()).OrderBy(x => x));

    private static string CheckRoleName(string name, IReadOnlyList<Role> existing, int? exceptId)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 3 or > 60)
            throw new RuleViolationException("Le nom du rôle doit faire 3 à 60 caractères.");
        if (existing.Any(r => r.Id != exceptId && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new RuleViolationException($"Un rôle « {name} » existe déjà.");
        return name;
    }

    private static string Properties(Role r, IReadOnlyList<Role> all)
        => $"{r.Name} — {r.Description} — {(r.IsActive ? "actif" : "inactif")} — attribuable : " +
           (r.AssignableRoleIds.Count == 0 ? "aucun" : string.Join(", ", all.Where(x => r.AssignableRoleIds.Contains(x.Id)).Select(x => x.Name)));

    private static string Describe(AppUser user)
        => $"{user.DisplayName} · {(user.IsActive ? "actif" : "désactivé")}\n" + string.Join("\n", user.Assignments.Select(a => a.Describe()));

    private static string UniqueCode(string name, IReadOnlyList<Role> existing)
    {
        var normalized = new string(name.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        var code = normalized.Length == 0 ? "role" : normalized;
        var candidate = code;
        for (var i = 2; existing.Any(r => r.Code == candidate); i++)
            candidate = $"{code}-{i}";
        return candidate;
    }

    private Task Write(Actor actor, string action, string target, string details, string? oldValue, string? newValue)
        => audit.WriteAsync(new AuditEntry
        {
            At = Now,
            UserLogin = actor.Login,
            Module = Modules.Utilisateurs,
            Action = action,
            Target = target,
            Details = details,
            OldValue = oldValue,
            NewValue = newValue,
            Workstation = client.Workstation,
        });
}
