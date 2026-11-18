using System.Data;
using Dapper;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.Data;

// The Planification window's own tables (Plan*), in the same database as the
// app's other tables (Machines, Notes) so every PC sees the same pannes and
// planning. Created on first use (idempotent); the Stiavelli tables are never
// written. The same DDL, for a DBA, is in database/planning.sql.
public class PlanningRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private bool _schemaReady;

    // access: the machines the signed-in account may see and plan (SPIGA
    // Suivi Production). Not given = every machine (TRS reads, kiosk).
    public PlanningRepository(IDbConnectionFactory connectionFactory, MachineAccess? access = null)
    {
        _connectionFactory = connectionFactory;
        Access = access ?? MachineAccess.Everything();
    }

    // Every read below leaves out the machines outside it; every write on one
    // of them (or on a row that belongs to one) is refused.
    public MachineAccess Access { get; }

    private Task<Func<string, bool>> AllowedAsync() => Access.FilterAsync(_connectionFactory);

    private Task DemandAsync(string machineId) => Access.DemandAsync(_connectionFactory, machineId);

    // An update: the row as stored must be in scope too (no moving another
    // machine's row into one's own).
    private async Task DemandStoredAsync(IDbConnection connection, string table, int id)
    {
        if (!Access.IsRestricted)
            return;
        var stored = await connection.ExecuteScalarAsync<string?>($"SELECT MachineId FROM {table} WHERE Id = @id", new { id });
        if (stored != null)
            await DemandAsync(stored);
    }

    private bool IsSqlServer => _connectionFactory.Provider == DatabaseProvider.SqlServer;

    private string IdentitySelect => IsSqlServer ? "SELECT CAST(SCOPE_IDENTITY() AS INT)" : "SELECT last_insert_rowid()";

    public async Task EnsureSchemaAsync()
    {
        if (_schemaReady)
            return;
        using var connection = _connectionFactory.CreateConnection();
        foreach (var statement in IsSqlServer ? SqlServerSchema : SqliteSchema)
            await connection.ExecuteAsync(statement);
        // Planned stops (v1.5.8): tables made before have no Kind column.
        if (!await HasKindColumnAsync(connection))
            await connection.ExecuteAsync(IsSqlServer
                ? "ALTER TABLE dbo.PlanMaintenanceEvents ADD Kind INT NOT NULL CONSTRAINT DF_PlanMaintenanceEvents_Kind DEFAULT 0"
                : "ALTER TABLE PlanMaintenanceEvents ADD COLUMN Kind INTEGER NOT NULL DEFAULT 0");
        // Expected return of a panne / planned stop (v1.5.14).
        if (!await HasExpectedEndColumnAsync(connection))
            await connection.ExecuteAsync(IsSqlServer
                ? "ALTER TABLE dbo.PlanStopQualifications ADD ExpectedEnd DATETIME2(0) NULL"
                : "ALTER TABLE PlanStopQualifications ADD COLUMN ExpectedEnd TEXT NULL");
        _schemaReady = true;
    }

    // Forget the check after a database switch (Parametres).
    public void Reset() => _schemaReady = false;

    private Task<bool> HasExpectedEndColumnAsync(IDbConnection connection)
        => connection.ExecuteScalarAsync<bool>(IsSqlServer
            ? "SELECT CASE WHEN COL_LENGTH('dbo.PlanStopQualifications', 'ExpectedEnd') IS NULL THEN 0 ELSE 1 END"
            : "SELECT COUNT(*) FROM pragma_table_info('PlanStopQualifications') WHERE name = 'ExpectedEnd'");

    private Task<bool> HasKindColumnAsync(IDbConnection connection)
        => connection.ExecuteScalarAsync<bool>(IsSqlServer
            ? "SELECT CASE WHEN COL_LENGTH('dbo.PlanMaintenanceEvents', 'Kind') IS NULL THEN 0 ELSE 1 END"
            : "SELECT COUNT(*) FROM pragma_table_info('PlanMaintenanceEvents') WHERE name = 'Kind'");

    private static readonly string[] SqlServerSchema =
    {
        @"IF OBJECT_ID('dbo.PlanStopQualifications') IS NULL
          CREATE TABLE dbo.PlanStopQualifications (
            Id INT IDENTITY(1,1) PRIMARY KEY,
            MachineId VARCHAR(10) NOT NULL,
            StopStart DATETIME2(0) NOT NULL,
            StopEnd DATETIME2(0) NOT NULL,
            ShiftDate DATE NOT NULL,
            ShiftId INT NOT NULL,
            Team NVARCHAR(50) NOT NULL,
            CauseCode VARCHAR(20) NOT NULL,
            Component NVARCHAR(100) NULL,
            Technician NVARCHAR(100) NULL,
            Comment NVARCHAR(1000) NULL,
            IsManual BIT NOT NULL,
            CreatedAt DATETIME2(0) NOT NULL)",
        @"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PlanStopQualifications_Start')
          CREATE INDEX IX_PlanStopQualifications_Start ON dbo.PlanStopQualifications (StopStart, MachineId)",
        @"IF OBJECT_ID('dbo.PlanMaintenancePlans') IS NULL
          CREATE TABLE dbo.PlanMaintenancePlans (
            Id INT IDENTITY(1,1) PRIMARY KEY,
            MachineId VARCHAR(10) NOT NULL,
            Task NVARCHAR(100) NOT NULL,
            Kind INT NOT NULL,
            IntervalDays INT NOT NULL,
            IntervalBags BIGINT NOT NULL,
            DurationMinutes INT NOT NULL,
            LastDoneAt DATETIME2(0) NULL,
            IsActive BIT NOT NULL)",
        @"IF OBJECT_ID('dbo.PlanMaintenanceEvents') IS NULL
          CREATE TABLE dbo.PlanMaintenanceEvents (
            Id INT IDENTITY(1,1) PRIMARY KEY,
            PlanId INT NULL,
            MachineId VARCHAR(10) NOT NULL,
            Task NVARCHAR(100) NOT NULL,
            PlannedStart DATETIME2(0) NOT NULL,
            DurationMinutes INT NOT NULL,
            Status INT NOT NULL,
            DoneAt DATETIME2(0) NULL,
            Technician NVARCHAR(100) NULL,
            Comment NVARCHAR(1000) NULL,
            Kind INT NOT NULL CONSTRAINT DF_PlanMaintenanceEvents_Kind DEFAULT 0)",
        @"IF OBJECT_ID('dbo.PlanProductionOrders') IS NULL
          CREATE TABLE dbo.PlanProductionOrders (
            Id INT IDENTITY(1,1) PRIMARY KEY,
            OrderNo NVARCHAR(30) NOT NULL,
            MachineId VARCHAR(10) NOT NULL,
            Product NVARCHAR(100) NOT NULL,
            TargetWeightGrams FLOAT NOT NULL,
            QuantityBags BIGINT NOT NULL,
            PlannedStart DATETIME2(0) NOT NULL,
            PlannedEnd DATETIME2(0) NOT NULL,
            Status INT NOT NULL,
            Comment NVARCHAR(1000) NULL)",
    };

    private static readonly string[] SqliteSchema =
    {
        @"CREATE TABLE IF NOT EXISTS PlanStopQualifications (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            MachineId TEXT NOT NULL, StopStart TEXT NOT NULL, StopEnd TEXT NOT NULL,
            ShiftDate TEXT NOT NULL, ShiftId INTEGER NOT NULL, Team TEXT NOT NULL,
            CauseCode TEXT NOT NULL, Component TEXT NULL, Technician TEXT NULL, Comment TEXT NULL,
            IsManual INTEGER NOT NULL, CreatedAt TEXT NOT NULL)",
        "CREATE INDEX IF NOT EXISTS IX_PlanStopQualifications_Start ON PlanStopQualifications (StopStart, MachineId)",
        @"CREATE TABLE IF NOT EXISTS PlanMaintenancePlans (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            MachineId TEXT NOT NULL, Task TEXT NOT NULL, Kind INTEGER NOT NULL,
            IntervalDays INTEGER NOT NULL, IntervalBags INTEGER NOT NULL, DurationMinutes INTEGER NOT NULL,
            LastDoneAt TEXT NULL, IsActive INTEGER NOT NULL)",
        @"CREATE TABLE IF NOT EXISTS PlanMaintenanceEvents (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            PlanId INTEGER NULL, MachineId TEXT NOT NULL, Task TEXT NOT NULL,
            PlannedStart TEXT NOT NULL, DurationMinutes INTEGER NOT NULL, Status INTEGER NOT NULL,
            DoneAt TEXT NULL, Technician TEXT NULL, Comment TEXT NULL, Kind INTEGER NOT NULL DEFAULT 0)",
        @"CREATE TABLE IF NOT EXISTS PlanProductionOrders (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            OrderNo TEXT NOT NULL, MachineId TEXT NOT NULL, Product TEXT NOT NULL,
            TargetWeightGrams REAL NOT NULL, QuantityBags INTEGER NOT NULL,
            PlannedStart TEXT NOT NULL, PlannedEnd TEXT NOT NULL, Status INTEGER NOT NULL, Comment TEXT NULL)",
    };

    private async Task<IDbConnection> OpenAsync()
    {
        await EnsureSchemaAsync();
        return _connectionFactory.CreateConnection();
    }

    // ---- Stop qualifications ----

    // Stops that started in [from, to).
    public async Task<List<StopQualification>> GetQualificationsAsync(DateTime from, DateTime to)
    {
        using var connection = await OpenAsync();
        var allowed = await AllowedAsync();
        return (await QueryQualificationsAsync(connection, from, to)).Where(q => allowed(q.MachineId)).ToList();
    }

    // Same, for the Arrets window: read only, never creates the tables (none
    // before the Planification window was first opened = no qualification).
    public async Task<List<StopQualification>> FindQualificationsAsync(DateTime from, DateTime to)
    {
        using var connection = _connectionFactory.CreateConnection();
        var exists = await connection.ExecuteScalarAsync<int>(IsSqlServer
            ? "SELECT CASE WHEN OBJECT_ID('dbo.PlanStopQualifications') IS NULL THEN 0 ELSE 1 END"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'PlanStopQualifications'");
        if (exists == 0)
            return new();
        var allowed = await AllowedAsync();
        return (await QueryQualificationsAsync(connection, from, to)).Where(q => allowed(q.MachineId)).ToList();
    }

    private async Task<List<StopQualification>> QueryQualificationsAsync(IDbConnection connection, DateTime from, DateTime to)
    {
        // Read only callers may run before the column exists (older table).
        var expectedEnd = await HasExpectedEndColumnAsync(connection) ? "ExpectedEnd" : "NULL AS ExpectedEnd";
        var rows = await connection.QueryAsync<StopQualification>(
            $@"SELECT Id, MachineId, StopStart, StopEnd, ShiftDate, ShiftId, Team, CauseCode, Component,
                     Technician, Comment, IsManual, CreatedAt, {expectedEnd}
              FROM PlanStopQualifications WHERE StopStart >= @from AND StopStart < @to",
            new { from, to });
        // Rows saved before v1.5.9 spelled the no-team value without the accent.
        foreach (var q in rows.Where(q => q.Team == "Sans equipe"))
            q.Team = TeamScheduleService.NoTeam;
        return rows.OrderBy(q => q.StopStart).ToList();
    }

    public async Task SaveQualificationAsync(StopQualification q)
    {
        await DemandAsync(q.MachineId);
        using var connection = await OpenAsync();
        await DemandStoredAsync(connection, "PlanStopQualifications", q.Id);
        if (q.Id == 0)
        {
            q.CreatedAt = DateTime.Now;
            q.Id = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO PlanStopQualifications (MachineId, StopStart, StopEnd, ShiftDate, ShiftId, Team, CauseCode,
                      Component, Technician, Comment, IsManual, CreatedAt, ExpectedEnd)
                  VALUES (@MachineId, @StopStart, @StopEnd, @ShiftDate, @ShiftId, @Team, @CauseCode,
                      @Component, @Technician, @Comment, @IsManual, @CreatedAt, @ExpectedEnd); " + IdentitySelect, q);
        }
        else
        {
            await connection.ExecuteAsync(
                @"UPDATE PlanStopQualifications SET StopStart = @StopStart, StopEnd = @StopEnd, CauseCode = @CauseCode,
                      Component = @Component, Technician = @Technician, Comment = @Comment, ExpectedEnd = @ExpectedEnd
                  WHERE Id = @Id", q);
        }
    }

    public async Task DeleteQualificationAsync(int id)
    {
        using var connection = await OpenAsync();
        await DemandStoredAsync(connection, "PlanStopQualifications", id);
        await connection.ExecuteAsync("DELETE FROM PlanStopQualifications WHERE Id = @id", new { id });
    }

    public async Task<List<string>> GetDistinctAsync(string column)
    {
        // Column names come from code only (Component / Technician).
        using var connection = await OpenAsync();
        var allowed = await AllowedAsync();
        var rows = await connection.QueryAsync<(string MachineId, string Value)>(
            $"SELECT DISTINCT MachineId, {column} AS Value FROM PlanStopQualifications WHERE {column} IS NOT NULL");
        return rows.Where(r => allowed(r.MachineId) && !string.IsNullOrWhiteSpace(r.Value)).Select(r => r.Value).Distinct().ToList();
    }

    // ---- Maintenance plans and events ----

    public async Task<List<MaintenancePlan>> GetPlansAsync()
    {
        using var connection = await OpenAsync();
        var allowed = await AllowedAsync();
        return (await connection.QueryAsync<MaintenancePlan>(
            @"SELECT Id, MachineId, Task, Kind, IntervalDays, IntervalBags, DurationMinutes, LastDoneAt, IsActive
              FROM PlanMaintenancePlans WHERE IsActive = 1")).Where(p => allowed(p.MachineId)).ToList();
    }

    public async Task SavePlanAsync(MaintenancePlan p)
    {
        await DemandAsync(p.MachineId);
        using var connection = await OpenAsync();
        await DemandStoredAsync(connection, "PlanMaintenancePlans", p.Id);
        if (p.Id == 0)
            p.Id = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO PlanMaintenancePlans (MachineId, Task, Kind, IntervalDays, IntervalBags, DurationMinutes, LastDoneAt, IsActive)
                  VALUES (@MachineId, @Task, @Kind, @IntervalDays, @IntervalBags, @DurationMinutes, @LastDoneAt, @IsActive); " + IdentitySelect, p);
        else
            await connection.ExecuteAsync(
                @"UPDATE PlanMaintenancePlans SET MachineId = @MachineId, Task = @Task, Kind = @Kind, IntervalDays = @IntervalDays,
                      IntervalBags = @IntervalBags, DurationMinutes = @DurationMinutes, LastDoneAt = @LastDoneAt, IsActive = @IsActive
                  WHERE Id = @Id", p);
    }

    // A maintenance can last several days: one started this long before a
    // range still counts in it (board, and the TRS required time).
    private const int EventLookbackDays = 60;

    public async Task<List<MaintenanceEvent>> GetEventsAsync(DateTime from, DateTime to)
    {
        using var connection = await OpenAsync();
        var rows = await connection.QueryAsync<MaintenanceEvent>(
            @"SELECT Id, PlanId, Kind, MachineId, Task, PlannedStart, DurationMinutes, Status, DoneAt, Technician, Comment
              FROM PlanMaintenanceEvents WHERE PlannedStart >= @from AND PlannedStart < @to AND Status <> 2",
            new { from = from.AddDays(-EventLookbackDays), to });
        var allowed = await AllowedAsync();
        return rows.Where(e => allowed(e.MachineId) && (e.PlannedStart >= from || e.ActualEnd > from)).ToList();
    }

    // Planned slots of a plan that aren't done yet (to avoid planning it twice).
    public async Task<List<MaintenanceEvent>> GetOpenEventsAsync()
    {
        using var connection = await OpenAsync();
        var allowed = await AllowedAsync();
        return (await connection.QueryAsync<MaintenanceEvent>(
            @"SELECT Id, PlanId, Kind, MachineId, Task, PlannedStart, DurationMinutes, Status, DoneAt, Technician, Comment
              FROM PlanMaintenanceEvents WHERE Status = 0")).Where(e => allowed(e.MachineId)).ToList();
    }

    public async Task SaveEventAsync(MaintenanceEvent e)
    {
        await DemandAsync(e.MachineId);
        using var connection = await OpenAsync();
        await DemandStoredAsync(connection, "PlanMaintenanceEvents", e.Id);
        if (e.Id == 0)
            e.Id = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO PlanMaintenanceEvents (PlanId, Kind, MachineId, Task, PlannedStart, DurationMinutes, Status, DoneAt, Technician, Comment)
                  VALUES (@PlanId, @Kind, @MachineId, @Task, @PlannedStart, @DurationMinutes, @Status, @DoneAt, @Technician, @Comment); " + IdentitySelect, e);
        else
            await connection.ExecuteAsync(
                @"UPDATE PlanMaintenanceEvents SET Kind = @Kind, MachineId = @MachineId, Task = @Task, PlannedStart = @PlannedStart,
                      DurationMinutes = @DurationMinutes, Status = @Status, DoneAt = @DoneAt, Technician = @Technician, Comment = @Comment
                  WHERE Id = @Id", e);
    }

    // Maintenance slots and planned stops running at `time` (not done, not
    // cancelled), for the slides. Read only, like the TRS read below: no
    // tables = nothing; no Kind column yet = maintenance only.
    public async Task<List<MaintenanceEvent>> FindActiveSlotsAsync(DateTime time)
    {
        using var connection = _connectionFactory.CreateConnection();
        var exists = await connection.ExecuteScalarAsync<int>(IsSqlServer
            ? "SELECT CASE WHEN OBJECT_ID('dbo.PlanMaintenanceEvents') IS NULL THEN 0 ELSE 1 END"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'PlanMaintenanceEvents'");
        if (exists == 0)
            return new();
        var kind = await HasKindColumnAsync(connection) ? "Kind" : "0 AS Kind";
        var rows = await connection.QueryAsync<MaintenanceEvent>(
            $@"SELECT Id, {kind}, MachineId, Task, PlannedStart, DurationMinutes, Status
               FROM PlanMaintenanceEvents WHERE PlannedStart >= @early AND PlannedStart <= @time AND Status = 0",
            new { early = time.AddDays(-EventLookbackDays), time });
        var allowed = await AllowedAsync();
        return rows.Where(e => allowed(e.MachineId) && e.IsActiveAt(time)).ToList();
    }

    // ---- Planned stops, for the TRS ----

    // Time each line was planned to stand still in [from, to): maintenance
    // slots and planned stops (planned or done, not cancelled) and stops qualified with a
    // "Planifié" cause (Maintenance préventive, Pas de production prévue).
    // Read by the KPI queries, so it never creates the tables: before the
    // Planification window was first opened there is simply nothing.
    public async Task<List<(string MachineId, DateTime Start, DateTime End)>> GetPlannedStopWindowsAsync(DateTime from, DateTime to)
    {
        using var connection = _connectionFactory.CreateConnection();
        var exists = await connection.ExecuteScalarAsync<int>(IsSqlServer
            ? "SELECT CASE WHEN OBJECT_ID('dbo.PlanMaintenanceEvents') IS NULL OR OBJECT_ID('dbo.PlanStopQualifications') IS NULL THEN 0 ELSE 1 END"
            : "SELECT CASE WHEN COUNT(*) = 2 THEN 1 ELSE 0 END FROM sqlite_master WHERE type = 'table' AND name IN ('PlanMaintenanceEvents', 'PlanStopQualifications')");
        if (exists == 0)
            return new();

        var planned = StopCauses.All.Where(c => c.Family == CauseFamily.Planifie).Select(c => c.Code).ToList();
        var events = await connection.QueryAsync<MaintenanceEvent>(
            @"SELECT MachineId, PlannedStart, DurationMinutes, Status, DoneAt FROM PlanMaintenanceEvents
              WHERE PlannedStart >= @early AND PlannedStart < @to AND Status <> 2",
            new { early = from.AddDays(-EventLookbackDays), to });
        // A planned stop still going on reaches its expected return: only the
        // minutes the line actually stood still are taken out of the TRS, so
        // a line back early counts normally. Its pieces in later postes start
        // up to the stop's length later: look back as far as a stop goes.
        var expectedEnd = await HasExpectedEndColumnAsync(connection) ? "ExpectedEnd" : "NULL AS ExpectedEnd";
        var stops = await connection.QueryAsync<StopQualification>(
            $@"SELECT MachineId, StopStart, StopEnd, {expectedEnd} FROM PlanStopQualifications
              WHERE StopStart >= @early AND StopStart < @to AND CauseCode IN @planned",
            new { early = from.AddDays(-15), to, planned });

        var allowed = await AllowedAsync();
        return events.Select(e => (e.MachineId, e.PlannedStart, e.ActualEnd))
            .Concat(stops.Select(q => (q.MachineId, q.StopStart, q.ExpectedEnd is { } e && e > q.StopEnd ? e : q.StopEnd)))
            .Where(w => allowed(w.MachineId) && w.Item3 > from && w.Item2 < to && w.Item3 > w.Item2)
            .ToList();
    }

    // ---- Production orders ----

    public async Task<List<ProductionOrder>> GetOrdersAsync(DateTime from, DateTime to)
    {
        using var connection = await OpenAsync();
        var allowed = await AllowedAsync();
        return (await connection.QueryAsync<ProductionOrder>(
            @"SELECT Id, OrderNo, MachineId, Product, TargetWeightGrams, QuantityBags, PlannedStart, PlannedEnd, Status, Comment
              FROM PlanProductionOrders WHERE PlannedStart < @to AND PlannedEnd > @from AND Status <> 2",
            new { from, to })).Where(o => allowed(o.MachineId)).OrderBy(o => o.PlannedStart).ToList();
    }

    // Every order of the plant, whatever the scope: the order numbers
    // (OF-yyyy-nnnn) follow it and must stay unique.
    public async Task<int> CountOrdersAsync()
    {
        using var connection = await OpenAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PlanProductionOrders");
    }

    public async Task SaveOrderAsync(ProductionOrder o)
    {
        await DemandAsync(o.MachineId);
        using var connection = await OpenAsync();
        await DemandStoredAsync(connection, "PlanProductionOrders", o.Id);
        if (o.Id == 0)
            o.Id = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO PlanProductionOrders (OrderNo, MachineId, Product, TargetWeightGrams, QuantityBags, PlannedStart, PlannedEnd, Status, Comment)
                  VALUES (@OrderNo, @MachineId, @Product, @TargetWeightGrams, @QuantityBags, @PlannedStart, @PlannedEnd, @Status, @Comment); " + IdentitySelect, o);
        else
            await connection.ExecuteAsync(
                @"UPDATE PlanProductionOrders SET OrderNo = @OrderNo, MachineId = @MachineId, Product = @Product,
                      TargetWeightGrams = @TargetWeightGrams, QuantityBags = @QuantityBags, PlannedStart = @PlannedStart,
                      PlannedEnd = @PlannedEnd, Status = @Status, Comment = @Comment
                  WHERE Id = @Id", o);
    }
}
