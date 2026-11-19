using System.IO;
using System.Text.Json;

namespace SpigaLicenseManager;

// Only remembers where the private key folder is (%APPDATA%\SpigaLicenseManager).
public sealed class ManagerSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpigaLicenseManager", "settings.json");

    public string KeysDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SPIGA-Licence");

    public static ManagerSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(FilePath)) ?? new ManagerSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ManagerSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}
