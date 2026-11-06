using Dapper;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Data;

public class SharedSettingsRow
{
    public int Version { get; set; }
    public string Json { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}

// dbo.AppSharedSettings: one row (Id = 1) holding SharedSettings as JSON and
// a version bumped on every save, so a PC sees a change with one tiny query
// and a save made from stale values is refused instead of overwriting.
// Created on first use when the login may (Windows login on the server);
// database/shared_settings.sql does the same for a DBA, with the grants for
// the dashboard login.
public class SharedSettingsRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private bool _schemaReady;

    public SharedSettingsRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    private bool IsSqlServer => _connectionFactory.Provider == DatabaseProvider.SqlServer;

    // Forget the check after a database switch (Parametres).
    public void Reset() => _schemaReady = false;

    private async Task EnsureSchemaAsync()
    {
        if (_schemaReady)
            return;
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(IsSqlServer
            ? @"IF OBJECT_ID('dbo.AppSharedSettings') IS NULL
                CREATE TABLE dbo.AppSharedSettings (
                    Id INT NOT NULL PRIMARY KEY CHECK (Id = 1),
                    Version INT NOT NULL,
                    Json NVARCHAR(MAX) NOT NULL,
                    UpdatedAt DATETIME2(0) NOT NULL,
                    UpdatedBy NVARCHAR(100) NOT NULL)"
            : @"CREATE TABLE IF NOT EXISTS AppSharedSettings (
                    Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                    Version INTEGER NOT NULL, Json TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL, UpdatedBy TEXT NOT NULL)");
        _schemaReady = true;
    }

    // 0 = nothing shared yet.
    public async Task<int> GetVersionAsync()
    {
        await EnsureSchemaAsync();
        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<int?>("SELECT Version FROM AppSharedSettings WHERE Id = 1") ?? 0;
    }

    public async Task<SharedSettingsRow?> GetAsync()
    {
        await EnsureSchemaAsync();
        using var connection = _connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<SharedSettingsRow>(
            "SELECT Version, Json, UpdatedAt, UpdatedBy FROM AppSharedSettings WHERE Id = 1");
    }

    // Saves over expectedVersion only: returns the new version, or null when
    // another PC saved in between (or created the first row at the same time).
    public async Task<int?> SaveAsync(string json, int expectedVersion, string updatedBy)
    {
        await EnsureSchemaAsync();
        using var connection = _connectionFactory.CreateConnection();
        var args = new { json, expectedVersion, updatedAt = DateTime.Now, updatedBy };

        if (expectedVersion == 0)
        {
            var exists = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AppSharedSettings WHERE Id = 1");
            if (exists > 0)
                return null;
            try
            {
                await connection.ExecuteAsync(
                    @"INSERT INTO AppSharedSettings (Id, Version, Json, UpdatedAt, UpdatedBy)
                      VALUES (1, 1, @json, @updatedAt, @updatedBy)", args);
                return 1;
            }
            catch (System.Data.Common.DbException)
            {
                // Lost the race on the primary key: someone else's settings came
                // first. Anything else (no INSERT right, ...) is a real error.
                if (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AppSharedSettings WHERE Id = 1") > 0)
                    return null;
                throw;
            }
        }

        var updated = await connection.ExecuteAsync(
            @"UPDATE AppSharedSettings
              SET Version = Version + 1, Json = @json, UpdatedAt = @updatedAt, UpdatedBy = @updatedBy
              WHERE Id = 1 AND Version = @expectedVersion", args);
        return updated == 1 ? expectedVersion + 1 : null;
    }
}
