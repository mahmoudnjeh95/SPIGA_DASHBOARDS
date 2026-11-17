using Spiga.Core.Audit;

namespace Spiga.Core.Security;

// Where the session runs: the PC name on the desktop, the client address
// on the web. Written in every audit line.
public sealed class ClientInfo
{
    public string Workstation { get; set; } = Environment.MachineName;
}

public enum LoginStatus
{
    Success,
    InvalidCredentials,
    Locked,
    Disabled,
}

public sealed record LoginResult(LoginStatus Status, Actor? Actor = null, string Message = "")
{
    public bool MustChangePassword => Actor?.User.MustChangePassword == true;
}

public sealed class AuthService(
    IUserStore users, IRoleStore roles, ISettingsStore settings, IAuditLog audit, TimeProvider clock, ClientInfo client)
{
    private const string InvalidMessage = "Identifiant ou mot de passe incorrect.";

    private DateTime Now => clock.GetLocalNow().DateTime;

    public async Task<SecurityPolicy> GetPolicyAsync()
        => await settings.GetAsync<SecurityPolicy>(SettingKeys.SecurityPolicy) ?? new SecurityPolicy();

    public async Task<bool> NeedsSetupAsync() => await users.CountAsync() == 0;

    // First start only: creates the first administrator. Refused as soon as
    // any account exists, so the setup page cannot be replayed.
    public async Task<Actor> CreateFirstAdminAsync(string login, string displayName, string password)
    {
        if (!await NeedsSetupAsync())
            throw new RuleViolationException("L'application est déjà installée : connectez-vous avec un compte existant.");
        login = AccessAdminService.CheckLogin(login);
        if (string.IsNullOrWhiteSpace(displayName))
            throw new RuleViolationException("Indiquez le nom de l'administrateur.");
        var error = (await GetPolicyAsync()).CheckPassword(password);
        if (error != null)
            throw new RuleViolationException(error);

        var adminRole = await roles.GetByCodeAsync(DefaultRoles.AdminCode)
            ?? throw new InvalidOperationException("Rôle Administrateur introuvable : la base n'a pas été initialisée.");
        var user = new AppUser
        {
            Login = login,
            DisplayName = displayName.Trim(),
            RoleId = adminRole.Id,
            RoleName = adminRole.Name,
            PasswordHash = PasswordHasher.Hash(password),
            MustChangePassword = false,
            IsActive = true,
            CreatedAt = Now,
            PasswordChangedAt = Now,
            LastLoginAt = Now,
        };
        user.Id = await users.InsertAsync(user);
        await Write(login, AuditActions.SetupDone, login, $"Compte {login} créé avec le rôle Administrateur.");
        return new Actor(user, adminRole);
    }

    public async Task<LoginResult> LoginAsync(string login, string password)
    {
        login = (login ?? "").Trim();
        var user = string.IsNullOrEmpty(login) ? null : await users.GetByLoginAsync(login);
        if (user == null)
        {
            PasswordHasher.Verify(password ?? "", PasswordHasher.Decoy);
            await Write(login, AuditActions.LoginFailed, login, "Identifiant inconnu.");
            return new LoginResult(LoginStatus.InvalidCredentials, Message: InvalidMessage);
        }

        if (user.IsLocked(Now))
        {
            await Write(user.Login, AuditActions.LoginRefused, user.Login, "Compte bloqué.");
            return new LoginResult(LoginStatus.Locked, Message: LockedMessage(user));
        }

        var policy = await GetPolicyAsync();
        if (!PasswordHasher.Verify(password ?? "", user.PasswordHash))
        {
            user.FailedAttempts++;
            var locked = user.FailedAttempts >= policy.MaxFailedAttempts;
            if (locked)
            {
                user.LockedUntil = policy.LockoutMinutes == 0 ? DataScope.Forever : Now.AddMinutes(policy.LockoutMinutes);
                user.FailedAttempts = 0;
            }
            await users.UpdateAsync(user);
            await Write(user.Login, locked ? AuditActions.AccountLocked : AuditActions.LoginFailed, user.Login,
                locked ? $"{policy.MaxFailedAttempts} échecs consécutifs." : "Mot de passe incorrect.");
            return locked
                ? new LoginResult(LoginStatus.Locked, Message: LockedMessage(user))
                : new LoginResult(LoginStatus.InvalidCredentials, Message: InvalidMessage);
        }

        if (!user.IsActive)
        {
            await Write(user.Login, AuditActions.LoginRefused, user.Login, "Compte désactivé.");
            return new LoginResult(LoginStatus.Disabled, Message: "Ce compte est désactivé. Contactez un administrateur.");
        }

        var role = await roles.GetAsync(user.RoleId)
            ?? throw new InvalidOperationException($"Rôle {user.RoleId} introuvable pour {user.Login}.");
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        user.LastLoginAt = Now;
        await users.UpdateAsync(user);
        await Write(user.Login, AuditActions.LoginOk, user.Login, $"Rôle {role.Name}.");
        return new LoginResult(LoginStatus.Success, new Actor(user, role));
    }

    public async Task ChangePasswordAsync(Actor actor, string currentPassword, string newPassword)
    {
        var user = await users.GetAsync(actor.User.Id) ?? throw new RuleViolationException("Compte introuvable.");
        if (!PasswordHasher.Verify(currentPassword ?? "", user.PasswordHash))
            throw new RuleViolationException("Le mot de passe actuel est incorrect.");
        var error = (await GetPolicyAsync()).CheckPassword(newPassword);
        if (error != null)
            throw new RuleViolationException(error);
        if (PasswordHasher.Verify(newPassword, user.PasswordHash))
            throw new RuleViolationException("Le nouveau mot de passe doit être différent de l'actuel.");

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        user.MustChangePassword = false;
        user.PasswordChangedAt = Now;
        await users.UpdateAsync(user);
        actor.User.MustChangePassword = false;
        actor.User.PasswordHash = user.PasswordHash;
        await Write(actor.Login, AuditActions.PasswordChanged, actor.Login, "");
    }

    // Re-reads the account and its role, so a change made by an
    // administrator applies at the next navigation without signing out.
    public async Task<Actor?> RefreshAsync(Actor actor)
    {
        var user = await users.GetAsync(actor.User.Id);
        if (user == null || !user.IsActive || user.IsLocked(Now))
            return null;
        var role = await roles.GetAsync(user.RoleId);
        return role == null ? null : new Actor(user, role);
    }

    // Web host, page reloaded: the tab's stored session is taken back if the
    // account is still active and the inactivity limit has not passed.
    public async Task<Actor?> ResumeAsync(int userId, DateTime lastActivity)
    {
        var policy = await GetPolicyAsync();
        var limit = policy.SessionTimeoutMinutes > 0 ? TimeSpan.FromMinutes(policy.SessionTimeoutMinutes) : TimeSpan.FromHours(12);
        if (Now - lastActivity >= limit)
            return null;
        var user = await users.GetAsync(userId);
        if (user == null || !user.IsActive || user.IsLocked(Now) || user.MustChangePassword)
            return null;
        var role = await roles.GetAsync(user.RoleId);
        return role == null ? null : new Actor(user, role);
    }

    public async Task SetThemeAsync(Actor actor, string theme)
    {
        var user = await users.GetAsync(actor.User.Id);
        if (user == null)
            return;
        user.Theme = theme == "dark" ? "dark" : "light";
        await users.UpdateAsync(user);
        actor.User.Theme = user.Theme;
    }

    public Task LogoutAsync(Actor actor) => Write(actor.Login, AuditActions.Logout, actor.Login, "");

    public Task SessionLockedAsync(Actor actor) => Write(actor.Login, AuditActions.SessionLocked, actor.Login, "Inactivité.");

    private string LockedMessage(AppUser user)
        => user.LockedUntil >= DataScope.Forever.AddDays(-1)
            ? "Ce compte est bloqué. Un administrateur doit le débloquer."
            : $"Ce compte est bloqué jusqu'à {user.LockedUntil:HH:mm}.";

    private Task Write(string login, string action, string target, string details)
        => audit.WriteAsync(new AuditEntry
        {
            At = Now,
            UserLogin = string.IsNullOrEmpty(login) ? "(vide)" : login,
            Module = Modules.Utilisateurs,
            Action = action,
            Target = target,
            Details = details,
            Workstation = client.Workstation,
        });
}
