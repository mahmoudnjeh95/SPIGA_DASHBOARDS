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
        "CreatedBy, CreatedAt, UpdatedBy, UpdatedAt, ValidatedBy, ValidatedAt, Version, " +
        "ReturnedBy, ReturnedAt, ReturnComment, CancelledBy, CancelledAt, CancelReason";

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

    public async Task<long> InsertAsync(WasteEntry e, WasteRevision revision)
    {
        using var c = await db.OpenAsync();
        using var t = c.BeginTransaction();
        var id = await c.ExecuteScalarAsync<long>(
            @"INSERT INTO WasteEntries (Kind, ProductionDate, ShiftId, Team, Sector, MachineId, Quantity, Unit, Reason, Comment, Status,
                                        CreatedBy, CreatedAt, UpdatedBy, UpdatedAt, ValidatedBy, ValidatedAt, Version,
                                        ReturnedBy, ReturnedAt, ReturnComment, CancelledBy, CancelledAt, CancelReason, IsDeleted)
              VALUES (@Kind, @ProductionDate, @ShiftId, @Team, @Sector, @MachineId, @Quantity, @Unit, @Reason, @Comment, @Status,
                      @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt, @ValidatedBy, @ValidatedAt, @Version,
                      @ReturnedBy, @ReturnedAt, @ReturnComment, @CancelledBy, @CancelledAt, @CancelReason, 0)" + db.ReturnId,
            Params(e, 0), t);
        revision.EntryId = id;
        await AddRevision(c, t, revision);
        t.Commit();
        return id;
    }

    public async Task<bool> UpdateAsync(WasteEntry e, int expectedVersion, WasteRevision revision)
    {
        using var c = await db.OpenAsync();
        using var t = c.BeginTransaction();
        var rows = await c.ExecuteAsync(
            @"UPDATE WasteEntries SET ProductionDate = @ProductionDate, ShiftId = @ShiftId, Team = @Team, Sector = @Sector,
                     MachineId = @MachineId, Quantity = @Quantity, Unit = @Unit, Reason = @Reason, Comment = @Comment, Status = @Status,
                     UpdatedBy = @UpdatedBy, UpdatedAt = @UpdatedAt, ValidatedBy = @ValidatedBy, ValidatedAt = @ValidatedAt,
                     Version = @Version, ReturnedBy = @ReturnedBy, ReturnedAt = @ReturnedAt, ReturnComment = @ReturnComment,
                     CancelledBy = @CancelledBy, CancelledAt = @CancelledAt, CancelReason = @CancelReason
              WHERE Id = @Id AND IsDeleted = 0 AND Version = @Expected",
            Params(e, expectedVersion), t);
        if (rows == 0)
            return false;
        await AddRevision(c, t, revision);
        t.Commit();
        return true;
    }

    public async Task<bool> DeleteAsync(long id, int expectedVersion, string deletedBy, DateTime at, WasteRevision revision)
    {
        using var c = await db.OpenAsync();
        using var t = c.BeginTransaction();
        var rows = await c.ExecuteAsync(
            "UPDATE WasteEntries SET IsDeleted = 1, DeletedBy = @deletedBy, DeletedAt = @at, Version = Version + 1 " +
            "WHERE Id = @id AND IsDeleted = 0 AND Version = @expectedVersion",
            new { id, deletedBy, at, expectedVersion }, t);
        if (rows == 0)
            return false;
        await AddRevision(c, t, revision);
        t.Commit();
        return true;
    }

    public async Task<IReadOnlyList<WasteRevision>> ListRevisionsAsync(long entryId)
    {
        using var c = await db.OpenAsync();
        return (await c.QueryAsync<WasteRevision>(
            "SELECT Id, EntryId, Version, At, UserLogin, Action, Status, Snapshot, Note FROM WasteRevisions WHERE EntryId = @entryId ORDER BY Version, Id",
            new { entryId })).ToList();
    }

    public async Task<IReadOnlyList<WasteReason>> ListReasonsAsync(WasteKind kind)
    {
        using var c = await db.OpenAsync();
        return (await c.QueryAsync<WasteReason>(
            "SELECT Id, Kind, Label, IsActive, SortOrder FROM WasteReasons WHERE Kind = @kind AND IsActive = 1 ORDER BY SortOrder, Label",
            new { kind = kind.ToString() })).ToList();
    }

    private static Task AddRevision(System.Data.IDbConnection c, System.Data.IDbTransaction t, WasteRevision r)
        => c.ExecuteAsync(
            @"INSERT INTO WasteRevisions (EntryId, Version, At, UserLogin, Action, Status, Snapshot, Note)
              VALUES (@EntryId, @Version, @At, @UserLogin, @Action, @Status, @Snapshot, @Note)",
            new { r.EntryId, r.Version, r.At, r.UserLogin, r.Action, Status = r.Status.ToString(), r.Snapshot, r.Note }, t);

    // Enums go in as text (Dapper would send their number).
    private static object Params(WasteEntry e, int expected) => new
    {
        e.Id, Kind = e.Kind.ToString(), ProductionDate = e.ProductionDate.Date, e.ShiftId, e.Team, e.Sector, e.MachineId,
        e.Quantity, e.Unit, e.Reason, e.Comment, Status = e.Status.ToString(),
        e.CreatedBy, e.CreatedAt, e.UpdatedBy, e.UpdatedAt, e.ValidatedBy, e.ValidatedAt, e.Version,
        e.ReturnedBy, e.ReturnedAt, e.ReturnComment, e.CancelledBy, e.CancelledAt, e.CancelReason,
        Expected = expected,
    };
}
