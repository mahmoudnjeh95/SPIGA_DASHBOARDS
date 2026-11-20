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

// AppSharedSettings: one row (Id = 1) holding SharedSettings as JSON and a
// version bumped on every save, so a PC sees a change with one tiny query
// and a save made from stale values is refused instead of overwriting.
// In the application database (spiga_suivi), created by its migrations; the
// production database "stats" is only read -- except while PCs still run
// 2.0, which keep these settings in stats: then every PC uses them there
// (PlantDataLocation).
public class SharedSettingsRepository
{
    private readonly PlantDataLocation _location;

    // appData: the application database; live: the production database.
    public SharedSettingsRepository(IDbConnectionFactory appData, IDbConnectionFactory live)
    {
        _location = new PlantDataLocation(appData, live);
    }

    // Kept for the callers that reset after a database switch: nothing cached.
    public void Reset() { }

    private static Task EnsureSchemaAsync() => Task.CompletedTask;

    // 0 = nothing shared yet.
    public async Task<int> GetVersionAsync()
    {
        await OffUiThread.Switch();
        await EnsureSchemaAsync();
        using var connection = (await _location.ReadTargetAsync()).CreateConnection();
        return await connection.ExecuteScalarAsync<int?>("SELECT Version FROM AppSharedSettings WHERE Id = 1") ?? 0;
    }

    public async Task<SharedSettingsRow?> GetAsync()
    {
        await OffUiThread.Switch();
        await EnsureSchemaAsync();
        using var connection = (await _location.ReadTargetAsync()).CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<SharedSettingsRow>(
            "SELECT Version, Json, UpdatedAt, UpdatedBy FROM AppSharedSettings WHERE Id = 1");
    }

    // Saves over expectedVersion only: returns the new version, or null when
    // another PC saved in between (or created the first row at the same time).
    public async Task<int?> SaveAsync(string json, int expectedVersion, string updatedBy)
    {
        await OffUiThread.Switch();
        await EnsureSchemaAsync();
        using var connection = (await _location.WriteTargetAsync()).CreateConnection();
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
