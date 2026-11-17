using System.Data;
using System.Text.Json;
using Dapper;
using Spiga.Core.Security;

namespace Spiga.Data;

// Row classes with settable properties, not positional records: Dapper then
// converts the column types, which differ between SQLite and SQL Server.

public sealed class RoleStore(AppDb db) : IRoleStore
{
    private sealed class RoleRow
    {
        public long Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsSystem { get; set; }
        public bool IsActive { get; set; }
        public long UserCount { get; set; }
    }

    private sealed class PairRow
    {
        public long RoleId { get; set; }
        public string Value { get; set; } = "";
    }

    public async Task<IReadOnlyList<Role>> ListAsync()
    {
        using var c = await db.OpenAsync();
        var rows = await c.QueryAsync<RoleRow>(
            @"SELECT r.Id, r.Code, r.Name, r.Description, r.IsSystem, r.IsActive,
                     (SELECT COUNT(DISTINCT ur.UserId) FROM UserRoles ur WHERE ur.RoleId = r.Id) AS UserCount
              FROM Roles r ORDER BY r.IsSystem DESC, r.Name");
        var grants = (await c.QueryAsync<PairRow>("SELECT RoleId, Permission AS Value FROM RoleGrants")).ToLookup(g => g.RoleId);
        var assignable = (await c.QueryAsync<PairRow>("SELECT RoleId, CAST(AssignableRoleId AS VARCHAR(20)) AS Value FROM RoleAssignables"))
            .ToLookup(a => a.RoleId);
        return rows.Select(r => new Role
        {
            Id = (int)r.Id,
            Code = r.Code,
            Name = r.Name,
            Description = r.Description,
            IsSystem = r.IsSystem,
            IsActive = r.IsActive,
            UserCount = (int)r.UserCount,
            Permissions = new PermissionSet(grants[r.Id].Select(g => g.Value)),
            AssignableRoleIds = assignable[r.Id].Select(a => int.Parse(a.Value)).ToList(),
        }).ToList();
    }

    public async Task<Role?> GetAsync(int id) => (await ListAsync()).FirstOrDefault(r => r.Id == id);

    public async Task<Role?> GetByCodeAsync(string code) => (await ListAsync()).FirstOrDefault(r => r.Code == code);

    public async Task<int> InsertAsync(Role role)
    {
        using var c = await db.OpenAsync();
        using var t = c.BeginTransaction();
        var now = DateTime.Now;
        var id = (int)await c.ExecuteScalarAsync<long>(
            "INSERT INTO Roles (Code, Name, Description, IsSystem, IsActive, CreatedAt, UpdatedAt) VALUES (@Code, @Name, @Description, @IsSystem, @IsActive, @now, @now)" + db.ReturnId,
            new { role.Code, role.Name, role.Description, role.IsSystem, role.IsActive, now }, t);
        await WriteChildren(c, t, id, role);
        t.Commit();
        return id;
    }

    public async Task UpdateAsync(Role role)
    {
        using var c = await db.OpenAsync();
        using var t = c.BeginTransaction();
        await c.ExecuteAsync("UPDATE Roles SET Name = @Name, Description = @Description, IsActive = @IsActive, UpdatedAt = @now WHERE Id = @Id",
            new { role.Name, role.Description, role.IsActive, role.Id, now = DateTime.Now }, t);
        await c.ExecuteAsync("DELETE FROM RoleGrants WHERE RoleId = @Id", new { role.Id }, t);
        await c.ExecuteAsync("DELETE FROM RoleAssignables WHERE RoleId = @Id", new { role.Id }, t);
        await WriteChildren(c, t, role.Id, role);
        t.Commit();
    }

    public async Task DeleteAsync(int id)
    {
        using var c = await db.OpenAsync();
        using var t = c.BeginTransaction();
        await c.ExecuteAsync("DELETE FROM RoleGrants WHERE RoleId = @id", new { id }, t);
        await c.ExecuteAsync("DELETE FROM RoleAssignables WHERE RoleId = @id OR AssignableRoleId = @id", new { id }, t);
        await c.ExecuteAsync("DELETE FROM Roles WHERE Id = @id AND IsSystem = 0", new { id }, t);
        t.Commit();
    }

    private static async Task WriteChildren(IDbConnection c, IDbTransaction t, int roleId, Role role)
    {
        // The system role's rights are implicit (all); nothing stored for it.
        if (!role.IsSystem)
            await c.ExecuteAsync("INSERT INTO RoleGrants (RoleId, Permission) VALUES (@roleId, @permission)",
                role.Permissions.Keys.Select(permission => new { roleId, permission }), t);
        await c.ExecuteAsync("INSERT INTO RoleAssignables (RoleId, AssignableRoleId) VALUES (@roleId, @assignable)",
            role.AssignableRoleIds.Distinct().Where(a => a != 0).Select(assignable => new { roleId, assignable }), t);
    }
}

public sealed class UserStore(AppDb db) : IUserStore
{
    private sealed class UserRow
    {
        public long Id { get; set; }
        public string Login { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public bool MustChangePassword { get; set; }
        public bool IsActive { get; set; }
        public long FailedAttempts { get; set; }
        public DateTime? LockedUntil { get; set; }
        public string Theme { get; set; } = "light";
        public DateTime? LastLoginAt { get; set; }
        public DateTime? PasswordChangedAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class AssignmentRow
    {
        public long Id { get; set; }
        public long UserId { get; set; }
        public long RoleId { get; set; }
        public string RoleName { get; set; } = "";
        public string ScopeSectors { get; set; } = "";
        public string ScopeTeam { get; set; } = "";
        public string ScopeMachines { get; set; } = "";
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
    }

    private const string Select =
        @"SELECT Id, Login, DisplayName, PasswordHash, MustChangePassword, IsActive, FailedAttempts, LockedUntil, Theme,
                 LastLoginAt, PasswordChangedAt, CreatedAt
          FROM Users";

    private const string SelectAssignments =
        @"SELECT ur.Id, ur.UserId, ur.RoleId, r.Name AS RoleName, ur.ScopeSectors, ur.ScopeTeam, ur.ScopeMachines, ur.ValidFrom, ur.ValidTo
          FROM UserRoles ur JOIN Roles r ON r.Id = ur.RoleId";

    public async Task<int> CountAsync()
    {
        using var c = await db.OpenAsync();
        return (int)await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Users");
    }

    public async Task<IReadOnlyList<AppUser>> ListAsync()
    {
        using var c = await db.OpenAsync();
        var rows = await c.QueryAsync<UserRow>(Select + " ORDER BY Login");
        var assignments = (await c.QueryAsync<AssignmentRow>(SelectAssignments + " ORDER BY ur.Id")).ToLookup(a => a.UserId);
        return rows.Select(r => Map(r, assignments[r.Id])).ToList();
    }

    public Task<AppUser?> GetAsync(int id) => GetOneAsync(" WHERE Id = @id", new { id });

    public Task<AppUser?> GetByLoginAsync(string login) => GetOneAsync(" WHERE Login = @login", new { login });

    private async Task<AppUser?> GetOneAsync(string where, object args)
    {
        using var c = await db.OpenAsync();
        var row = await c.QuerySingleOrDefaultAsync<UserRow>(Select + where, args);
        if (row == null)
            return null;
        var assignments = await c.QueryAsync<AssignmentRow>(SelectAssignments + " WHERE ur.UserId = @id ORDER BY ur.Id", new { id = row.Id });
        return Map(row, assignments);
    }

    public async Task<int> InsertAsync(AppUser user)
    {
        using var c = await db.OpenAsync();
        using var t = c.BeginTransaction();
        var id = (int)await c.ExecuteScalarAsync<long>(
            @"INSERT INTO Users (Login, DisplayName, PasswordHash, MustChangePassword, IsActive, FailedAttempts, LockedUntil,
                                 Theme, LastLoginAt, PasswordChangedAt, CreatedAt, UpdatedAt)
              VALUES (@Login, @DisplayName, @PasswordHash, @MustChangePassword, @IsActive, @FailedAttempts, @LockedUntil,
                      @Theme, @LastLoginAt, @PasswordChangedAt, @CreatedAt, @UpdatedAt)" + db.ReturnId,
            Params(user), t);
        await WriteAssignments(c, t, id, user.Assignments);
        t.Commit();
        return id;
    }

    public async Task UpdateAsync(AppUser user)
    {
        using var c = await db.OpenAsync();
        await c.ExecuteAsync(
            @"UPDATE Users SET DisplayName = @DisplayName, PasswordHash = @PasswordHash, MustChangePassword = @MustChangePassword,
                     IsActive = @IsActive, FailedAttempts = @FailedAttempts, LockedUntil = @LockedUntil, Theme = @Theme,
                     LastLoginAt = @LastLoginAt, PasswordChangedAt = @PasswordChangedAt, UpdatedAt = @UpdatedAt
              WHERE Id = @Id",
            Params(user));
    }

    public async Task SetAssignmentsAsync(int userId, IReadOnlyList<RoleAssignment> assignments)
    {
        using var c = await db.OpenAsync();
        using var t = c.BeginTransaction();
        await c.ExecuteAsync("DELETE FROM UserRoles WHERE UserId = @userId", new { userId }, t);
        await WriteAssignments(c, t, userId, assignments);
        t.Commit();
    }

    private static Task WriteAssignments(IDbConnection c, IDbTransaction t, int userId, IEnumerable<RoleAssignment> assignments)
        => c.ExecuteAsync(
            @"INSERT INTO UserRoles (UserId, RoleId, ScopeSectors, ScopeTeam, ScopeMachines, ValidFrom, ValidTo)
              VALUES (@userId, @RoleId, @ScopeSectors, @ScopeTeam, @ScopeMachines, @ValidFrom, @ValidTo)",
            assignments.Select(a => new
            {
                userId, a.RoleId,
                ScopeSectors = DataScope.ToCsv(a.Scope.Sectors),
                ScopeTeam = a.Scope.Team ?? "",
                ScopeMachines = DataScope.ToCsv(a.Scope.Machines),
                ValidFrom = a.ValidFrom?.Date,
                ValidTo = a.ValidTo?.Date,
            }), t);

    private static object Params(AppUser u) => new
    {
        u.Id, u.Login, u.DisplayName, u.PasswordHash, u.MustChangePassword, u.IsActive, u.FailedAttempts, u.LockedUntil,
        u.Theme, u.LastLoginAt, u.PasswordChangedAt,
        CreatedAt = u.CreatedAt == default ? DateTime.Now : u.CreatedAt,
        UpdatedAt = DateTime.Now,
    };

    private static AppUser Map(UserRow r, IEnumerable<AssignmentRow> assignments) => new()
    {
        Id = (int)r.Id,
        Login = r.Login,
        DisplayName = r.DisplayName,
        PasswordHash = r.PasswordHash,
        MustChangePassword = r.MustChangePassword,
        IsActive = r.IsActive,
        FailedAttempts = (int)r.FailedAttempts,
        LockedUntil = r.LockedUntil,
        Theme = r.Theme,
        LastLoginAt = r.LastLoginAt,
        PasswordChangedAt = r.PasswordChangedAt,
        CreatedAt = r.CreatedAt,
        Assignments = assignments.Select(a => new RoleAssignment
        {
            Id = a.Id,
            RoleId = (int)a.RoleId,
            RoleName = a.RoleName,
            Scope = new DataScope(DataScope.FromCsv(a.ScopeSectors), string.IsNullOrWhiteSpace(a.ScopeTeam) ? null : a.ScopeTeam,
                DataScope.FromCsv(a.ScopeMachines)),
            ValidFrom = a.ValidFrom,
            ValidTo = a.ValidTo,
        }).ToList(),
    };
}

public sealed class SettingsStore(AppDb db) : ISettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        using var c = await db.OpenAsync();
        var value = await c.QuerySingleOrDefaultAsync<string>($"SELECT Value FROM AppSettings WHERE {db.Key} = @key", new { key });
        return value == null ? null : JsonSerializer.Deserialize<T>(value, Json);
    }

    public async Task SetAsync<T>(string key, T value, string updatedBy) where T : class
    {
        using var c = await db.OpenAsync();
        using var t = c.BeginTransaction();
        var json = JsonSerializer.Serialize(value, Json);
        var args = new { key, json, now = DateTime.Now, updatedBy };
        var changed = await c.ExecuteAsync($"UPDATE AppSettings SET Value = @json, UpdatedAt = @now, UpdatedBy = @updatedBy WHERE {db.Key} = @key", args, t);
        if (changed == 0)
            await c.ExecuteAsync($"INSERT INTO AppSettings ({db.Key}, Value, UpdatedAt, UpdatedBy) VALUES (@key, @json, @now, @updatedBy)", args, t);
        t.Commit();
    }
}
