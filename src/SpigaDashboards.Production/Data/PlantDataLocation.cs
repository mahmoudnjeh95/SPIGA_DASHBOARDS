using System.Text.Json;
using Dapper;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Data;

// Where the planning, the note, the machine names and the shared settings
// are read and written during the move from "stats" to the application
// database, PC by PC:
//
//  Legacy      some PCs still run 2.0, which write them in stats: 2.1 PCs
//              read and write there too, so every PC sees the same data
//              (the audit trail is still written in spiga_suivi).
//  Switching   IT is moving them (« Mettre à jour la base SPIGA »): writes
//              are refused for a couple of minutes while the rows are copied.
//  Application every PC runs 2.1: they are in spiga_suivi, stats is only read.
//
// Kept in spiga_suivi (AppSettings, key plant-data.location), read again
// every 30 s, so a screen running 24/7 follows the switch on its own. The
// first 2.1 PC to start decides: stats has the 2.0 tables (a 2.0 PC ever ran)
// → Legacy; a new site → Application.
public enum PlantDataMode
{
    Legacy,
    Switching,
    Application,
}

public sealed class PlantDataLocation(IDbConnectionFactory appData, IDbConnectionFactory live)
{
    public const string SettingKey = "plant-data.location";

    // How long a PC may keep the mode it read: the switch waits longer than
    // this before copying, so no PC still writes in stats by then.
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public sealed class State
    {
        public PlantDataMode Mode { get; set; }
        public DateTime ChangedAt { get; set; }
        public string ChangedBy { get; set; } = "";
    }

    private PlantDataMode? _mode;
    private DateTime _readAt;

    public IDbConnectionFactory ApplicationDatabase => appData;

    public IDbConnectionFactory ProductionDatabase => live;

    public async Task<PlantDataMode> ModeAsync()
    {
        if (_mode is { } cached && DateTime.UtcNow - _readAt < CacheDuration)
            return cached;
        var mode = (await ReadAsync(appData))?.Mode ?? await DecideAsync();
        _mode = mode;
        _readAt = DateTime.UtcNow;
        return mode;
    }

    // Where to read: stats until the switch is done.
    public async Task<IDbConnectionFactory> ReadTargetAsync()
        => await ModeAsync() == PlantDataMode.Application ? appData : live;

    // Where to write; refused while IT is switching.
    public async Task<IDbConnectionFactory> WriteTargetAsync()
        => await ModeAsync() switch
        {
            PlantDataMode.Application => appData,
            PlantDataMode.Legacy => live,
            _ => throw new InvalidOperationException(
                "Les données de planification passent dans la base de l'application (mise à jour en cours par l'informatique) : " +
                "réessayez dans deux minutes."),
        };

    public static async Task<State?> ReadAsync(IDbConnectionFactory appData)
    {
        using var connection = appData.CreateConnection();
        var json = await connection.QuerySingleOrDefaultAsync<string?>(
            $"SELECT Value FROM AppSettings WHERE {Key(appData)} = @key", new { key = SettingKey });
        return json == null ? null : JsonSerializer.Deserialize<State>(json);
    }

    public static async Task WriteAsync(IDbConnectionFactory appData, PlantDataMode mode, string by)
    {
        using var connection = appData.CreateConnection();
        var args = new
        {
            key = SettingKey,
            json = JsonSerializer.Serialize(new State { Mode = mode, ChangedAt = DateTime.Now, ChangedBy = by }),
            now = DateTime.Now,
            by,
        };
        var updated = await connection.ExecuteAsync(
            $"UPDATE AppSettings SET Value = @json, UpdatedAt = @now, UpdatedBy = @by WHERE {Key(appData)} = @key", args);
        if (updated == 0)
            await connection.ExecuteAsync(
                $"INSERT INTO AppSettings ({Key(appData)}, Value, UpdatedAt, UpdatedBy) VALUES (@key, @json, @now, @by)", args);
    }

    // First start of 2.1 on this application database.
    private async Task<PlantDataMode> DecideAsync()
    {
        using var connection = live.CreateConnection();
        var has2_0Tables = await connection.ExecuteScalarAsync<long>(live.Provider == DatabaseProvider.SqlServer
            ? "SELECT CASE WHEN OBJECT_ID('dbo.AppSharedSettings') IS NULL AND OBJECT_ID('dbo.PlanStopQualifications') IS NULL THEN 0 ELSE 1 END"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('AppSharedSettings', 'PlanStopQualifications')") > 0;
        var mode = has2_0Tables ? PlantDataMode.Legacy : PlantDataMode.Application;
        // Another PC may have decided meanwhile: its choice stands.
        if ((await ReadAsync(appData))?.Mode is { } decided)
            return decided;
        await WriteAsync(appData, mode, Environment.MachineName);
        return mode;
    }

    private static string Key(IDbConnectionFactory factory) => factory.Provider == DatabaseProvider.SqlServer ? "[Key]" : "Key";

    // IT, once every PC runs 2.1: writes paused, copy, then the new place.
    // progress: what to show meanwhile.
    // pause: how long writes are stopped before the copy (tests: zero).
    public static async Task<IReadOnlyList<string>> SwitchToApplicationAsync(
        IDbConnectionFactory appData, IDbConnectionFactory live, string by, Action<string>? progress = null, TimeSpan? pause = null)
    {
        if ((await ReadAsync(appData))?.Mode == PlantDataMode.Application)
            return new[] { "Déjà dans la base de l'application." };
        await WriteAsync(appData, PlantDataMode.Switching, by);
        // Every PC has read "Switching" by then and stopped writing in stats.
        var wait = pause ?? CacheDuration * 3;
        for (var left = wait; left > TimeSpan.Zero; left -= TimeSpan.FromSeconds(5))
        {
            progress?.Invoke($"Les postes arrêtent d'écrire dans la base de production… {(int)left.TotalSeconds} s");
            await Task.Delay(left < TimeSpan.FromSeconds(5) ? left : TimeSpan.FromSeconds(5));
        }
        try
        {
            progress?.Invoke("Copie des données…");
            var report = await new LegacyDataImporter(live, appData).ImportAsync();
            await WriteAsync(appData, PlantDataMode.Application, by);
            return report;
        }
        catch
        {
            // Nothing half done is used: back to stats, the switch can be run again.
            await WriteAsync(appData, PlantDataMode.Legacy, by);
            throw;
        }
    }
}
