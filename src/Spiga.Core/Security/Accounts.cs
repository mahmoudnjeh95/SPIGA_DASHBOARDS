namespace Spiga.Core.Security;

public sealed class Role
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    // The built-in Administrateur role: always full rights, cannot be
    // edited or deleted, so the application can never lock itself out.
    public bool IsSystem { get; set; }

    public PermissionSet Permissions { get; set; } = new();
    public int UserCount { get; set; }
}

public sealed class AppUser
{
    public int Id { get; set; }
    public string Login { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int RoleId { get; set; }
    public string RoleName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool MustChangePassword { get; set; }
    public bool IsActive { get; set; } = true;
    public int FailedAttempts { get; set; }

    // Locked while LockedUntil is in the future. DataScope.Forever = until an
    // administrator unlocks the account.
    public DateTime? LockedUntil { get; set; }

    public DataScope Scope { get; set; } = DataScope.Everything;
    public string Theme { get; set; } = "light";
    public DateTime? LastLoginAt { get; set; }
    public DateTime? PasswordChangedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool IsLocked(DateTime now) => LockedUntil is { } until && until > now;
}

// The signed-in user as every service sees it: who, with which rights,
// over which part of the plant. Services check it themselves, the screens
// only hide what the user cannot use.
public sealed class Actor
{
    public Actor(AppUser user, Role role)
    {
        User = user;
        Role = role;
    }

    public AppUser User { get; }
    public Role Role { get; }
    public string Login => User.Login;
    public string DisplayName => User.DisplayName;
    public DataScope Scope => User.Scope;
    public PermissionSet Permissions => Role.IsSystem ? PermissionSet.Full() : Role.Permissions;

    public bool Can(string module, AppAction action) => Permissions.Can(module, action);

    public void Demand(string module, AppAction action)
    {
        if (!Can(module, action))
        {
            var label = ModuleCatalog.Find(module)?.Label ?? module;
            throw new AccessDeniedException($"Votre rôle ne permet pas « {AppActions.Label(action)} » dans « {label} ».");
        }
    }
}

public sealed class AccessDeniedException(string message) : Exception(message);

// A business rule refused the operation; the message is shown to the user as is.
public sealed class RuleViolationException(string message) : Exception(message);
