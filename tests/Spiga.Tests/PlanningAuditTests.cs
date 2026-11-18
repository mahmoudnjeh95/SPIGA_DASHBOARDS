using Dapper;
using Spiga.Core.Plant;
using Spiga.Core.Security;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;

namespace Spiga.Tests;

// Every planning change is in the audit trail with its author, written in
// the same transaction as the change.
public class PlanningAuditTests
{
    private static readonly ChangeAuthor Chef = new("chef.maint", "Chef maintenance", "PC-MAINT");

    [Fact]
    public async Task Orders_interventions_and_qualifications_are_audited_with_their_author()
    {
        using var app = new TestApp();
        using var live = new LiveDb();
        var planning = new PlanningRepository(app.AppData, live.Factory) { Author = Chef };

        var order = new ProductionOrder { OrderNo = "OF-2026-0001", MachineId = "PC01", Product = "Penne", QuantityBags = 1000,
            PlannedStart = new DateTime(2026, 11, 17, 6, 0, 0), PlannedEnd = new DateTime(2026, 11, 17, 14, 0, 0) };
        await planning.SaveOrderAsync(order);
        order.Status = PlanStatus.Done;
        await planning.SaveOrderAsync(order);

        var intervention = new MaintenanceEvent { MachineId = "PC01", Task = "Graissage", PlannedStart = new DateTime(2026, 11, 18, 6, 0, 0), DurationMinutes = 30 };
        await planning.SaveEventAsync(intervention);
        intervention.Status = PlanStatus.Cancelled;
        await planning.SaveEventAsync(intervention);

        var stop = new StopQualification { MachineId = "PC01", StopStart = new DateTime(2026, 11, 17, 9, 0, 0), StopEnd = new DateTime(2026, 11, 17, 9, 30, 0),
            ShiftDate = new DateTime(2026, 11, 17), ShiftId = 1, Team = "Equipe A", CauseCode = "PANNE" };
        await planning.SaveQualificationAsync(stop);
        await planning.DeleteQualificationAsync(stop.Id);

        var trail = (await app.AuditAsync()).Where(e => e.Module == Modules.Maintenance).OrderBy(e => e.Id).ToList();
        Assert.Equal(new[]
        {
            "Ordre de fabrication : création", "Ordre de fabrication : réalisé / validé",
            "Intervention de maintenance : création", "Intervention de maintenance : annulation",
            "Qualification d'arrêt : création", "Qualification d'arrêt : suppression",
        }, trail.Select(e => e.Action));
        Assert.All(trail, e => Assert.Equal(("chef.maint", "PC-MAINT"), (e.UserLogin, e.Workstation)));
        var done = trail[1];
        Assert.Contains("Statut : planifié", done.OldValue);
        Assert.Contains("Statut : réalisé", done.NewValue);
        Assert.Contains("Début : 17/11/2026 06:00", done.NewValue);
        Assert.Contains("Machine : PC01", trail[5].OldValue);
        Assert.Null(trail[5].NewValue);
    }

    [Fact]
    public async Task A_refused_change_leaves_no_trace_and_no_row()
    {
        using var app = new TestApp();
        using var live = new LiveDb();
        var access = MachineAccess.Nothing();
        access.Set("pc", (_, sector) => sector == "Pate courte");
        var planning = new PlanningRepository(app.AppData, live.Factory, access) { Author = Chef };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => planning.SaveOrderAsync(new ProductionOrder
        {
            OrderNo = "OF-2026-0001", MachineId = "CC01", Product = "Couscous", PlannedStart = DateTime.Today, PlannedEnd = DateTime.Today.AddHours(1),
        }));
        Assert.DoesNotContain(await app.AuditAsync(), e => e.Module == Modules.Maintenance);
        Assert.Empty(await new PlanningRepository(app.AppData, live.Factory).GetOrdersAsync(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1)));
    }

    [Fact]
    public async Task The_note_of_the_screens_needs_its_right_over_the_whole_plant()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var prod = await app.UserAsync(admin, "resp.prod", "resp-production");
        var pc = await app.Admin.CreatePlatformAsync(admin, new PlatformChange("Pâte courte", "", true, new[] { new PlatformMachine("PC01", Sectors.PateCourte) }));
        var limited = await app.UserAsync(admin, "resp.pc", "resp-production", DataScope.Of(null, new[] { pc }));

        Assert.True(admin.CanEverywhere(Perm.NoteEcran));
        Assert.True(prod.CanEverywhere(Perm.NoteEcran));
        Assert.True(limited.Can(Perm.NoteEcran));
        Assert.False(limited.CanEverywhere(Perm.NoteEcran));
    }

    [Fact]
    public async Task Upgrading_gives_the_note_right_to_the_roles_with_the_dashboard()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var role = await app.Admin.CreateRoleAsync(admin, "Tableau seul", "", null);
        await app.Admin.UpdateRoleAsync(admin, role.Id, new RoleChange(role.Name, "", true, new PermissionSet(new[] { Perm.TableauDetaille }), Array.Empty<int>()));

        // As on a site still in version 4.
        using (var c = await app.Db.OpenAsync())
            await c.ExecuteAsync("DELETE FROM SchemaVersion WHERE Version = 5");
        await new Spiga.Data.Migrator(app.Db).MigrateAsync();

        Assert.True((await app.Roles.GetAsync(role.Id))!.Permissions.Has(Perm.NoteEcran));
    }
}
