using System.Text.Json;
using Dapper;
using Spiga.Core.Security;

namespace Spiga.Data;

public sealed class RoleStore(AppDb db) : IRoleStore
{
    // Settable classes, not positional records: Dapper then converts the
    // column types, which differ between SQLite and SQL Server.
    private sealed class RoleRow
    {
        public long Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsSystem { get; set; }
        public long UserCount { get; set; }
    }

    private sealed class GrantRow
    {
        public long RoleId { get; set; }
        public string Module { get; set; } = "";
        public long Actions { get; set; }
    }

    public async Task<IReadOnlyList<Role>> ListAsync()
    {
        using var c = await db.OpenAsync();
        var rows = await c.QueryAsync<RoleRow>(
            @"SELECT r.Id, r.Code, r.Name, r.Description, r.IsSystem,
                     (SELECT COUNT(*) FROM Users u WHERE u.RoleId = r.Id) AS UserCount
              FROM Roles r ORDER BY r.IsSystem DESC, r.Id");
        var grants = (await c.QueryAsync<GrantRow>("SELECT RoleId, Module, Actions FROM RolePermissions"))
            .ToLookup(g => g.RoleId);
        return rows.Select(r => Map(r, grants[r.Id])).ToList();
    }

    public async Task<Role?> GetAsync(int id) => (await ListAsync()).FirstOrDefault(r => r.Id == id);

    public async Task<Role?> GetByCodeAsync(string code) => (await ListAsync()).FirstOrDefault(r => r.Code == code);

    public async Task<int> InsertAsync(Role role)
    {
        using var c = await db.OpenAsync();
        using var t = c.BeginTransaction();
        var now = DateTime.Now;
        var id = await c.ExecuteScalarAsync<long>(
            "INSERT INTO Roles (Code, Name, Description, IsSystem, CreatedAt, UpdatedAt) VALUES (@Code, @Name, @Description, @IsSystem, @now, @now)" + db.ReturnId,
            new { role.Code, role.Name, role.Description, role.IsSystem, now }, t);
        await WriteGrants(c, t, id, role.Permissions);
        t.Commit();
        return (int)id;
    }

    public async Task UpdateAsync(Role role)
    {
        using var c = await db.OpenAsync();
        using var t = c.BeginTransaction();
        await c.ExecuteAsync("UPDATE Roles SET Name = @Name, Description = @Description, UpdatedAt = @now WHERE Id = @Id",
            new { role.Name, role.Description, role.Id, now = DateTime.Now }, t);
        await c.ExecuteAsync("DELETE FROM RolePermissions WHERE RoleId = @Id", new { role.Id }, t);
        await WriteGrants(c, t, role.Id, role.Permissions);
        t.Commit();
    }

    public async Task DeleteAsync(int id)
    {
        using var c = await db.OpenAsync();
        using var t = c.BeginTransaction();
        await c.ExecuteAsync("DELETE FROM RolePermissions WHERE RoleId = @id", new { id }, t);
        await c.ExecuteAsync("DELETE FROM Roles WHERE Id = @id AND IsSystem = 0", new { id }, t);
        t.Commit();
    }

    private static Task WriteGrants(System.Data.IDbConnection c, System.Data.IDbTransaction t, long roleId, PermissionSet permissions)
        => c.ExecuteAsync("INSERT INTO RolePermissions (RoleId, Module, Actions) VALUES (@roleId, @Module, @Actions)",
            permissions.Grants.Select(g => new { roleId, Module = g.Key, Actions = (int)g.Value }), t);

    private static Role Map(RoleRow r, IEnumerable<GrantRow> grants) => new()
    {
        Id = (int)r.Id,
        Code = r.Code,
        Name = r.Name,
        Description = r.Description,
        IsSystem = r.IsSystem,
        UserCount = (int)r.UserCount,
        Permissions = new PermissionSet(grants.Select(g => new KeyValuePair<string, AppAction>(g.Module, (AppAction)g.Actions))),
    };
}

public sealed class UserStore(AppDb db) : IUserStore
{
    private sealed class UserRow
    {
        public long Id { get; set; }
        public string Login { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public long RoleId { get; set; }
        public string RoleName { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public bool MustChangePassword { get; set; }
        public bool IsActive { get; set; }
        public long FailedAttempts { get; set; }
        public DateTime? LockedUntil { get; set; }
        public string ScopeSectors { get; set; } = "";
        public string ScopeTeam { get; set; } = "";
        public string ScopeMachines { get; set; } = "";
        public string Theme { get; set; } = "light";
        public DateTime? LastLoginAt { get; set; }
        public DateTime? PasswordChangedAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private const string Select =
        @"SELECT u.Id, u.Login, u.DisplayName, u.RoleId, r.Name AS RoleName, u.PasswordHash, u.MustChangePassword, u.IsActive,
                 u.FailedAttempts, u.LockedUntil, u.ScopeSectors, u.ScopeTeam, u.ScopeMachines, u.Theme,
                 u.LastLoginAt, u.PasswordChangedAt, u.CreatedAt
          FROM Users u JOIN Roles r ON r.Id = u.RoleId";

    public async Task<int> CountAsync()
    {
        using var c = await db.OpenAsync();
        return (int)await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Users");
    }

    public async Task<IReadOnlyList<AppUser>> ListAsync()
    {
        using var c = await db.OpenAsync();
        return (await c.QueryAsync<UserRow>(Select + " ORDER BY u.Login")).Select(Map).ToList();
    }

    public async Task<AppUser?> GetAsync(int id)
    {
        using var c = await db.OpenAsync();
        var row = await c.QuerySingleOrDefaultAsync<UserRow>(Select + " WHERE u.Id = @id", new { id });
        return row == null ? null : Map(row);
    }

    public async Task<AppUser?> GetByLoginAsync(string login)
    {
        using var c = await db.OpenAsync();
        var row = await c.QuerySingleOrDefaultAsync<UserRow>(Select + " WHERE u.Login = @login", new { login });
        return row == null ? null : Map(row);
    }

    public async Task<int> InsertAsync(AppUser user)
    {
        using var c = await db.OpenAsync();
        var id = await c.ExecuteScalarAsync<long>(
            @"INSERT INTO Users (Login, DisplayName, RoleId, PasswordHash, MustChangePassword, IsActive, FailedAttempts, LockedUntil,
                                 ScopeSectors, ScopeTeam, ScopeMachines, Theme, LastLoginAt, PasswordChangedAt, CreatedAt, UpdatedAt)
              VALUES (@Login, @DisplayName, @RoleId, @PasswordHash, @MustChangePassword, @IsActive, @FailedAttempts, @LockedUntil,
                      @ScopeSectors, @ScopeTeam, @ScopeMachines, @Theme, @LastLoginAt, @PasswordChangedAt, @CreatedAt, @UpdatedAt)" + db.ReturnId,
            Params(user));
        return (int)id;
    }

    public async Task UpdateAsync(AppUser user)
    {
        using var c = await db.OpenAsync();
        await c.ExecuteAsync(
            @"UPDATE Users SET DisplayName = @DisplayName, RoleId = @RoleId, PasswordHash = @PasswordHash,
                     MustChangePassword = @MustChangePassword, IsActive = @IsActive, FailedAttempts = @FailedAttempts,
                     LockedUntil = @LockedUntil, ScopeSectors = @ScopeSectors, ScopeTeam = @ScopeTeam, ScopeMachines = @ScopeMachines,
                     Theme = @Theme, LastLoginAt = @LastLoginAt, PasswordChangedAt = @PasswordChangedAt, UpdatedAt = @UpdatedAt
              WHERE Id = @Id",
            Params(user));
    }

    private static object Params(AppUser u) => new
    {
        u.Id, u.Login, u.DisplayName, u.RoleId, u.PasswordHash, u.MustChangePassword, u.IsActive, u.FailedAttempts, u.LockedUntil,
        ScopeSectors = DataScope.ToCsv(u.Scope.Sectors),
        ScopeTeam = u.Scope.Team ?? "",
        ScopeMachines = DataScope.ToCsv(u.Scope.Machines),
        u.Theme, u.LastLoginAt, u.PasswordChangedAt,
        CreatedAt = u.CreatedAt == default ? DateTime.Now : u.CreatedAt,
        UpdatedAt = DateTime.Now,
    };

    private static AppUser Map(UserRow r) => new()
    {
        Id = (int)r.Id,
        Login = r.Login,
        DisplayName = r.DisplayName,
        RoleId = (int)r.RoleId,
        RoleName = r.RoleName,
        PasswordHash = r.PasswordHash,
        MustChangePassword = r.MustChangePassword,
        IsActive = r.IsActive,
        FailedAttempts = (int)r.FailedAttempts,
        LockedUntil = r.LockedUntil,
        Scope = new DataScope(DataScope.FromCsv(r.ScopeSectors), string.IsNullOrWhiteSpace(r.ScopeTeam) ? null : r.ScopeTeam, DataScope.FromCsv(r.ScopeMachines)),
        Theme = r.Theme,
        LastLoginAt = r.LastLoginAt,
        PasswordChangedAt = r.PasswordChangedAt,
        CreatedAt = r.CreatedAt,
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
