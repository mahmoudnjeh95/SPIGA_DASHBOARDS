using Spiga.Core.Audit;
using Spiga.Core.Plant;
using Spiga.Core.Security;

namespace Spiga.Tests;

// Platforms: named groups of machines. A role is given over a team and
// platforms; its holder works on those machines only.
public class PlatformTests
{
    private static PlatformChange Change(string name, params (string Id, string Sector)[] machines)
        => new(name, "", true, machines.Select(m => new PlatformMachine(m.Id, m.Sector)).ToList());

    [Fact]
    public async Task A_role_given_over_a_team_and_a_platform_opens_only_its_machines_for_that_team()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var ligne1 = await app.Admin.CreatePlatformAsync(admin, Change("Ligne PC01 + couscous", ("PC01", Sectors.PateCourte), ("CC01", Sectors.Couscous)));

        var chef = await app.UserAsync(admin, "chef.l1", "chef-equipe", DataScope.Of("Equipe A", new[] { ligne1 }));

        Assert.True(chef.Allows(Perm.DechetsValider, Sectors.PateCourte, "PC01", "Equipe A"));
        Assert.True(chef.Allows(Perm.DechetsSaisir, Sectors.Couscous, "CC01", "Equipe A"));
        Assert.False(chef.Allows(Perm.DechetsSaisir, Sectors.PateCourte, "PC02", "Equipe A"));
        Assert.False(chef.Allows(Perm.DechetsSaisir, Sectors.PateLongue, "PL01", "Equipe A"));
        Assert.False(chef.Allows(Perm.DechetsSaisir, Sectors.PateCourte, "PC01", "Equipe B"));
        // Sectors: open when the platform has a machine there.
        Assert.True(chef.Allows(Perm.DechetsSaisir, Sectors.PateCourte));
        Assert.False(chef.Allows(Perm.DechetsSaisir, Sectors.PateLongue));
        Assert.Equal("Ligne PC01 + couscous · Equipe A", chef.ScopeSummary);

        // The services filter the same way.
        var machines = await app.WasteService.MachinesFor(chef);
        Assert.Equal(new[] { "CC01", "PC01" }, machines.Select(m => m.Id).OrderBy(m => m));
    }

    [Fact]
    public async Task Changing_or_deactivating_a_platform_changes_the_access_of_its_holders()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var p = await app.Admin.CreatePlatformAsync(admin, Change("Pâte courte", ("PC01", Sectors.PateCourte)));
        await app.UserAsync(admin, "op.pc", "operateur", DataScope.Of("Equipe B", new[] { p }));

        await app.Admin.UpdatePlatformAsync(admin, p.Id, Change("Pâte courte", ("PC01", Sectors.PateCourte), ("PC02", Sectors.PateCourte)));
        var op = (await app.Auth.LoginAsync("op.pc", "Password2026x")).Actor!;
        Assert.True(op.Allows(Perm.DechetsSaisir, Sectors.PateCourte, "PC02", "Equipe B"));

        await app.Admin.UpdatePlatformAsync(admin, p.Id, Change("Pâte courte", ("PC01", Sectors.PateCourte)) with { IsActive = false });
        op = (await app.Auth.LoginAsync("op.pc", "Password2026x")).Actor!;
        Assert.False(op.Allows(Perm.DechetsSaisir, Sectors.PateCourte, "PC01", "Equipe B"));
        Assert.False(op.Allows(Perm.DechetsSaisir, Sectors.PateCourte));

        var audit = await app.AuditAsync();
        Assert.Contains(audit, e => e.Action == AuditActions.PlatformCreated && e.Target == "Pâte courte");
        Assert.Equal(2, audit.Count(e => e.Action == AuditActions.PlatformUpdated));
    }

    [Fact]
    public async Task Platforms_in_use_cannot_be_deleted_and_names_are_unique()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var p = await app.Admin.CreatePlatformAsync(admin, Change("Couscous", ("CC01", Sectors.Couscous)));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Admin.CreatePlatformAsync(admin, Change("couscous", ("CC01", Sectors.Couscous))));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Admin.CreatePlatformAsync(admin, Change("Vide")));

        await app.UserAsync(admin, "op.cc", "operateur", DataScope.Of("Equipe B", new[] { p }));
        Assert.Equal(1, (await app.Admin.ListPlatformsAsync(admin)).Single().AssignmentCount);
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Admin.DeletePlatformAsync(admin, p.Id));

        var other = await app.Admin.CreatePlatformAsync(admin, Change("Pâte longue", ("PL01", Sectors.PateLongue)));
        await app.Admin.DeletePlatformAsync(admin, other.Id);
        Assert.Single(await app.Admin.ListPlatformsAsync(admin));
    }

    [Fact]
    public async Task Only_holders_of_the_right_manage_platforms()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var prod = await app.UserAsync(admin, "resp.prod", "resp-production");
        Assert.False(prod.Can(Perm.PlateformesGerer));
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.Admin.CreatePlatformAsync(prod, Change("Couscous", ("CC01", Sectors.Couscous))));
        Assert.True(admin.Can(Perm.PlateformesGerer));
    }

    [Fact]
    public async Task Delegated_manager_gives_only_its_own_platforms()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var pc = await app.Admin.CreatePlatformAsync(admin, Change("Pâte courte", ("PC01", Sectors.PateCourte), ("PC02", Sectors.PateCourte)));
        var pc1 = await app.Admin.CreatePlatformAsync(admin, Change("PC01 seule", ("PC01", Sectors.PateCourte)));
        var cc = await app.Admin.CreatePlatformAsync(admin, Change("Couscous", ("CC01", Sectors.Couscous)));
        var prod = await app.UserAsync(admin, "resp.pc", "resp-production", DataScope.Of(null, new[] { pc }));
        var operateur = await app.RoleAsync("operateur");

        await app.Admin.CreateUserAsync(prod, new UserDraft("op.pc1", "Op", new[] { TestApp.As(operateur, DataScope.Of("Equipe A", new[] { pc1 })) }));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            app.Admin.CreateUserAsync(prod, new UserDraft("op.cc", "Op", new[] { TestApp.As(operateur, DataScope.Of("Equipe A", new[] { cc })) })));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            app.Admin.CreateUserAsync(prod, new UserDraft("op.all", "Op", new[] { TestApp.As(operateur, TestApp.Team("Equipe A")) })));
    }

    [Fact]
    public async Task The_service_uses_the_stored_platform_not_the_one_the_screen_sent()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var pc1 = await app.Admin.CreatePlatformAsync(admin, Change("PC01 seule", ("PC01", Sectors.PateCourte)));
        var forged = pc1.Clone();
        forged.Machines.Add(new PlatformMachine("PL01", Sectors.PateLongue));

        var op = await app.UserAsync(admin, "op.forge", "operateur", DataScope.Of("Equipe B", new[] { forged }));
        Assert.False(op.Allows(Perm.DechetsSaisir, Sectors.PateLongue, "PL01", "Equipe B"));

        var unknown = new Platform { Id = 999, Name = "?", Machines = { new PlatformMachine("PL01", Sectors.PateLongue) } };
        await Assert.ThrowsAsync<RuleViolationException>(() => app.UserAsync(admin, "op.inconnu", "operateur", DataScope.Of("Equipe B", new[] { unknown })));
    }

    [Fact]
    public void A_scope_never_widens_when_its_platform_is_missing()
    {
        var scope = DataScope.Of("Equipe A", new[] { Platform.Missing(7) });
        Assert.False(scope.IsUnrestricted);
        Assert.False(scope.AllowsMachine("PC01", Sectors.PateCourte));
        Assert.False(scope.AllowsSector(Sectors.PateCourte));
    }
}
