using System.IO;
using System.Text.Json;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Services;

public class SettingsService
{
    // SPIGA_SETTINGS_DIR (development only) points a test copy at its own
    // settings, e.g. the SQLite dev DB, instead of the production ones.
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetEnvironmentVariable("SPIGA_SETTINGS_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpigaDashboards"),
        "settings.json");

    public static string SettingsFolder => Path.GetDirectoryName(SettingsPath)!;

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            UpgradePassword(settings);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new AppSettings();
        }
    }

    // A password saved by a version up to 2.0 (machine scope: readable by any
    // account of the PC) is protected again for this Windows user only.
    private void UpgradePassword(AppSettings settings)
    {
        try
        {
            if (PasswordProtector.Upgrade(settings.SqlServer.EncryptedPassword) is { } upgraded)
            {
                settings.SqlServer.EncryptedPassword = upgraded;
                Save(settings);
                AppLogger.Log("Mot de passe SQL re-chiffré pour ce seul compte Windows.");
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or PlatformNotSupportedException)
        {
            AppLogger.Log("Mot de passe SQL non re-chiffré : " + ex.Message);
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
