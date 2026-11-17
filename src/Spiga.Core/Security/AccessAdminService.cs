using System.Text.RegularExpressions;
using Spiga.Core.Audit;

namespace Spiga.Core.Security;

public sealed record UserDraft(string Login, string DisplayName, int RoleId, DataScope Scope, bool IsActive = true);

// Users, roles and security rules. Every method checks the caller's rights
// itself and writes the audit trail; it also refuses any change that would
// leave nobody able to administer the application.
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
        actor.Demand(Modules.Utilisateurs, AppAction.Voir);
        return await roles.ListAsync();
    }

    public async Task<Role> CreateRoleAsync(Actor actor, string name, string description, int? copyFromRoleId)
    {
        actor.Demand(Modules.Utilisateurs, AppAction.Administrer);
        name = (name ?? "").Trim();
        if (name.Length is < 3 or > 60)
            throw new RuleViolationException("Le nom du rôle doit faire 3 à 60 caractères.");
        var existing = await roles.ListAsync();
        if (existing.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new RuleViolationException($"Un rôle « {name} » existe déjà.");

        var source = copyFromRoleId is { } id ? existing.FirstOrDefault(r => r.Id == id) : null;
        var role = new Role
        {
            Code = UniqueCode(name, existing),
            Name = name,
            Description = (description ?? "").Trim(),
            IsSystem = false,
            Permissions = source == null ? new PermissionSet() : (source.IsSystem ? PermissionSet.Full() : source.Permissions.Clone()),
        };
        role.Id = await roles.InsertAsync(role);
        await Write(actor, AuditActions.RoleCreated, role.Name,
            source == null ? "Rôle vide." : $"Copie de {source.Name}.", null, PermissionSet.Diff(new PermissionSet(), role.Permissions));
        return role;
    }

    public async Task UpdateRoleAsync(Actor actor, int roleId, string name, string description, PermissionSet permissions)
    {
        actor.Demand(Modules.Utilisateurs, AppAction.Administrer);
        var role = await roles.GetAsync(roleId) ?? throw new RuleViolationException("Rôle introuvable.");
        if (role.IsSystem)
            throw new RuleViolationException("Le rôle Administrateur est un rôle système : il ne peut pas être modifié.");
        name = (name ?? "").Trim();
        if (name.Length is < 3 or > 60)
            throw new RuleViolationException("Le nom du rôle doit faire 3 à 60 caractères.");
        var all = await roles.ListAsync();
        if (all.Any(r => r.Id != roleId && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new RuleViolationException($"Un rôle « {name} » existe déjà.");

        var updated = new Role
        {
            Id = role.Id, Code = role.Code, Name = name, Description = (description ?? "").Trim(),
            IsSystem = false, Permissions = permissions.Clone(),
        };
        await EnsureAnAdministratorRemains(rolesOverride: r => r.Id == roleId ? updated : r);

        await roles.UpdateAsync(updated);
        var diff = PermissionSet.Diff(role.Permissions, updated.Permissions);
        if (diff.Length > 0)
            await Write(actor, AuditActions.RoleUpdated, updated.Name, "", PermissionSet.Diff(updated.Permissions, role.Permissions), diff);
        if (role.Name != updated.Name || role.Description != updated.Description)
            await Write(actor, AuditActions.RoleRenamed, updated.Name, "", $"{role.Name} — {role.Description}", $"{updated.Name} — {updated.Description}");
    }

    public async Task DeleteRoleAsync(Actor actor, int roleId)
    {
        actor.Demand(Modules.Utilisateurs, AppAction.Administrer);
        var role = await roles.GetAsync(roleId) ?? throw new RuleViolationException("Rôle introuvable.");
        if (role.IsSystem)
            throw new RuleViolationException("Le rôle Administrateur ne peut pas être supprimé.");
        if ((await users.ListAsync()).Any(u => u.RoleId == roleId))
            throw new RuleViolationException($"Des utilisateurs ont le rôle « {role.Name} » : attribuez-leur un autre rôle d'abord.");
        await roles.DeleteAsync(roleId);
        await Write(actor, AuditActions.RoleDeleted, role.Name, "", PermissionSet.Diff(new PermissionSet(), role.Permissions), null);
    }

    // ---------- users ----------

    public async Task<IReadOnlyList<AppUser>> ListUsersAsync(Actor actor)
    {
        actor.Demand(Modules.Utilisateurs, AppAction.Voir);
        return await users.ListAsync();
    }

    // Returns the temporary password to hand to the user; it must be
    // changed at the first sign-in.
    public async Task<string> CreateUserAsync(Actor actor, UserDraft draft)
    {
        actor.Demand(Modules.Utilisateurs, AppAction.Administrer);
        var login = CheckLogin(draft.Login);
        if (await users.GetByLoginAsync(login) != null)
            throw new RuleViolationException($"L'identifiant « {login} » est déjà utilisé.");
        if (string.IsNullOrWhiteSpace(draft.DisplayName))
            throw new RuleViolationException("Indiquez le nom de l'utilisateur.");
        var role = await roles.GetAsync(draft.RoleId) ?? throw new RuleViolationException("Choisissez un rôle.");

        var temporary = PasswordHasher.GenerateTemporary();
        var user = new AppUser
        {
            Login = login,
            DisplayName = draft.DisplayName.Trim(),
            RoleId = role.Id,
            RoleName = role.Name,
            PasswordHash = PasswordHasher.Hash(temporary),
            MustChangePassword = true,
            IsActive = draft.IsActive,
            Scope = draft.Scope,
            CreatedAt = Now,
        };
        user.Id = await users.InsertAsync(user);
        await Write(actor, AuditActions.UserCreated, login, "", null, Describe(user, role));
        return temporary;
    }

    public async Task UpdateUserAsync(Actor actor, int userId, UserDraft draft)
    {
        actor.Demand(Modules.Utilisateurs, AppAction.Administrer);
        var user = await users.GetAsync(userId) ?? throw new RuleViolationException("Utilisateur introuvable.");
        var oldRole = await roles.GetAsync(user.RoleId);
        var role = await roles.GetAsync(draft.RoleId) ?? throw new RuleViolationException("Choisissez un rôle.");
        if (string.IsNullOrWhiteSpace(draft.DisplayName))
            throw new RuleViolationException("Indiquez le nom de l'utilisateur.");
        if (user.Id == actor.User.Id && (!draft.IsActive || draft.RoleId != user.RoleId))
            throw new RuleViolationException("Vous ne pouvez pas désactiver votre propre compte ni changer votre propre rôle.");

        var before = Describe(user, oldRole);
        user.DisplayName = draft.DisplayName.Trim();
        user.RoleId = role.Id;
        user.RoleName = role.Name;
        user.Scope = draft.Scope;
        user.IsActive = draft.IsActive;
        await EnsureAnAdministratorRemains(usersOverride: u => u.Id == user.Id ? user : u);

        await users.UpdateAsync(user);
        var after = Describe(user, role);
        if (after != before)
            await Write(actor, AuditActions.UserUpdated, user.Login, "", before, after);
    }

    public async Task<string> ResetPasswordAsync(Actor actor, int userId)
    {
        actor.Demand(Modules.Utilisateurs, AppAction.Administrer);
        var user = await users.GetAsync(userId) ?? throw new RuleViolationException("Utilisateur introuvable.");
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
        actor.Demand(Modules.Utilisateurs, AppAction.Administrer);
        var user = await users.GetAsync(userId) ?? throw new RuleViolationException("Utilisateur introuvable.");
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        await users.UpdateAsync(user);
        await Write(actor, AuditActions.AccountUnlocked, user.Login, "", null, null);
    }

    // ---------- security rules ----------

    public async Task<SecurityPolicy> GetPolicyAsync(Actor actor)
    {
        actor.Demand(Modules.Utilisateurs, AppAction.Voir);
        return await settings.GetAsync<SecurityPolicy>(SettingKeys.SecurityPolicy) ?? new SecurityPolicy();
    }

    public async Task SavePolicyAsync(Actor actor, SecurityPolicy policy)
    {
        actor.Demand(Modules.Utilisateurs, AppAction.Administrer);
        var error = policy.CheckSelf();
        if (error != null)
            throw new RuleViolationException(error);
        var before = await settings.GetAsync<SecurityPolicy>(SettingKeys.SecurityPolicy) ?? new SecurityPolicy();
        await settings.SetAsync(SettingKeys.SecurityPolicy, policy, actor.Login);
        await Write(actor, AuditActions.PolicyUpdated, "Sécurité", "", before.Describe(), policy.Describe());
    }

    // ---------- helpers ----------

    private async Task EnsureAnAdministratorRemains(Func<AppUser, AppUser>? usersOverride = null, Func<Role, Role>? rolesOverride = null)
    {
        var allRoles = (await roles.ListAsync()).Select(r => rolesOverride?.Invoke(r) ?? r).ToDictionary(r => r.Id);
        var admins = (await users.ListAsync())
            .Select(u => usersOverride?.Invoke(u) ?? u)
            .Count(u => u.IsActive && allRoles.TryGetValue(u.RoleId, out var r)
                        && (r.IsSystem || r.Permissions.Can(Modules.Utilisateurs, AppAction.Administrer)));
        if (admins == 0)
            throw new RuleViolationException("Cette modification laisserait l'application sans aucun administrateur actif.");
    }

    private static string Describe(AppUser user, Role? role)
        => $"{user.DisplayName} · rôle {role?.Name ?? "?"} · périmètre {user.Scope.Describe()} · {(user.IsActive ? "actif" : "désactivé")}";

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
