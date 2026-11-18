using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;
using Spiga.Core.Audit;
using Spiga.Core.Plant;
using Spiga.Core.Security;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;

namespace Spiga.Tests;

// A production database "stats" as on a site that ran version 2.0: the
// reference tables, and the tables 2.0 wrote there (planning, shared
// settings, note) in their older shape.
public sealed class LiveDb : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"spiga-stats-{Guid.NewGuid():N}.db");

    public LiveDb(bool withLegacyTables = false)
    {
        using var c = new SqliteConnection($"Data Source={_path}");
        c.Execute(@"CREATE TABLE Machines (MachineId TEXT PRIMARY KEY, MachineName TEXT, MachineType TEXT, Sector TEXT, IPAddress TEXT, IsActive INTEGER);
            INSERT INTO Machines VALUES ('PC01','Peseuse PC01','','Pate courte','',1), ('CC01','Contrôle CC01','','Couscous','',1);");
        if (!withLegacyTables)
            return;
        c.Execute(@"
            CREATE TABLE Notes (NoteId INTEGER PRIMARY KEY, Content TEXT, CreatedByEmployeeId INTEGER, CreatedAt TEXT, IsActive INTEGER);
            INSERT INTO Notes VALUES (1, 'ancienne', 1, '2026-11-01 08:00:00', 0), (2, 'Nettoyage ligne 2 à 14h', 1, '2026-11-10 08:00:00', 1);
            CREATE TABLE AppSharedSettings (Id INTEGER PRIMARY KEY, Version INTEGER, Json TEXT, UpdatedAt TEXT, UpdatedBy TEXT);
            INSERT INTO AppSharedSettings VALUES (1, 7, '{""x"":1}', '2026-11-10 08:00:00', 'PC-HALL');
            -- 2.0 before v1.5.14: no ExpectedEnd; before v1.5.8: no Kind.
            CREATE TABLE PlanStopQualifications (Id INTEGER PRIMARY KEY, MachineId TEXT, StopStart TEXT, StopEnd TEXT, ShiftDate TEXT, ShiftId INTEGER,
                Team TEXT, CauseCode TEXT, Component TEXT, Technician TEXT, Comment TEXT, IsManual INTEGER, CreatedAt TEXT);
            INSERT INTO PlanStopQualifications VALUES (5, 'PC01', '2026-11-10 09:00:00', '2026-11-10 09:30:00', '2026-11-10', 1, 'Equipe A', 'PANNE', NULL, NULL, NULL, 0, '2026-11-10 10:00:00');
            CREATE TABLE PlanMaintenancePlans (Id INTEGER PRIMARY KEY, MachineId TEXT, Task TEXT, Kind INTEGER, IntervalDays INTEGER, IntervalBags INTEGER,
                DurationMinutes INTEGER, LastDoneAt TEXT, IsActive INTEGER);
            INSERT INTO PlanMaintenancePlans VALUES (3, 'PC01', 'Graissage', 0, 7, 0, 30, NULL, 1);
            CREATE TABLE PlanMaintenanceEvents (Id INTEGER PRIMARY KEY, PlanId INTEGER, MachineId TEXT, Task TEXT, PlannedStart TEXT, DurationMinutes INTEGER,
                Status INTEGER, DoneAt TEXT, Technician TEXT, Comment TEXT);
            INSERT INTO PlanMaintenanceEvents VALUES (8, 3, 'PC01', 'Graissage', '2026-11-12 06:00:00', 30, 0, NULL, NULL, NULL);
            CREATE TABLE PlanProductionOrders (Id INTEGER PRIMARY KEY, OrderNo TEXT, MachineId TEXT, Product TEXT, TargetWeightGrams REAL, QuantityBags INTEGER,
                PlannedStart TEXT, PlannedEnd TEXT, Status INTEGER, Comment TEXT);
            INSERT INTO PlanProductionOrders VALUES
                (1, 'OF-2026-0001', 'PC01', 'Penne', 500, 1000, '2026-11-10 06:00:00', '2026-11-10 14:00:00', 0, NULL),
                (2, 'OF-2026-0001', 'CC01', 'Couscous', 1000, 500, '2026-11-11 06:00:00', '2026-11-11 14:00:00', 0, NULL);");
    }

    public IDbConnectionFactory Factory => new SqliteConnectionFactory(_path);

    public string Hash()
    {
        SqliteConnection.ClearAllPools();
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_path)));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }
}

// Version 4: stats is only read; planning, note, machine names and shared
// settings are in the application database.
public class PlantDataTests
{
    private static readonly ChangeAuthor Chef = new("chef.l1", "Chef ligne 1", "PC-ATELIER");

    private static ProductionOrder Order(string no, string machine)
        => new() { OrderNo = no, MachineId = machine, Product = "Penne", PlannedStart = new DateTime(2026, 11, 17, 6, 0, 0), PlannedEnd = new DateTime(2026, 11, 17, 14, 0, 0) };

    [Fact]
    public async Task Writing_planning_note_and_names_leaves_the_production_database_untouched()
    {
        using var app = new TestApp();
        using var live = new LiveDb();
        var before = live.Hash();

        var production = new ProductionRepository(live.Factory, app.AppData, new AppSettings());
        var planning = new PlanningRepository(app.AppData, live.Factory);
        await planning.SaveOrderAsync(Order(await planning.NextOrderNoAsync(), "PC01"));
        await planning.SaveQualificationAsync(new StopQualification { MachineId = "PC01", StopStart = DateTime.Today, StopEnd = DateTime.Today.AddMinutes(20),
            ShiftDate = DateTime.Today, ShiftId = 1, Team = "Equipe A", CauseCode = "PANNE" });
        await production.SetActiveNoteAsync("Nettoyage à 14h", Chef);
        await production.UpdateMachineNameAsync("PC01", "Peseuse ligne 1", Chef);
        await production.GetMachinesAsync();

        Assert.Equal(before, live.Hash());
        Assert.Single(await planning.GetOrdersAsync(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(2)));
    }

    [Fact]
    public async Task The_note_keeps_its_author_and_goes_to_the_audit_trail()
    {
        using var app = new TestApp();
        using var live = new LiveDb();
        var production = new ProductionRepository(live.Factory, app.AppData, new AppSettings());

        await production.SetActiveNoteAsync("Première note", ChangeAuthor.Kiosk);
        await production.SetActiveNoteAsync("Nettoyage à 14h", Chef);

        var note = (await production.GetActiveNoteAsync())!;
        Assert.Equal(("Nettoyage à 14h", "chef.l1", "Chef ligne 1", "PC-ATELIER"), (note.Content, note.CreatedBy, note.CreatedByName, note.Workstation));
        var entry = (await app.AuditAsync()).First(e => e.Action == AuditActions.NoteUpdated && e.UserLogin == "chef.l1");
        Assert.Equal(("Première note", "Nettoyage à 14h", "PC-ATELIER", Modules.Conditionnement), (entry.OldValue, entry.NewValue, entry.Workstation, entry.Module));
    }

    [Fact]
    public async Task A_machine_renamed_shows_its_new_name_and_is_audited_and_scoped()
    {
        using var app = new TestApp();
        using var live = new LiveDb();
        var production = new ProductionRepository(live.Factory, app.AppData, new AppSettings());

        await production.UpdateMachineNameAsync("PC01", "Peseuse ligne 1", Chef);
        Assert.Equal("Peseuse ligne 1", (await production.GetMachinesAsync()).Single(m => m.MachineId == "PC01").MachineName);
        var entry = (await app.AuditAsync()).Single(e => e.Action == AuditActions.MachineRenamed);
        Assert.Equal(("PC01", "Peseuse PC01", "Peseuse ligne 1", "chef.l1"), (entry.Target, entry.OldValue, entry.NewValue, entry.UserLogin));

        var access = MachineAccess.Nothing();
        access.Set("cc", (_, sector) => sector == "Couscous");
        var scoped = new ProductionRepository(live.Factory, app.AppData, new AppSettings(), access);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scoped.UpdateMachineNameAsync("PC01", "x", Chef));
    }

    [Fact]
    public async Task Order_numbers_stay_unique_across_PCs()
    {
        using var app = new TestApp();
        using var live = new LiveDb();
        var pc1 = new PlanningRepository(app.AppData, live.Factory);
        var pc2 = new PlanningRepository(app.AppData, live.Factory);
        var at = new DateTime(2026, 11, 17);

        // Both PCs propose the same number; the second gets the next one.
        var proposed1 = await pc1.NextOrderNoAsync(at);
        var proposed2 = await pc2.NextOrderNoAsync(at);
        Assert.Equal("OF-2026-0001", proposed1);
        Assert.Equal(proposed1, proposed2);
        var first = Order(proposed1, "PC01");
        var second = Order(proposed2, "CC01");
        await pc1.SaveOrderAsync(first);
        await pc2.SaveOrderAsync(second);
        Assert.Equal("OF-2026-0001", first.OrderNo);
        Assert.Equal("OF-2026-0002", second.OrderNo);
        Assert.Equal("OF-2026-0003", await pc1.NextOrderNoAsync(at));

        // A number typed by hand that is taken: refused, not renumbered.
        var manual = Order("LOT-A", "PC01");
        await pc1.SaveOrderAsync(manual);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pc2.SaveOrderAsync(Order("LOT-A", "CC01")));
        // Nor can an existing order take another's number.
        second.OrderNo = "OF-2026-0001";
        await Assert.ThrowsAsync<InvalidOperationException>(() => pc2.SaveOrderAsync(second));
    }

    [Fact]
    public async Task What_version_2_0_wrote_in_stats_is_copied_once()
    {
        using var app = new TestApp();
        using var live = new LiveDb(withLegacyTables: true);
        var before = live.Hash();

        var report = await new LegacyDataImporter(live.Factory, app.AppData).ImportAsync();
        Assert.Equal(6, report.Count);

        var planning = new PlanningRepository(app.AppData, live.Factory);
        var orders = await planning.GetOrdersAsync(new DateTime(2026, 11, 1), new DateTime(2026, 12, 1));
        Assert.Equal(new[] { "OF-2026-0001", "OF-2026-0001-2" }, orders.OrderBy(o => o.Id).Select(o => o.OrderNo));
        var events = await planning.GetEventsAsync(new DateTime(2026, 11, 1), new DateTime(2026, 12, 1));
        Assert.Equal((8, 3), (events.Single().Id, events.Single().PlanId));
        Assert.Equal(3, (await planning.GetPlansAsync()).Single().Id);
        var stop = (await planning.GetQualificationsAsync(new DateTime(2026, 11, 1), new DateTime(2026, 12, 1))).Single();
        Assert.Equal((5, (DateTime?)null), (stop.Id, stop.ExpectedEnd));
        Assert.Equal("OF-2026-0002", await planning.NextOrderNoAsync(new DateTime(2026, 11, 17)));
        var shared = await new SharedSettingsRepository(app.AppData).GetAsync();
        Assert.Equal(7, shared!.Version);
        var note = await new ProductionRepository(live.Factory, app.AppData, new AppSettings()).GetActiveNoteAsync();
        Assert.Equal("Nettoyage ligne 2 à 14h", note!.Content);

        // Once only: rows written later in stats (a PC on 2.0) are not copied again.
        using (var c = new SqliteConnection(live.Factory.CreateConnection().ConnectionString))
            await c.ExecuteAsync("INSERT INTO PlanProductionOrders VALUES (9, 'OF-2026-0099', 'PC01', 'Penne', 500, 1, '2026-11-12 06:00:00', '2026-11-12 07:00:00', 0, NULL)");
        await new LegacyDataImporter(live.Factory, app.AppData).ImportAsync();
        Assert.Equal(2, (await planning.GetOrdersAsync(new DateTime(2026, 11, 1), new DateTime(2026, 12, 1))).Count);
    }

    [Fact]
    public async Task Repositories_list_read_and_write_only_the_allowed_machines()
    {
        using var app = new TestApp();
        using var live = new LiveDb();
        var open = new PlanningRepository(app.AppData, live.Factory);
        var foreign = Order("OF-2026-0001", "CC01");
        await open.SaveOrderAsync(foreign);
        await open.SaveOrderAsync(Order("OF-2026-0002", "PC01"));

        var access = MachineAccess.Nothing();
        access.Set("pc", (_, sector) => sector == "Pate courte");
        var production = new ProductionRepository(live.Factory, app.AppData, new AppSettings(), access);
        var planning = new PlanningRepository(app.AppData, live.Factory, access);

        Assert.Equal(new[] { "PC01" }, (await production.GetMachinesAsync()).Select(m => m.MachineId));
        Assert.Equal(new[] { "OF-2026-0002" }, (await planning.GetOrdersAsync(DateTime.Today.AddDays(-30), DateTime.Today.AddDays(30))).Select(o => o.OrderNo));
        Assert.Empty(await production.GetLotsAsync("CC01", DateTime.Today.AddDays(-1), DateTime.Today));
        Assert.Null(await production.GetLatestStatusAsync("CC01"));

        // Writing on another machine, or moving another machine's row to one's own: refused.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => planning.SaveOrderAsync(Order("OF-2026-0003", "CC01")));
        foreign.MachineId = "PC01";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => planning.SaveOrderAsync(foreign));

        // Before an account gives its scope: nothing. Kiosk / TV: every machine.
        Assert.Empty(await new ProductionRepository(live.Factory, app.AppData, new AppSettings(), MachineAccess.Nothing()).GetMachinesAsync());
        Assert.Equal(2, (await new ProductionRepository(live.Factory, app.AppData, new AppSettings()).GetMachinesAsync()).Count);
    }

    [Fact]
    public async Task A_delegated_manager_sees_only_the_accounts_inside_its_scope()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var pc = await app.Admin.CreatePlatformAsync(admin, new PlatformChange("Pâte courte", "", true, new[] { new PlatformMachine("PC01", Sectors.PateCourte) }));
        var cc = await app.Admin.CreatePlatformAsync(admin, new PlatformChange("Couscous", "", true, new[] { new PlatformMachine("CC01", Sectors.Couscous) }));
        var manager = await app.UserAsync(admin, "resp.pc", "resp-production", DataScope.Of(null, new[] { pc }));
        await app.UserAsync(admin, "op.pc", "operateur", DataScope.Of("Equipe A", new[] { pc }));
        await app.UserAsync(admin, "op.cc", "operateur", DataScope.Of("Equipe A", new[] { cc }));

        Assert.Equal(new[] { "op.pc", "resp.pc" }, (await app.Admin.ListUsersAsync(manager)).Select(u => u.Login).OrderBy(l => l));
        Assert.Equal(4, (await app.Admin.ListUsersAsync(admin)).Count);
    }

    [Fact]
    public async Task The_audit_trail_is_read_only_with_its_right_over_the_whole_plant()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var trail = new AuditTrailService(app.Audit);
        Assert.NotEmpty(await trail.QueryAsync(admin, new AuditQuery(DateTime.MinValue, DateTime.MaxValue)));

        var reader = await app.Admin.CreateRoleAsync(admin, "Lecteur du journal", "", null);
        await app.Admin.UpdateRoleAsync(admin, reader.Id, new RoleChange(reader.Name, "", true, new PermissionSet(new[] { Perm.View(Modules.Audit) }), Array.Empty<int>()));
        var limited = await app.UserAsync(admin, "lecteur", TestApp.As((await app.Roles.GetAsync(reader.Id))!, TestApp.Team("Equipe A")));
        await Assert.ThrowsAsync<AccessDeniedException>(() => trail.QueryAsync(limited, new AuditQuery(DateTime.MinValue, DateTime.MaxValue)));
    }
}
