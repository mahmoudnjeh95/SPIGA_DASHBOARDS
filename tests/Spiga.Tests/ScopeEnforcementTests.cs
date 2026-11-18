using Dapper;
using Microsoft.Data.Sqlite;
using Spiga.Core.Plant;
using Spiga.Core.Security;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;

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

    [Fact]
    public async Task Repositories_list_read_and_write_only_the_allowed_machines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spiga-prod-{Guid.NewGuid():N}.db");
        try
        {
            using (var c = new SqliteConnection($"Data Source={path}"))
            {
                await c.ExecuteAsync(@"CREATE TABLE Machines (MachineId TEXT PRIMARY KEY, MachineName TEXT, MachineType TEXT, Sector TEXT, IPAddress TEXT, IsActive INTEGER);
                    INSERT INTO Machines VALUES ('PC01','PC01','','Pate courte','',1), ('CC01','CC01','','Couscous','',1);");
            }
            var factory = new SqliteConnectionFactory(path);
            var open = new PlanningRepository(factory);
            await open.EnsureSchemaAsync();
            var foreign = new ProductionOrder { OrderNo = "OF-1", MachineId = "CC01", PlannedStart = DateTime.Today, PlannedEnd = DateTime.Today.AddHours(8) };
            await open.SaveOrderAsync(foreign);
            await open.SaveOrderAsync(new ProductionOrder { OrderNo = "OF-2", MachineId = "PC01", PlannedStart = DateTime.Today, PlannedEnd = DateTime.Today.AddHours(8) });

            var access = MachineAccess.Nothing();
            access.Set("pc", (id, sector) => sector == "Pate courte");
            var production = new ProductionRepository(factory, new AppSettings(), access);
            var planning = new PlanningRepository(factory, access);

            Assert.Equal(new[] { "PC01" }, (await production.GetMachinesAsync()).Select(m => m.MachineId));
            Assert.Equal(new[] { "OF-2" }, (await planning.GetOrdersAsync(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1))).Select(o => o.OrderNo));
            Assert.Empty(await production.GetLotsAsync("CC01", DateTime.Today.AddDays(-1), DateTime.Today));
            Assert.Null(await production.GetLatestStatusAsync("CC01"));

            // Writing on another machine, or moving another machine's row to one's own: refused.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                planning.SaveOrderAsync(new ProductionOrder { OrderNo = "OF-3", MachineId = "CC01", PlannedStart = DateTime.Today, PlannedEnd = DateTime.Today.AddHours(1) }));
            foreign.MachineId = "PC01";
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => planning.SaveOrderAsync(foreign));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => production.UpdateMachineNameAsync("CC01", "x"));

            // Before an account gives its scope: nothing.
            Assert.Empty(await new ProductionRepository(factory, new AppSettings(), MachineAccess.Nothing()).GetMachinesAsync());
            // Kiosk / TV: every machine.
            Assert.Equal(2, (await new ProductionRepository(factory, new AppSettings()).GetMachinesAsync()).Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
