using Spiga.Core.Audit;
using Spiga.Core.Plant;
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
    public void A_permission_implies_seeing_its_module_and_removing_voir_removes_the_module()
    {
        var set = new PermissionSet();
        set.Set(Perm.DechetsCorriger, true);
        Assert.True(set.Can(Modules.SaisieDechets, AppAction.Voir));
        Assert.False(set.Has(Perm.DechetsSaisir));

        set.Set(Perm.DechetsSaisir, true);
        set.Set(Perm.View(Modules.SaisieDechets), false);
        Assert.Equal(0, set.Count);

        set.Set("vue-ensemble:administrer", true);
        set.Set("module-inconnu:voir", true);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void Every_navigation_entry_and_every_named_permission_is_in_the_catalogue()
    {
        foreach (var nav in ModuleCatalog.Navigation)
            Assert.NotNull(PermissionCatalog.Find(Perm.View(nav.Module)));
        foreach (var key in new[] { Perm.DechetsSaisir, Perm.DechetsValider, Perm.DechetsCorriger, Perm.DechetsTardif, Perm.DechetsAutrui,
                     Perm.ComptesGerer, Perm.ComptesDebloquer, Perm.RolesGerer, Perm.SecuriteGerer, Perm.TableauDetaille, Perm.EcranTv, Perm.ParametresPoste, Perm.EquipesMultiples })
            Assert.NotNull(PermissionCatalog.Find(key));
        foreach (var t in RoleTemplates.All)
            Assert.All(t.Permissions, p => Assert.NotNull(PermissionCatalog.Find(p)));
    }

    [Fact]
    public async Task Only_the_administrator_role_exists_until_templates_are_asked_for()
    {
        using var app = new TestApp();
        Assert.Equal(0, await new Migrator(app.Db).MigrateAsync());
        var roles = await app.Roles.ListAsync();
        Assert.True(Assert.Single(roles).IsSystem);

        var admin = await app.SetupAdminAsync(templates: false);
        Assert.Single(await app.Roles.ListAsync());
        Assert.Equal(RoleTemplates.All.Count, await app.Admin.AddTemplatesAsync(admin));
        Assert.Equal(0, await app.Admin.AddTemplatesAsync(admin));

        var prod = await app.RoleAsync("resp-production");
        Assert.Equal(new[] { "chef-equipe", "operateur" },
            (await app.Roles.ListAsync()).Where(r => prod.AssignableRoleIds.Contains(r.Id)).Select(r => r.Code).OrderBy(c => c));
    }

    [Fact]
    public async Task Administrators_build_any_number_of_roles_from_the_catalogue()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync(templates: false);
        for (var i = 1; i <= 12; i++)
            await app.Admin.CreateRoleAsync(admin, $"Rôle atelier {i}", "", null);
        var magasin = await app.Admin.CreateRoleAsync(admin, "Magasinier", "Stock silos", null);
        var rights = new PermissionSet(new[] { "silos:saisir", "silos:exporter", Perm.View(Modules.Rendement) });
        await app.Admin.UpdateRoleAsync(admin, magasin.Id, new RoleChange("Magasinier silos", "Stock silos", true, rights, Array.Empty<int>()));

        var saved = (await app.Roles.GetAsync(magasin.Id))!;
        Assert.Equal("Magasinier silos", saved.Name);
        Assert.True(saved.Permissions.Has("silos:voir"));
        Assert.False(saved.Permissions.Has("silos:valider"));
        Assert.Equal(14, (await app.Roles.ListAsync()).Count);

        var change = Assert.Single(await app.AuditAsync(), e => e.Action == AuditActions.RoleUpdated);
        Assert.Contains("+ Silos › Mettre à jour niveau et produit", change.NewValue);
    }

    [Fact]
    public async Task First_setup_creates_the_administrator_once()
    {
        using var app = new TestApp();
        Assert.True(await app.Auth.NeedsSetupAsync());
        var admin = await app.SetupAdminAsync();
        Assert.True(admin.IsAdministrator);
        Assert.True(admin.Can(Perm.RolesGerer));
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
    public async Task A_user_holds_several_roles_each_over_its_own_scope()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        // Chef d'équipe for Equipe B everywhere, and operator on couscous (Equipe B too: one team only).
        var user = await app.UserAsync(admin, "polyvalent",
            TestApp.As(await app.RoleAsync("chef-equipe"), TestApp.Team("Equipe B")),
            TestApp.As(await app.RoleAsync("operateur"), TestApp.Sector(Sectors.Couscous)));

        Assert.Equal("Chef d'équipe, Opérateur de saisie", user.RoleNames);
        Assert.True(user.Allows(Perm.DechetsValider, Sectors.PateCourte, "PC01", "Equipe B"));
        Assert.False(user.Allows(Perm.DechetsValider, Sectors.PateCourte, "PC01", "Equipe A"));
        Assert.True(user.Allows(Perm.DechetsSaisir, Sectors.Couscous, "CC01", "Equipe B"));
        Assert.False(user.Allows(Perm.DechetsSaisir, Sectors.Couscous, "CC01", "Equipe A"));
        Assert.Equal("Equipe B", user.Team);
    }

    [Fact]
    public async Task Only_responsables_cover_several_teams()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var chef = await app.RoleAsync("chef-equipe");
        var operateur = await app.RoleAsync("operateur");
        var all = new RoleAssignment { RoleId = chef.Id, RoleName = chef.Name, Scope = DataScope.Everything };
        await Assert.ThrowsAsync<RuleViolationException>(() =>
            app.Admin.CreateUserAsync(admin, new UserDraft("chef.all", "Chef", new[] { all })));
        await Assert.ThrowsAsync<RuleViolationException>(() =>
            app.Admin.CreateUserAsync(admin, new UserDraft("chef.ab", "Chef", new[]
            {
                TestApp.As(chef, TestApp.Team("Equipe A")), TestApp.As(operateur, TestApp.Team("Equipe B")),
            })));

        var prod = await app.UserAsync(admin, "resp.prod", "resp-production");
        Assert.Null(prod.Team);
        Assert.True(prod.IsOwnTeam("Equipe A"));
        var chefA = await app.UserAsync(admin, "chef.a", "chef-equipe", TestApp.Team("Equipe A"));
        Assert.Equal("Equipe A", chefA.Team);
        Assert.False(chefA.IsOwnTeam("Equipe B"));
    }

    [Fact]
    public async Task Assignments_apply_only_during_their_period_and_while_the_role_is_active()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var chef = await app.RoleAsync("chef-equipe");
        var today = app.Clock.Now.Date;
        var temp = await app.UserAsync(admin, "interim", TestApp.As(chef, from: today.AddDays(-2), to: today.AddDays(3)));
        Assert.True(temp.Can(Perm.DechetsValider));

        app.Clock.Now = app.Clock.Now.AddDays(4);
        var late = await app.Auth.LoginAsync("interim", "Password2026x");
        Assert.Equal(LoginStatus.NoRole, late.Status);

        app.Clock.Now = app.Clock.Now.AddDays(-4);
        await app.Admin.UpdateRoleAsync(admin, chef.Id, new RoleChange(chef.Name, chef.Description, false, chef.Permissions, chef.AssignableRoleIds));
        Assert.Equal(LoginStatus.NoRole, (await app.Auth.LoginAsync("interim", "Password2026x")).Status);
    }

    [Fact]
    public async Task Delegated_manager_gives_only_its_assignable_roles_inside_its_own_scope()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var prod = await app.UserAsync(admin, "resp.pc", "resp-production", TestApp.Sector(Sectors.PateCourte));
        var operateur = await app.RoleAsync("operateur");

        Assert.Equal(new[] { "Chef d'équipe", "Opérateur de saisie" },
            (await app.Admin.AssignableRolesAsync(prod)).Select(r => r.Name).OrderBy(n => n));

        await app.Admin.CreateUserAsync(prod, new UserDraft("op.pc", "Opérateur PC", new[] { TestApp.As(operateur, TestApp.Sector(Sectors.PateCourte)) }));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            app.Admin.CreateUserAsync(prod, new UserDraft("op.cc", "Opérateur CC", new[] { TestApp.As(operateur, TestApp.Sector(Sectors.Couscous)) })));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            app.Admin.CreateUserAsync(prod, new UserDraft("op.all", "Opérateur", new[] { TestApp.As(operateur) })));
        var direction = await app.RoleAsync("direction");
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            app.Admin.CreateUserAsync(prod, new UserDraft("dir", "Directeur", new[] { TestApp.As(direction, TestApp.Sector(Sectors.PateCourte)) })));

        // It cannot touch an account holding a role it does not manage.
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            app.Admin.UpdateUserAsync(prod, admin.User.Id, new UserDraft("admin", "Admin", new[] { TestApp.As(operateur, TestApp.Sector(Sectors.PateCourte)) })));
        // Nor define roles.
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.Admin.CreateRoleAsync(prod, "Mon rôle", "", null));
    }

    [Fact]
    public async Task Only_an_administrator_gives_the_administrator_role()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var roleManager = await app.Admin.CreateRoleAsync(admin, "Gestionnaire des accès", "", null);
        await app.Admin.UpdateRoleAsync(admin, roleManager.Id, new RoleChange(roleManager.Name, "", true,
            new PermissionSet(new[] { Perm.RolesGerer, Perm.ComptesGerer }), Array.Empty<int>()));
        var manager = await app.UserAsync(admin, "rh.acces", TestApp.As((await app.Roles.GetAsync(roleManager.Id))!));

        Assert.DoesNotContain(await app.Admin.AssignableRolesAsync(manager), r => r.IsSystem);
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            app.Admin.CreateUserAsync(manager, new UserDraft("pirate", "X", new[] { TestApp.As(admin.Grants[0].Role) })));
        Assert.False(await app.Admin.CanManageAsync(manager, admin.User));
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
    public async Task New_user_must_change_the_temporary_password_and_needs_a_role()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var role = await app.RoleAsync("operateur");
        await Assert.ThrowsAsync<RuleViolationException>(() =>
            app.Admin.CreateUserAsync(admin, new UserDraft("sans.role", "Sans rôle", Array.Empty<RoleAssignment>())));
        var temporary = await app.Admin.CreateUserAsync(admin, new UserDraft("Op.CC", "Opérateur CC", new[] { TestApp.As(role) }));
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
        await app.Admin.UpdateUserAsync(admin, op.User.Id, new UserDraft(op.Login, op.DisplayName, op.User.Assignments, IsActive: false));
        Assert.Equal(LoginStatus.Disabled, (await app.Auth.LoginAsync("operateur.pc", "Password2026x")).Status);
    }

    [Fact]
    public async Task The_application_never_loses_its_last_administrator()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();

        // Nobody disables or demotes themselves.
        await Assert.ThrowsAsync<RuleViolationException>(() =>
            app.Admin.UpdateUserAsync(admin, admin.User.Id, new UserDraft("admin", "Admin", admin.User.Assignments, IsActive: false)));

        // A second administrator can disable the first one, and is then the last.
        var second = await app.UserAsync(admin, "admin.site", RoleTemplates.AdminCode);
        await app.Admin.UpdateUserAsync(second, admin.User.Id, new UserDraft("admin", "Admin", admin.User.Assignments, IsActive: false));
        await Assert.ThrowsAsync<RuleViolationException>(() =>
            app.Admin.UpdateUserAsync(second, second.User.Id, new UserDraft("admin.site", "Admin site", second.User.Assignments, IsActive: false)));

        // A custom role holding "Gérer les rôles" + "Créer des comptes" is not
        // an administrator: it cannot touch administrator accounts.
        var custom = await app.Admin.CreateRoleAsync(second, "Gestion des accès", "", copyFromRoleId: second.Grants[0].Role.Id);
        var manager = await app.UserAsync(second, "acces", custom.Code);
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            app.Admin.UpdateUserAsync(manager, second.User.Id, new UserDraft("admin.site", "Admin site", second.User.Assignments, IsActive: false)));
    }

    [Fact]
    public async Task System_role_is_read_only_and_roles_in_use_cannot_be_deleted()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        await Assert.ThrowsAsync<RuleViolationException>(() =>
            app.Admin.UpdateRoleAsync(admin, admin.Grants[0].Role.Id, new RoleChange("Admin", "", true, new PermissionSet(), Array.Empty<int>())));
        await app.UserAsync(admin, "chef.b", "chef-equipe");
        var chef = await app.RoleAsync("chef-equipe");
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Admin.DeleteRoleAsync(admin, chef.Id));
        var unused = await app.RoleAsync("resp-rh");
        await app.Admin.DeleteRoleAsync(admin, unused.Id);
        Assert.Null(await app.Roles.GetAsync(unused.Id));
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
        Assert.Null(await app.Auth.ResumeAsync(op.User.Id, now.AddMinutes(-16)));

        await app.Admin.UpdateUserAsync(admin, op.User.Id, new UserDraft(op.Login, op.DisplayName, op.User.Assignments, IsActive: false));
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

        await app.UserAsync(admin, "operateur.pc", "operateur");
        for (var i = 0; i < 3; i++)
            await app.Auth.LoginAsync("operateur.pc", "wrong");
        Assert.Equal(LoginStatus.Locked, (await app.Auth.LoginAsync("operateur.pc", "Password2026x")).Status);
        app.Clock.Now = app.Clock.Now.AddMinutes(31);
        Assert.Equal(LoginStatus.Success, (await app.Auth.LoginAsync("operateur.pc", "Password2026x")).Status);
    }
}
