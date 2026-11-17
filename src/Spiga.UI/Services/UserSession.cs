using Spiga.Core.Security;

namespace Spiga.UI.Services;

// The signed-in user of one window (desktop) or one browser tab (web).
// Locks itself after the inactivity time of the security policy; the
// shell reports activity through Touch().
public sealed class UserSession : IDisposable
{
    private readonly AuthService _auth;
    private readonly TimeProvider _clock;
    private readonly ISessionStore _store;
    private readonly Timer _idleTimer;
    private DateTime _lastActivity;

    public UserSession(AuthService auth, TimeProvider clock, ISessionStore store)
    {
        _auth = auth;
        _clock = clock;
        _store = store;
        _idleTimer = new Timer(_ => CheckIdle(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    public Actor? Actor { get; private set; }
    public bool IsLocked { get; private set; }
    public SecurityPolicy Policy { get; private set; } = new();

    // Shown before sign-in (login page); the user's own choice afterwards.
    public string Theme => Actor?.User.Theme ?? "light";

    public event Action? Changed;

    private DateTime Now => _clock.GetLocalNow().DateTime;

    public bool Can(string module, AppAction action) => Actor?.Can(module, action) == true;

    public bool Can(string permission) => Actor?.Can(permission) == true;

    public async Task SignInAsync(Actor actor)
    {
        Actor = actor;
        IsLocked = false;
        Policy = await _auth.GetPolicyAsync();
        Touch();
        await _store.SaveAsync(actor.User.Id, _lastActivity);
        Notify();
    }

    // Takes back the session of this browser tab after a page reload.
    public async Task<bool> TryResumeAsync()
    {
        var stored = await _store.LoadAsync();
        if (stored is not { } s)
            return false;
        var actor = await _auth.ResumeAsync(s.UserId, s.LastActivity);
        if (actor == null)
        {
            await _store.ClearAsync();
            return false;
        }
        Actor = actor;
        IsLocked = false;
        Policy = await _auth.GetPolicyAsync();
        Touch();
        await _store.SaveAsync(actor.User.Id, _lastActivity);
        return true;
    }

    public async Task SignOutAsync()
    {
        if (Actor != null)
            await _auth.LogoutAsync(Actor);
        Actor = null;
        IsLocked = false;
        await _store.ClearAsync();
        Notify();
    }

    public async Task<LoginResult> UnlockAsync(string password)
    {
        if (Actor == null)
            return new LoginResult(LoginStatus.InvalidCredentials);
        var result = await _auth.LoginAsync(Actor.Login, password);
        if (result.Status == LoginStatus.Success)
            await SignInAsync(result.Actor!);
        else if (result.Status is LoginStatus.Locked or LoginStatus.Disabled)
        {
            Actor = null;
            IsLocked = false;
            await _store.ClearAsync();
            Notify();
        }
        return result;
    }

    // Picks up changes an administrator made to this account or its role.
    public async Task RefreshAsync()
    {
        if (Actor == null || IsLocked)
            return;
        var fresh = await _auth.RefreshAsync(Actor);
        if (fresh == null)
        {
            Actor = null;
            await _store.ClearAsync();
            Notify();
            return;
        }
        await _store.SaveAsync(fresh.User.Id, _lastActivity);
        var changed = fresh.RoleNames != Actor.RoleNames || !fresh.Permissions.SameAs(Actor.Permissions)
                      || fresh.ScopeSummary != Actor.ScopeSummary;
        Actor = fresh;
        if (changed)
            Notify();
    }

    public async Task ToggleThemeAsync()
    {
        if (Actor == null)
            return;
        await _auth.SetThemeAsync(Actor, Actor.User.Theme == "dark" ? "light" : "dark");
        Notify();
    }

    public void Touch() => _lastActivity = Now;

    public void PasswordChanged() => Notify();

    private void CheckIdle()
    {
        if (Actor == null || IsLocked || Policy.SessionTimeoutMinutes <= 0)
            return;
        if (Now - _lastActivity < TimeSpan.FromMinutes(Policy.SessionTimeoutMinutes))
            return;
        IsLocked = true;
        _ = _auth.SessionLockedAsync(Actor);
        Notify();
    }

    private void Notify() => Changed?.Invoke();

    public void Dispose() => _idleTimer.Dispose();
}
