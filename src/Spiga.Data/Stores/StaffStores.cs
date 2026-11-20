using System.Data;
using Dapper;
using Spiga.Core.Security;
using Spiga.Core.Staff;

namespace Spiga.Data;

public sealed class StaffStore(AppDb db) : IStaffStore
{
    private sealed class LineRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Machines { get; set; } = "";
        public bool IsActive { get; set; }
    }

    private const string PostingColumns = "Id, EmployeeId, ValidFrom, Team, [Function], MachineId, LineId, CreatedBy, CreatedAt";

    public async Task<IReadOnlyList<Employee>> ListEmployeesAsync()
    {
        using var c = await db.OpenAsync();
        var employees = (await c.QueryAsync<Employee>("SELECT Id, Matricule, FullName, IsActive FROM StaffEmployees")).ToList();
        var postings = (await c.QueryAsync<EmployeePosting>($"SELECT {PostingColumns} FROM StaffPostings")).ToLookup(p => p.EmployeeId);
        foreach (var e in employees)
            e.Postings = postings[e.Id].OrderBy(p => p.ValidFrom).ThenBy(p => p.Id).ToList();
        return employees;
    }

    public async Task<Employee?> GetEmployeeAsync(int id)
    {
        using var c = await db.OpenAsync();
        var e = await c.QuerySingleOrDefaultAsync<Employee>("SELECT Id, Matricule, FullName, IsActive FROM StaffEmployees WHERE Id = @id", new { id });
        if (e != null)
            e.Postings = (await c.QueryAsync<EmployeePosting>($"SELECT {PostingColumns} FROM StaffPostings WHERE EmployeeId = @id ORDER BY ValidFrom, Id", new { id }))
                .ToList();
        return e;
    }

    public async Task<int> InsertEmployeeAsync(Employee employee, EmployeePosting posting)
    {
        using var c = await db.OpenAsync();
        using var t = c.BeginTransaction();
        var id = (int)await c.ExecuteScalarAsync<long>(
            "INSERT INTO StaffEmployees (Matricule, FullName, IsActive) VALUES (@Matricule, @FullName, @IsActive)" + db.ReturnId, employee, t);
        posting.EmployeeId = id;
        await InsertPosting(c, t, posting);
        t.Commit();
        return id;
    }

    public async Task UpdateEmployeeAsync(Employee employee)
    {
        using var c = await db.OpenAsync();
        await c.ExecuteAsync("UPDATE StaffEmployees SET Matricule = @Matricule, FullName = @FullName, IsActive = @IsActive WHERE Id = @Id", employee);
    }

    public async Task SavePostingAsync(EmployeePosting posting)
    {
        using var c = await db.OpenAsync();
        using var t = c.BeginTransaction();
        await c.ExecuteAsync("DELETE FROM StaffPostings WHERE EmployeeId = @EmployeeId AND ValidFrom = @ValidFrom",
            new { posting.EmployeeId, ValidFrom = posting.ValidFrom.Date }, t);
        await InsertPosting(c, t, posting);
        t.Commit();
    }

    public async Task DeletePostingAsync(long id)
    {
        using var c = await db.OpenAsync();
        await c.ExecuteAsync("DELETE FROM StaffPostings WHERE Id = @id", new { id });
    }

    public async Task<IReadOnlyList<ProductionLine>> ListLinesAsync()
    {
        using var c = await db.OpenAsync();
        return (await c.QueryAsync<LineRow>("SELECT Id, Name, Machines, IsActive FROM StaffLines"))
            .Select(r => new ProductionLine { Id = r.Id, Name = r.Name, IsActive = r.IsActive, Machines = DataScope.FromCsv(r.Machines).ToList() })
            .ToList();
    }

    public async Task<int> SaveLineAsync(ProductionLine line)
    {
        using var c = await db.OpenAsync();
        var row = new { line.Id, line.Name, Machines = DataScope.ToCsv(line.Machines), line.IsActive };
        if (line.Id == 0)
            return (int)await c.ExecuteScalarAsync<long>("INSERT INTO StaffLines (Name, Machines, IsActive) VALUES (@Name, @Machines, @IsActive)" + db.ReturnId, row);
        await c.ExecuteAsync("UPDATE StaffLines SET Name = @Name, Machines = @Machines, IsActive = @IsActive WHERE Id = @Id", row);
        return line.Id;
    }

    public async Task DeleteLineAsync(int id)
    {
        using var c = await db.OpenAsync();
        await c.ExecuteAsync("DELETE FROM StaffLines WHERE Id = @id", new { id });
    }

    private static Task InsertPosting(IDbConnection c, IDbTransaction t, EmployeePosting p)
        => c.ExecuteAsync(
            @"INSERT INTO StaffPostings (EmployeeId, ValidFrom, Team, [Function], MachineId, LineId, CreatedBy, CreatedAt)
              VALUES (@EmployeeId, @ValidFrom, @Team, @Function, @MachineId, @LineId, @CreatedBy, @CreatedAt)",
            new { p.EmployeeId, ValidFrom = p.ValidFrom.Date, p.Team, Function = p.Function.ToString(), p.MachineId, p.LineId, p.CreatedBy, p.CreatedAt }, t);
}

public sealed class AttendanceStore(AppDb db) : IAttendanceStore
{
    private const string SheetColumns =
        "Id, ProductionDate, ShiftId, Team, Status, Version, CreatedBy, CreatedAt, UpdatedBy, UpdatedAt, ValidatedBy, ValidatedAt";

    private const string LineColumns =
        "Id, SheetId, EmployeeId, FullName, [Function], HomeTeam, IsRenfort, Status, Absence, EntryAt, ExitAt, MachineId, LineId, " +
        "ReplacesEmployeeId, ActingFunction, ActingMachineId, ActingLineId, ActingFrom";

    public async Task<IReadOnlyList<AttendanceSheet>> ListAsync(DateTime fromDate, DateTime toDate)
    {
        using var c = await db.OpenAsync();
        var sheets = (await c.QueryAsync<AttendanceSheet>(
            $"SELECT {SheetColumns} FROM AttendanceSheets WHERE ProductionDate >= @from AND ProductionDate <= @to",
            new { from = fromDate.Date, to = toDate.Date })).ToList();
        if (sheets.Count == 0)
            return sheets;
        var lines = (await c.QueryAsync<AttendanceLine>(
            $@"SELECT {LineColumns} FROM AttendanceLines WHERE SheetId IN
               (SELECT Id FROM AttendanceSheets WHERE ProductionDate >= @from AND ProductionDate <= @to)",
            new { from = fromDate.Date, to = toDate.Date })).ToLookup(l => l.SheetId);
        foreach (var s in sheets)
            s.Lines = AttendanceService.Order(lines[s.Id]);
        return sheets;
    }

    public async Task<AttendanceSheet?> FindAsync(DateTime productionDate, int shiftId, string team)
    {
        using var c = await db.OpenAsync();
        var id = await c.ExecuteScalarAsync<long?>(
            "SELECT Id FROM AttendanceSheets WHERE ProductionDate = @date AND ShiftId = @shiftId AND Team = @team",
            new { date = productionDate.Date, shiftId, team });
        return id == null ? null : await GetAsync(id.Value);
    }

    public async Task<AttendanceSheet?> GetAsync(long id)
    {
        using var c = await db.OpenAsync();
        var sheet = await c.QuerySingleOrDefaultAsync<AttendanceSheet>($"SELECT {SheetColumns} FROM AttendanceSheets WHERE Id = @id", new { id });
        if (sheet != null)
            sheet.Lines = AttendanceService.Order(await c.QueryAsync<AttendanceLine>($"SELECT {LineColumns} FROM AttendanceLines WHERE SheetId = @id", new { id }));
        return sheet;
    }

    public async Task<long> InsertAsync(AttendanceSheet s, AttendanceRevision revision)
    {
        using var c = await db.OpenAsync();
        using var t = c.BeginTransaction();
        var id = await c.ExecuteScalarAsync<long>(
            @"INSERT INTO AttendanceSheets (ProductionDate, ShiftId, Team, Status, Version, CreatedBy, CreatedAt, UpdatedBy, UpdatedAt, ValidatedBy, ValidatedAt)
              VALUES (@ProductionDate, @ShiftId, @Team, @Status, @Version, @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt, @ValidatedBy, @ValidatedAt)" + db.ReturnId,
            SheetParams(s, 0), t);
        await InsertLines(c, t, id, s.Lines);
        revision.SheetId = id;
        await AddRevision(c, t, revision);
        t.Commit();
        return id;
    }

    public async Task<bool> UpdateAsync(AttendanceSheet s, int expectedVersion, AttendanceRevision revision)
    {
        using var c = await db.OpenAsync();
        using var t = c.BeginTransaction();
        var rows = await c.ExecuteAsync(
            @"UPDATE AttendanceSheets SET Status = @Status, Version = @Version, UpdatedBy = @UpdatedBy, UpdatedAt = @UpdatedAt,
                     ValidatedBy = @ValidatedBy, ValidatedAt = @ValidatedAt
              WHERE Id = @Id AND Version = @Expected",
            SheetParams(s, expectedVersion), t);
        if (rows == 0)
            return false;
        await c.ExecuteAsync("DELETE FROM AttendanceLines WHERE SheetId = @Id", new { s.Id }, t);
        await InsertLines(c, t, s.Id, s.Lines);
        await AddRevision(c, t, revision);
        t.Commit();
        return true;
    }

    public async Task<IReadOnlyList<AttendanceRevision>> ListRevisionsAsync(long sheetId)
    {
        using var c = await db.OpenAsync();
        return (await c.QueryAsync<AttendanceRevision>(
            "SELECT Id, SheetId, Version, At, UserLogin, Action, Changes, Note FROM AttendanceRevisions WHERE SheetId = @sheetId ORDER BY Version, Id",
            new { sheetId })).ToList();
    }

    private static object SheetParams(AttendanceSheet s, int expected) => new
    {
        s.Id, ProductionDate = s.ProductionDate.Date, s.ShiftId, s.Team, Status = s.Status.ToString(), s.Version,
        s.CreatedBy, s.CreatedAt, s.UpdatedBy, s.UpdatedAt, s.ValidatedBy, s.ValidatedAt, Expected = expected,
    };

    private static async Task InsertLines(IDbConnection c, IDbTransaction t, long sheetId, IEnumerable<AttendanceLine> lines)
    {
        foreach (var l in lines)
            await c.ExecuteAsync(
                @"INSERT INTO AttendanceLines (SheetId, EmployeeId, FullName, [Function], HomeTeam, IsRenfort, Status, Absence, EntryAt, ExitAt,
                      MachineId, LineId, ReplacesEmployeeId, ActingFunction, ActingMachineId, ActingLineId, ActingFrom)
                  VALUES (@SheetId, @EmployeeId, @FullName, @Function, @HomeTeam, @IsRenfort, @Status, @Absence, @EntryAt, @ExitAt,
                      @MachineId, @LineId, @ReplacesEmployeeId, @ActingFunction, @ActingMachineId, @ActingLineId, @ActingFrom)",
                new
                {
                    SheetId = sheetId, l.EmployeeId, l.FullName, Function = l.Function.ToString(), l.HomeTeam, l.IsRenfort,
                    Status = l.Status.ToString(), Absence = l.Absence?.ToString(), l.EntryAt, l.ExitAt, l.MachineId, l.LineId,
                    l.ReplacesEmployeeId, ActingFunction = l.ActingFunction?.ToString(), l.ActingMachineId, l.ActingLineId, l.ActingFrom,
                }, t);
    }

    private static Task AddRevision(IDbConnection c, IDbTransaction t, AttendanceRevision r)
        => c.ExecuteAsync(
            @"INSERT INTO AttendanceRevisions (SheetId, Version, At, UserLogin, Action, Changes, Note)
              VALUES (@SheetId, @Version, @At, @UserLogin, @Action, @Changes, @Note)", r, t);
}
