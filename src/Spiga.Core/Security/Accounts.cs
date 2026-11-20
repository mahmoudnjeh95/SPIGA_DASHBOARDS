namespace Spiga.Core.Security;

// A role is a named set of permissions, defined by the administrators in the
// application. Only Administrateur is built in.
public sealed class Role
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    // Administrateur: all permissions, cannot be changed, deactivated or
    // deleted, so the application can never lock itself out.
    public bool IsSystem { get; set; }

    // An inactive role gives nothing to the users who hold it, without
    // having to take it off each of them.
    public bool IsActive { get; set; } = true;

    public PermissionSet Permissions { get; set; } = new();

    // Delegated administration: holders of this role who may manage
    // accounts ("Créer et modifier des comptes") may give these roles only.
    public List<int> AssignableRoleIds { get; set; } = new();

    public int UserCount { get; set; }

    public PermissionSet EffectivePermissions => IsSystem ? PermissionSet.Full() : Permissions;
}

// One role given to one user, over a part of the plant, possibly for a
// period (temporary staff, a replacement). A user can hold several.
public sealed class RoleAssignment
{
    public long Id { get; set; }
    public int RoleId { get; set; }
    public string RoleName { get; set; } = "";
    public DataScope Scope { get; set; } = DataScope.Everything;
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }

    public bool IsCurrent(DateTime today)
        => (ValidFrom == null || ValidFrom.Value.Date <= today.Date) && (ValidTo == null || ValidTo.Value.Date >= today.Date);

    public string Validity()
        => (ValidFrom, ValidTo) switch
        {
            (null, null) => "permanent",
            ({ } f, null) => $"à partir du {f:dd/MM/yyyy}",
            (null, { } t) => $"jusqu'au {t:dd/MM/yyyy}",
            ({ } f, { } t) => $"du {f:dd/MM/yyyy} au {t:dd/MM/yyyy}",
        };

    public string Describe() => $"{RoleName} · {Scope.Describe()} · {Validity()}";

    public RoleAssignment Clone() => (RoleAssignment)MemberwiseClone();
}

public sealed class AppUser
{
    public int Id { get; set; }
    public string Login { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool MustChangePassword { get; set; }
    public bool IsActive { get; set; } = true;
    public int FailedAttempts { get; set; }

    // Locked while LockedUntil is in the future. DataScope.Forever = until an
    // administrator unlocks the account.
    public DateTime? LockedUntil { get; set; }

    public string Theme { get; set; } = "light";
    public DateTime? LastLoginAt { get; set; }
    public DateTime? PasswordChangedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<RoleAssignment> Assignments { get; set; } = new();

    public bool IsLocked(DateTime now) => LockedUntil is { } until && until > now;

    public string RolesSummary => Assignments.Count == 0 ? "Aucun rôle" : string.Join(", ", Assignments.Select(a => a.RoleName).Distinct());
}

// The signed-in user as every service sees it: their current role
// assignments, each one a set of permissions over a scope. A right is held
// at a place when one assignment gives that permission over that place.
public sealed class Actor
{
    public sealed record Grant(Role Role, RoleAssignment Assignment)
    {
        public DataScope Scope => Assignment.Scope;
    }

    public Actor(AppUser user, IReadOnlyList<Grant> grants)
    {
        User = user;
        Grants = grants;
        Permissions = PermissionSet.Union(grants.Select(g => g.Role.EffectivePermissions));
    }

    public AppUser User { get; }
    public IReadOnlyList<Grant> Grants { get; }

    // Union of the permissions of all current assignments, wherever they apply.
    public PermissionSet Permissions { get; }

    public string Login => User.Login;
    public string DisplayName => User.DisplayName;
    public bool IsAdministrator => Grants.Any(g => g.Role.IsSystem);
    public string RoleNames => string.Join(", ", Grants.Select(g => g.Role.Name).Distinct());

    public bool Can(string permission) => Permissions.Has(permission);

    public bool Can(string module, AppAction action) => Permissions.Can(module, action);

    // The permission over this place: sector, and when given machine and
    // team (null = not checked).
    public bool Allows(string permission, string sector, string? machineId = null, string? team = null)
        => Grants.Any(g => g.Role.EffectivePermissions.Has(permission)
                           && g.Scope.AllowsSector(sector)
                           && (machineId == null || g.Scope.AllowsMachine(machineId, sector))
                           && (team == null || g.Scope.AllowsTeam(team)));

    // The permission over the whole sector: a grant on that sector that is not
    // narrowed to some machines (data entered per sector, not per machine).
    public bool AllowsSectorWide(string permission, string sector, string? team = null)
        => Grants.Any(g => g.Role.EffectivePermissions.Has(permission)
                           && g.Scope.AllowsSector(sector)
                           && g.Scope.Machines.Count == 0
                           && (team == null || g.Scope.AllowsTeam(team)));

    // The permission for this team, whatever the machines (data kept per
    // team, such as the pointage sheets).
    public bool AllowsTeam(string permission, string team)
        => Grants.Any(g => g.Role.EffectivePermissions.Has(permission) && g.Scope.AllowsTeam(team));

    public bool AllowsAny(IEnumerable<string> permissions, string sector, string? machineId = null, string? team = null)
        => permissions.Any(p => Allows(p, sector, machineId, team));

    // Where the permission applies, in words.
    public string ScopeOf(string permission)
    {
        var scopes = Grants.Where(g => g.Role.EffectivePermissions.Has(permission)).Select(g => g.Scope).ToList();
        if (scopes.Count == 0)
            return "aucun";
        return scopes.Any(s => s.IsUnrestricted) ? "Toute l'usine" : string.Join(" ; ", scopes.Select(s => s.Describe()).Distinct());
    }

    // The one team of a team-bound account: every assignment is on that
    // team (always the case without "Couvrir plusieurs équipes"). null = the
    // account covers several or all teams. Postes worked by another team are
    // shown greyed out to it.
    public string? Team
    {
        get
        {
            var teams = Grants.Select(g => g.Scope.Team ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return teams.Count == 1 && teams[0].Length > 0 ? teams[0] : null;
        }
    }

    public bool IsOwnTeam(string? team) => Team == null || string.Equals(Team, team, StringComparison.OrdinalIgnoreCase);

    // Every role and scope held, as a stable text: changes with any change
    // of the account's rights, including the machines of its platforms.
    public string AccessKey
        => string.Join(" ; ", Grants.Select(g => $"{g.Role.Id}:{Key(g.Scope)}").OrderBy(k => k, StringComparer.Ordinal));

    // Where this permission applies, as a stable text: equal for two
    // permissions given over exactly the same scopes. Changes when an
    // administrator changes the scopes or the machines of a platform.
    public string ScopeKey(string permission)
        => string.Join(" ; ", Grants.Where(g => g.Role.EffectivePermissions.Has(permission))
            .Select(g => Key(g.Scope)).Distinct().OrderBy(k => k, StringComparer.Ordinal));

    // The machines where this permission applies, whatever the team; null
    // when it applies to every machine of the plant.
    public MachineRule? MachineRuleFor(string permission)
    {
        var scopes = Grants.Where(g => g.Role.EffectivePermissions.Has(permission)).Select(g => g.Scope).ToList();
        if (scopes.Any(s => s.Sectors.Count == 0 && s.Machines.Count == 0 && s.Platforms.Count == 0))
            return null;
        return new MachineRule(ScopeKey(permission), (machineId, sector) => scopes.Any(s => s.AllowsMachine(machineId, sector)));
    }

    // Whether `permission` applies everywhere `over` does (same scopes, or
    // the whole plant): an export or a validation then covers all that the
    // page shows.
    public bool Covers(string permission, string over)
        => Can(permission)
           && (Grants.Any(g => g.Role.EffectivePermissions.Has(permission) && g.Scope.IsUnrestricted)
               || ScopeKey(permission) == ScopeKey(over));

    // Station-wide outputs (Paramètres du poste, écran TV of the hall) are
    // for accounts whose right covers every machine.
    public bool CanEverywhere(string permission) => Can(permission) && MachineRuleFor(permission) == null;

    // Held over the whole plant and every team: for what cannot be split
    // by machine or team (the audit trail).
    public bool CanUnrestricted(string permission)
        => Grants.Any(g => g.Role.EffectivePermissions.Has(permission) && g.Scope.IsUnrestricted);

    private static string Key(DataScope s)
        => string.Join("|",
            "t:" + (s.Team ?? "").ToLowerInvariant(),
            "s:" + string.Join(",", s.Sectors.Select(x => x.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal)),
            "m:" + string.Join(",", s.Machines.Select(x => x.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal)),
            "p:" + string.Join(",", s.Platforms.OrderBy(p => p.Id).Select(p =>
                $"{p.Id}{(p.IsActive ? "" : "!")}[{string.Join(",", p.Machines.Select(m => m.MachineId.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal))}]")));

    public string ScopeSummary
        => Grants.Any(g => g.Scope.IsUnrestricted) ? "Toute l'usine" : string.Join(" ; ", Grants.Select(g => g.Scope.Describe()).Distinct());

    public void Demand(string permission)
    {
        if (!Can(permission))
            throw new AccessDeniedException($"Vos rôles ne donnent pas le droit « {PermissionCatalog.Label(permission)} ».");
    }

    public void Demand(string module, AppAction action)
    {
        var key = PermissionCatalog.Key(module, action);
        if (key == null || !Can(key))
            throw new AccessDeniedException(
                $"Vos rôles ne donnent pas le droit « {AppActions.Label(action)} » dans « {ModuleCatalog.Find(module)?.Label ?? module} ».");
    }
}

// The machines one right reaches (see Actor.MachineRuleFor). Key changes
// whenever the set may have changed, so the pages that hold data know to
// reload.
public sealed class MachineRule(string key, Func<string, string, bool> allows)
{
    public string Key { get; } = key;

    public bool Allows(string machineId, string sector) => allows(machineId, sector);
}

public sealed class AccessDeniedException(string message) : Exception(message);

// A business rule refused the operation; the message is shown to the user as is.
public sealed class RuleViolationException(string message) : Exception(message);
