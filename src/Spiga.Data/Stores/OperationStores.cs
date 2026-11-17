using Dapper;
using Spiga.Core.Audit;
using Spiga.Core.Waste;

namespace Spiga.Data;

public sealed class AuditStore(AppDb db) : IAuditLog
{
    public async Task WriteAsync(AuditEntry e)
    {
        using var c = await db.OpenAsync();
        await c.ExecuteAsync(
            @"INSERT INTO AuditLog (At, UserLogin, Module, Action, Target, Details, OldValue, NewValue, Workstation)
              VALUES (@At, @UserLogin, @Module, @Action, @Target, @Details, @OldValue, @NewValue, @Workstation)", e);
    }

    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery q)
    {
        var where = new List<string> { "At >= @From", "At < @To" };
        if (!string.IsNullOrEmpty(q.UserLogin))
            where.Add("UserLogin = @UserLogin");
        if (!string.IsNullOrEmpty(q.Module))
            where.Add("Module = @Module");
        var sql = db.Provider == AppDbProvider.SqlServer
            ? $"SELECT TOP ({q.Limit}) * FROM AuditLog WHERE {string.Join(" AND ", where)} ORDER BY At DESC, Id DESC"
            : $"SELECT * FROM AuditLog WHERE {string.Join(" AND ", where)} ORDER BY At DESC, Id DESC LIMIT {q.Limit}";
        using var c = await db.OpenAsync();
        return (await c.QueryAsync<AuditEntry>(sql, q)).ToList();
    }
}

public sealed class WasteStore(AppDb db) : IWasteStore
{
    private const string Columns =
        "Id, Kind, ProductionDate, ShiftId, Team, Sector, MachineId, Quantity, Unit, Reason, Comment, Status, " +
        "CreatedBy, CreatedAt, UpdatedBy, UpdatedAt, ValidatedBy, ValidatedAt";

    public async Task<IReadOnlyList<WasteEntry>> ListAsync(WasteKind? kind, DateTime fromDate, DateTime toDate)
    {
        using var c = await db.OpenAsync();
        var sql = $"SELECT {Columns} FROM WasteEntries WHERE IsDeleted = 0 AND ProductionDate >= @from AND ProductionDate <= @to"
                  + (kind == null ? "" : " AND Kind = @kind");
        return (await c.QueryAsync<WasteEntry>(sql, new { from = fromDate.Date, to = toDate.Date, kind = kind?.ToString() })).ToList();
    }

    public async Task<WasteEntry?> GetAsync(long id)
    {
        using var c = await db.OpenAsync();
        return await c.QuerySingleOrDefaultAsync<WasteEntry>($"SELECT {Columns} FROM WasteEntries WHERE Id = @id AND IsDeleted = 0", new { id });
    }

    public async Task<long> InsertAsync(WasteEntry e)
    {
        using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<long>(
            @"INSERT INTO WasteEntries (Kind, ProductionDate, ShiftId, Team, Sector, MachineId, Quantity, Unit, Reason, Comment, Status,
                                        CreatedBy, CreatedAt, UpdatedBy, UpdatedAt, ValidatedBy, ValidatedAt, IsDeleted)
              VALUES (@Kind, @ProductionDate, @ShiftId, @Team, @Sector, @MachineId, @Quantity, @Unit, @Reason, @Comment, @Status,
                      @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt, @ValidatedBy, @ValidatedAt, 0)" + db.ReturnId,
            Params(e));
    }

    public async Task UpdateAsync(WasteEntry e)
    {
        using var c = await db.OpenAsync();
        await c.ExecuteAsync(
            @"UPDATE WasteEntries SET ProductionDate = @ProductionDate, ShiftId = @ShiftId, Team = @Team, Sector = @Sector,
                     MachineId = @MachineId, Quantity = @Quantity, Unit = @Unit, Reason = @Reason, Comment = @Comment, Status = @Status,
                     UpdatedBy = @UpdatedBy, UpdatedAt = @UpdatedAt, ValidatedBy = @ValidatedBy, ValidatedAt = @ValidatedAt
              WHERE Id = @Id AND IsDeleted = 0",
            Params(e));
    }

    public async Task DeleteAsync(long id, string deletedBy, DateTime at)
    {
        using var c = await db.OpenAsync();
        await c.ExecuteAsync("UPDATE WasteEntries SET IsDeleted = 1, DeletedBy = @deletedBy, DeletedAt = @at WHERE Id = @id",
            new { id, deletedBy, at });
    }

    public async Task<IReadOnlyList<WasteReason>> ListReasonsAsync(WasteKind kind)
    {
        using var c = await db.OpenAsync();
        return (await c.QueryAsync<WasteReason>(
            "SELECT Id, Kind, Label, IsActive, SortOrder FROM WasteReasons WHERE Kind = @kind AND IsActive = 1 ORDER BY SortOrder, Label",
            new { kind = kind.ToString() })).ToList();
    }

    // Enums go in as text (Dapper would send their number).
    private static object Params(WasteEntry e) => new
    {
        e.Id, Kind = e.Kind.ToString(), ProductionDate = e.ProductionDate.Date, e.ShiftId, e.Team, e.Sector, e.MachineId,
        e.Quantity, e.Unit, e.Reason, e.Comment, Status = e.Status.ToString(),
        e.CreatedBy, e.CreatedAt, e.UpdatedBy, e.UpdatedAt, e.ValidatedBy, e.ValidatedAt,
    };
}
