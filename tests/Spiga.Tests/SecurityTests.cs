using Spiga.Core.Audit;
using Spiga.Core.Security;
using Spiga.Data;

namespace Spiga.Tests;

public class SecurityTests
{
    [Fact]
    public void Password_hash_verifies_only_the_right_password()
    {
        var hash = PasswordHasher.Hash("Spiga2026pass");
        Assert.True(PasswordHasher.Verify("Spiga2026pass", hash));
        Assert.False(PasswordHasher.Verify("spiga2026pass", hash));
        Assert.False(PasswordHasher.Verify("Spiga2026pass", "not-a-hash"));
        Assert.NotEqual(hash, PasswordHasher.Hash("Spiga2026pass"));
    }

    [Fact]
    public void Permissions_imply_voir_and_drop_actions_the_module_does_not_offer()
    {
        var set = new PermissionSet();
        set.Set(Modules.SaisieDechets, AppAction.Valider);
        set.Set(Modules.VueEnsemble, AppAction.Administrer);
        Assert.True(set.Can(Modules.SaisieDechets, AppAction.Voir));
        Assert.True(set.Can(Modules.SaisieDechets, AppAction.Valider));
        Assert.False(set.Can(Modules.SaisieDechets, AppAction.Saisir));
        Assert.False(set.Can(Modules.VueEnsemble, AppAction.Voir));
    }

    [Fact]
    public async Task Migration_runs_once_and_seeds_the_default_roles()
    {
        using var app = new TestApp();
        Assert.Equal(0, await new Migrator(app.Db).MigrateAsync());
        var roles = await app.Roles.ListAsync();
        Assert.Equal(DefaultRoles.Create().Count, roles.Count);
        Assert.Single(roles, r => r.IsSystem);
        var chef = roles.Single(r => r.Code == "chef-equipe");
        Assert.True(chef.Permissions.Can(Modules.SaisieDechets, AppAction.Valider));
        Assert.False(chef.Permissions.Can(Modules.Utilisateurs, AppAction.Voir));
    }

    [Fact]
    public async Task First_setup_creates_the_administrator_once()
    {
        using var app = new TestApp();
        Assert.True(await app.Auth.NeedsSetupAsync());
        var admin = await app.SetupAdminAsync();
        Assert.True(admin.Can(Modules.Utilisateurs, AppAction.Administrer));
        Assert.False(await app.Auth.NeedsSetupAsync());
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Auth.CreateFirstAdminAsync("other", "Other", "Another2026pass"));
    }

    [Fact]
    public async Task Weak_password_is_refused_at_setup()
    {
        using var app = new TestApp();
        var error = await Assert.ThrowsAsync<RuleViolationException>(() => app.Auth.CreateFirstAdminAsync("admin", "Admin", "short1"));
        Assert.Contains("10 caractères", error.Message);
    }

    [Fact]
    public async Task Account_locks_after_the_allowed_failures_until_an_administrator_unlocks_it()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var op = await app.UserAsync(admin, "operateur.pc", "operateur");

        for (var i = 0; i < 4; i++)
            Assert.Equal(LoginStatus.InvalidCredentials, (await app.Auth.LoginAsync("operateur.pc", "wrong")).Status);
        Assert.Equal(LoginStatus.Locked, (await app.Auth.LoginAsync("operateur.pc", "wrong")).Status);

        // Even the right password is refused while locked, a day later too.
        app.Clock.Now = app.Clock.Now.AddDays(1);
        Assert.Equal(LoginStatus.Locked, (await app.Auth.LoginAsync("operateur.pc", "Password2026x")).Status);

        await app.Admin.UnlockAsync(admin, op.User.Id);
        Assert.Equal(LoginStatus.Success, (await app.Auth.LoginAsync("operateur.pc", "Password2026x")).Status);

        var journal = await app.AuditAsync();
        Assert.Contains(journal, e => e.Action == AuditActions.AccountLocked && e.Target == "operateur.pc");
        Assert.Contains(journal, e => e.Action == AuditActions.AccountUnlocked && e.UserLogin == "admin");
    }

    [Fact]
    public async Task Unknown_login_and_wrong_password_give_the_same_answer()
    {
        using var app = new TestApp();
        await app.SetupAdminAsync();
        var unknown = await app.Auth.LoginAsync("nobody", "whatever");
        var wrong = await app.Auth.LoginAsync("admin", "whatever");
        Assert.Equal(unknown.Status, wrong.Status);
        Assert.Equal(unknown.Message, wrong.Message);
    }

    [Fact]
    public async Task New_user_must_change_the_temporary_password()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var role = await app.Roles.GetByCodeAsync("operateur");
        var temporary = await app.Admin.CreateUserAsync(admin, new UserDraft("Op.CC", "Opérateur CC", role!.Id, DataScope.Everything));
        var login = await app.Auth.LoginAsync("op.cc", temporary);
        Assert.Equal(LoginStatus.Success, login.Status);
        Assert.True(login.MustChangePassword);
    }

    [Fact]
    public async Task Disabled_account_cannot_sign_in()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var op = await app.UserAsync(admin, "operateur.pc", "operateur");
        await app.Admin.UpdateUserAsync(admin, op.User.Id, new UserDraft(op.Login, op.DisplayName, op.User.RoleId, op.Scope, IsActive: false));
        Assert.Equal(LoginStatus.Disabled, (await app.Auth.LoginAsync("operateur.pc", "Password2026x")).Status);
    }

    [Fact]
    public async Task The_application_never_loses_its_last_administrator()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();

        // An administrator cannot disable or demote themselves.
        await Assert.ThrowsAsync<RuleViolationException>(() =>
            app.Admin.UpdateUserAsync(admin, admin.User.Id, new UserDraft("admin", "Admin", admin.User.RoleId, DataScope.Everything, IsActive: false)));

        // A custom admin role cannot drop its Administrer right if it holds the last active administrator.
        var custom = await app.Admin.CreateRoleAsync(admin, "Admin site", "", copyFromRoleId: admin.Role.Id);
        var second = await app.UserAsync(admin, "admin.site", custom.Code);
        await app.Admin.UpdateUserAsync(second, admin.User.Id, new UserDraft("admin", "Admin", admin.User.RoleId, DataScope.Everything, IsActive: false));
        var stripped = custom.Permissions.With(Modules.Utilisateurs, AppAction.Voir);
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Admin.UpdateRoleAsync(second, custom.Id, custom.Name, "", stripped));
    }

    [Fact]
    public async Task System_role_is_read_only_and_role_changes_are_audited()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        await Assert.ThrowsAsync<RuleViolationException>(() =>
            app.Admin.UpdateRoleAsync(admin, admin.Role.Id, "Admin", "", new PermissionSet()));

        var chef = (await app.Roles.GetByCodeAsync("chef-equipe"))!;
        await app.Admin.UpdateRoleAsync(admin, chef.Id, chef.Name, chef.Description, chef.Permissions.With(Modules.Qualite, AppAction.Voir));
        Assert.True((await app.Roles.GetAsync(chef.Id))!.Permissions.Can(Modules.Qualite, AppAction.Voir));

        var entry = Assert.Single(await app.AuditAsync(), e => e.Action == AuditActions.RoleUpdated);
        Assert.Contains("Qualité : Aucun accès → Voir", entry.NewValue);
    }

    [Fact]
    public async Task Without_the_right_the_service_refuses_even_if_the_screen_is_bypassed()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var chef = await app.UserAsync(admin, "chef.b", "chef-equipe");
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.Admin.ListUsersAsync(chef));
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.Admin.ResetPasswordAsync(chef, admin.User.Id));
    }

    [Fact]
    public async Task Reloaded_page_resumes_only_a_recent_session_of_an_active_account()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var op = await app.UserAsync(admin, "operateur.pc", "operateur");
        var now = app.Clock.Now;

        Assert.NotNull(await app.Auth.ResumeAsync(op.User.Id, now.AddMinutes(-5)));
        // Past the 15 min inactivity limit: the screen would have locked, so no resume.
        Assert.Null(await app.Auth.ResumeAsync(op.User.Id, now.AddMinutes(-16)));

        await app.Admin.UpdateUserAsync(admin, op.User.Id, new UserDraft(op.Login, op.DisplayName, op.User.RoleId, op.Scope, IsActive: false));
        Assert.Null(await app.Auth.ResumeAsync(op.User.Id, now.AddMinutes(-1)));
    }

    [Fact]
    public async Task Security_policy_is_stored_and_applied()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var policy = await app.Admin.GetPolicyAsync(admin);
        policy.MaxFailedAttempts = 3;
        policy.LockoutMinutes = 30;
        await app.Admin.SavePolicyAsync(admin, policy);

        var op = await app.UserAsync(admin, "operateur.pc", "operateur");
        for (var i = 0; i < 3; i++)
            await app.Auth.LoginAsync("operateur.pc", "wrong");
        Assert.Equal(LoginStatus.Locked, (await app.Auth.LoginAsync("operateur.pc", "Password2026x")).Status);
        app.Clock.Now = app.Clock.Now.AddMinutes(31);
        Assert.Equal(LoginStatus.Success, (await app.Auth.LoginAsync("operateur.pc", "Password2026x")).Status);
    }
}
