using System.Data;
using Dapper;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.Data;

// The Planification window's own tables (Plan*), in the application database
// (SPIGA Suivi Production, spiga_suivi) so every PC sees the same pannes and
// planning. Created by its migrations (Spiga.Data, version 4). The production
// database "stats" is only read, for the machines' sectors.
public class PlanningRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IDbConnectionFactory _live;

    // appData: the application database. live: the production database
    // ("stats", read only: the machines' sectors for the scope).
    // access: the machines the signed-in account may see and plan (SPIGA
    // Suivi Production). Not given = every machine (TRS reads, kiosk).
    public PlanningRepository(IDbConnectionFactory appData, IDbConnectionFactory live, MachineAccess? access = null)
    {
        _connectionFactory = appData;
        _live = live;
        Access = access ?? MachineAccess.Everything();
    }

    // Every read below leaves out the machines outside it; every write on one
    // of them (or on a row that belongs to one) is refused.
    public MachineAccess Access { get; }

    private Task<Func<string, bool>> AllowedAsync() => Access.FilterAsync(_live);

    private Task DemandAsync(string machineId) => Access.DemandAsync(_live, machineId);

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

    // The tables come from the application database's migrations, run at
    // start-up: nothing to create here any more.
    public Task EnsureSchemaAsync() => Task.CompletedTask;

    private Task<IDbConnection> OpenAsync() => Task.FromResult(_connectionFactory.CreateConnection());

    // ---- Stop qualifications ----

    // Stops that started in [from, to).
    public async Task<List<StopQualification>> GetQualificationsAsync(DateTime from, DateTime to)
    {
        using var connection = await OpenAsync();
        var allowed = await AllowedAsync();
        return (await QueryQualificationsAsync(connection, from, to)).Where(q => allowed(q.MachineId)).ToList();
    }

    // Same, for the Arrets window (kept for its callers).
    public async Task<List<StopQualification>> FindQualificationsAsync(DateTime from, DateTime to)
    {
        using var connection = _connectionFactory.CreateConnection();
        var allowed = await AllowedAsync();
        return (await QueryQualificationsAsync(connection, from, to)).Where(q => allowed(q.MachineId)).ToList();
    }

    private async Task<List<StopQualification>> QueryQualificationsAsync(IDbConnection connection, DateTime from, DateTime to)
    {
        var rows = await connection.QueryAsync<StopQualification>(
            @"SELECT Id, MachineId, StopStart, StopEnd, ShiftDate, ShiftId, Team, CauseCode, Component,
                     Technician, Comment, IsManual, CreatedAt, ExpectedEnd
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
    // cancelled), for the slides.
    public async Task<List<MaintenanceEvent>> FindActiveSlotsAsync(DateTime time)
    {
        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<MaintenanceEvent>(
            @"SELECT Id, Kind, MachineId, Task, PlannedStart, DurationMinutes, Status
               FROM PlanMaintenanceEvents WHERE PlannedStart >= @early AND PlannedStart <= @time AND Status = 0",
            new { early = time.AddDays(-EventLookbackDays), time });
        var allowed = await AllowedAsync();
        return rows.Where(e => allowed(e.MachineId) && e.IsActiveAt(time)).ToList();
    }

    // ---- Planned stops, for the TRS ----

    // Time each line was planned to stand still in [from, to): maintenance
    // slots and planned stops (planned or done, not cancelled) and stops qualified with a
    // "Planifié" cause (Maintenance préventive, Pas de production prévue).
    // Read by the KPI queries (which carry on without it if it fails).
    public async Task<List<(string MachineId, DateTime Start, DateTime End)>> GetPlannedStopWindowsAsync(DateTime from, DateTime to)
    {
        using var connection = _connectionFactory.CreateConnection();

        var planned = StopCauses.All.Where(c => c.Family == CauseFamily.Planifie).Select(c => c.Code).ToList();
        var events = await connection.QueryAsync<MaintenanceEvent>(
            @"SELECT MachineId, PlannedStart, DurationMinutes, Status, DoneAt FROM PlanMaintenanceEvents
              WHERE PlannedStart >= @early AND PlannedStart < @to AND Status <> 2",
            new { early = from.AddDays(-EventLookbackDays), to });
        // A planned stop still going on reaches its expected return: only the
        // minutes the line actually stood still are taken out of the TRS, so
        // a line back early counts normally. Its pieces in later postes start
        // up to the stop's length later: look back as far as a stop goes.
        var stops = await connection.QueryAsync<StopQualification>(
            @"SELECT MachineId, StopStart, StopEnd, ExpectedEnd FROM PlanStopQualifications
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

    // The next free number of the year, OF-yyyy-nnnn: one more than the
    // highest given (every order of the plant, whatever the scope). Only a
    // proposal: SaveOrderAsync takes the next one again if another PC used
    // it meanwhile (the number is unique in the database).
    public async Task<string> NextOrderNoAsync(DateTime? at = null)
    {
        using var connection = await OpenAsync();
        return await NextOrderNoAsync(connection, null, at ?? DateTime.Now);
    }

    private static async Task<string> NextOrderNoAsync(IDbConnection connection, IDbTransaction? transaction, DateTime at)
    {
        var prefix = $"OF-{at:yyyy}-";
        var numbers = await connection.QueryAsync<string>(
            "SELECT OrderNo FROM PlanProductionOrders WHERE OrderNo LIKE @pattern", new { pattern = prefix + "%" }, transaction);
        var highest = numbers
            .Select(n => n[prefix.Length..].Split('-')[0])
            .Select(n => int.TryParse(n, out var value) ? value : 0)
            .DefaultIfEmpty(0)
            .Max();
        return $"{prefix}{highest + 1:0000}";
    }

    private static readonly System.Text.RegularExpressions.Regex AutoNumber = new(@"^OF-\d{4}-\d{4,}$");

    // A new order with a proposed number (OF-yyyy-nnnn) taken meanwhile by
    // another PC gets the next free one (o.OrderNo tells which). A number
    // typed by hand that is already used is refused.
    public async Task SaveOrderAsync(ProductionOrder o)
    {
        await DemandAsync(o.MachineId);
        using var connection = await OpenAsync();
        await DemandStoredAsync(connection, "PlanProductionOrders", o.Id);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
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
                return;
            }
            catch (Exception ex) when (IsDuplicate(ex))
            {
                if (o.Id != 0 || !AutoNumber.IsMatch(o.OrderNo) || attempt >= 5)
                    throw new InvalidOperationException($"Le numéro d'ordre {o.OrderNo} est déjà utilisé : choisissez-en un autre.", ex);
                o.OrderNo = await NextOrderNoAsync(connection, null, DateTime.Now);
            }
        }
    }

    // A unique key refused the row (SQL Server 2627 / 2601, SQLite 19).
    private static bool IsDuplicate(Exception ex) => ex switch
    {
        Microsoft.Data.SqlClient.SqlException sql => sql.Number is 2627 or 2601,
        Microsoft.Data.Sqlite.SqliteException lite => lite.SqliteErrorCode == 19 && lite.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
}
