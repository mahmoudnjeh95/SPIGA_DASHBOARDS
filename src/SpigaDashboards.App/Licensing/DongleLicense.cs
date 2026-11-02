using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpigaDashboards.App.Licensing;

// The spiga.lic file at the root of a licence dongle. It names the USB stick it
// was issued for (hardware serial) and is signed with SPIGA's private key, so
// it can neither be edited nor copied onto another stick. Licences are permanent.
public sealed class DongleLicense
{
    public const string FileName = "spiga.lic";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public string Id { get; set; } = "";
    public string Client { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Issued { get; set; } = "";
    public string Signature { get; set; } = "";

    // What the signature covers: changing any of these fields breaks it.
    private byte[] Payload() => Encoding.UTF8.GetBytes($"SPIGA-LIC-1\n{Id}\n{Client}\n{Serial}\n{Issued}");

    public void Sign(ECDsa privateKey) =>
        Signature = Convert.ToBase64String(privateKey.SignData(Payload(), HashAlgorithmName.SHA256));

    public bool HasValidSignature(string publicKeyBase64)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(Payload(), Convert.FromBase64String(Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    public static DongleLicense? Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<DongleLicense>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    // The licence on the dongle at driveRoot ("E:\"), or null with the reason why not.
    public static DongleLicense? Check(string driveRoot, out string reason)
    {
        var path = Path.Combine(driveRoot, FileName);
        if (!File.Exists(path))
        {
            reason = $"pas de fichier {FileName}";
            return null;
        }

        var license = Load(path);
        if (license == null)
        {
            reason = $"{FileName} illisible";
            return null;
        }
        if (!license.HasValidSignature(LicensePublicKey.Value))
        {
            reason = "signature invalide (fichier modifie ou cle inconnue)";
            return null;
        }

        var drive = UsbDriveIdentity.Read(driveRoot);
        if (drive is not { IsUsb: true } || drive.Serial == null)
        {
            reason = "le lecteur n'est pas une cle USB avec numero de serie";
            return null;
        }
        if (!string.Equals(drive.Serial, license.Serial, StringComparison.Ordinal))
        {
            reason = "licence copiee depuis une autre cle (numero de serie different)";
            return null;
        }

        reason = "";
        return license;
    }

    // First valid dongle plugged into this PC, if any.
    public static DongleLicense? FindPlugged()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            // Network or optical drives can stall; a USB stick is Removable, or Fixed for some models.
            if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed))
                continue;
            try
            {
                if (drive.IsReady && Check(drive.RootDirectory.FullName, out _) is { } license)
                    return license;
            }
            catch (IOException)
            {
                // Stick pulled out mid-check: just not this one.
            }
        }
        return null;
    }
}
