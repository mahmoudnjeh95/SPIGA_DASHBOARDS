using Spiga.Core.Plant;
using Spiga.Core.Security;

namespace Spiga.Tests;

// What the WPF pages (dashboard, arrêts, planification) get: the machines of
// the right the page needs, enforced by the repositories they read through.
public class ScopeEnforcementTests
{
    private static PlatformChange Change(string name, params (string Id, string Sector)[] machines)
        => new(name, "", true, machines.Select(m => new PlatformMachine(m.Id, m.Sector)).ToList());

    [Fact]
    public async Task Each_right_has_its_own_machines_and_buttons_need_a_right_covering_the_page()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var pc = await app.Admin.CreatePlatformAsync(admin, Change("Pâte courte", ("PC01", Sectors.PateCourte), ("PC02", Sectors.PateCourte)));
        var chef = await app.UserAsync(admin, "chef.pc", "chef-equipe", DataScope.Of("Equipe A", new[] { pc }));

        var rule = chef.MachineRuleFor(Perm.TableauDetaille)!;
        Assert.True(rule.Allows("PC01", Sectors.PateCourte));
        Assert.False(rule.Allows("CC01", Sectors.Couscous));
        // Same scope for the dashboard and its export.
        Assert.True(chef.Covers("conditionnement:exporter", Perm.TableauDetaille));
        Assert.False(chef.Covers("maintenance:exporter", Perm.TableauDetaille));
        // Station-wide outputs: not for a limited scope.
        Assert.False(chef.CanEverywhere(Perm.TableauDetaille));

        Assert.Null(admin.MachineRuleFor(Perm.TableauDetaille));
        Assert.True(admin.CanEverywhere(Perm.ParametresPoste));
        Assert.True(admin.CanEverywhere(Perm.EcranTv));
        // The audit trail: whole plant and every team.
        Assert.True(admin.CanUnrestricted(Perm.View(Modules.Audit)));
        Assert.False(chef.CanUnrestricted(Perm.TableauDetaille));
    }

    [Fact]
    public async Task An_export_on_a_smaller_scope_than_the_page_does_not_cover_it()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var both = await app.Admin.CreatePlatformAsync(admin, Change("PC + CC", ("PC01", Sectors.PateCourte), ("CC01", Sectors.Couscous)));
        var pcOnly = await app.Admin.CreatePlatformAsync(admin, Change("Pâte courte PC01", ("PC01", Sectors.PateCourte)));
        var viewer = await app.Admin.CreateRoleAsync(admin, "Lecteur tableau", "", null);
        await app.Admin.UpdateRoleAsync(admin, viewer.Id, new RoleChange(viewer.Name, "", true, new PermissionSet(new[] { Perm.TableauDetaille }), Array.Empty<int>()));
        var exporter = await app.Admin.CreateRoleAsync(admin, "Exporteur", "", null);
        await app.Admin.UpdateRoleAsync(admin, exporter.Id, new RoleChange(exporter.Name, "", true, new PermissionSet(new[] { "conditionnement:exporter" }), Array.Empty<int>()));

        var user = await app.UserAsync(admin, "mixte",
            TestApp.As(await app.Roles.GetAsync(viewer.Id) ?? viewer, DataScope.Of("Equipe B", new[] { both })),
            TestApp.As(await app.Roles.GetAsync(exporter.Id) ?? exporter, DataScope.Of("Equipe B", new[] { pcOnly })));

        Assert.True(user.Can("conditionnement:exporter"));
        Assert.False(user.Covers("conditionnement:exporter", Perm.TableauDetaille));
    }

    [Fact]
    public async Task Scope_key_changes_when_a_platform_changes_so_the_pages_reload()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var p = await app.Admin.CreatePlatformAsync(admin, Change("Pâte courte PC01", ("PC01", Sectors.PateCourte)));
        var before = await app.UserAsync(admin, "op.pc", "operateur", DataScope.Of("Equipe B", new[] { p }));
        await app.Admin.UpdatePlatformAsync(admin, p.Id, Change("Pâte courte PC01", ("PC01", Sectors.PateCourte), ("PC02", Sectors.PateCourte)));
        var after = (await app.Auth.RefreshAsync(before))!;

        Assert.NotEqual(before.AccessKey, after.AccessKey);
        Assert.NotEqual(before.MachineRuleFor(Perm.DechetsSaisir)!.Key, after.MachineRuleFor(Perm.DechetsSaisir)!.Key);
        Assert.Equal(before.ScopeSummary, after.ScopeSummary);
    }
}
