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
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new AppSettings();
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
