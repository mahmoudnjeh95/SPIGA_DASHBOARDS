using System.IO;
using System.Security.Cryptography;
using SpigaDashboards.App.Licensing;

namespace SpigaLicenseManager;

// Makes the dongles. The private key (private.pem) and the register of issued
// dongles (dongles.csv, ';' for French Excel) live together in one folder.
public sealed class LicenseIssuer(string keysDir)
{
    public sealed record Entry(string Id, string Serial, string Client, string Issued, string Model);

    public string KeysDir { get; } = keysDir;
    private string PrivateKeyPath => Path.Combine(KeysDir, "private.pem");
    private string RegistryPath => Path.Combine(KeysDir, "dongles.csv");

    // Why this folder can't make dongles, or null when it can.
    public string? KeyProblem()
    {
        if (!File.Exists(PrivateKeyPath))
            return "private.pem introuvable dans ce dossier";
        try
        {
            LoadKey().Dispose();
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    public IReadOnlyList<Entry> Entries()
    {
        if (!File.Exists(RegistryPath))
            return [];
        return File.ReadAllLines(RegistryPath).Skip(1)
            .Select(line => line.Split(';'))
            .Where(f => f.Length >= 5)
            .Select(f => new Entry(f[0], f[1], f[2], f[3], f[4]))
            .ToList();
    }

    // Same stick again (licence file lost or re-made): it keeps its ID.
    public string NextId(string? serial)
    {
        var entries = Entries();
        return entries.LastOrDefault(e => e.Serial == serial)?.Id
               ?? $"SPIGA-{entries.Select(e => e.Id).Distinct().Count() + 1:000}";
    }

    // Writes spiga.lic on the stick, reads it back through the dashboard's own
    // check, and only then records it. Throws InvalidOperationException with
    // the reason when the stick can't be made into a dongle.
    public DongleLicense Issue(string driveRoot, string client, string id)
    {
        var identity = UsbDriveIdentity.Read(driveRoot);
        if (identity is not { IsUsb: true })
            throw new InvalidOperationException($"{driveRoot} n'est pas une clé USB.");
        if (identity.Serial == null)
            throw new InvalidOperationException($"{identity.Model} ne donne pas de numéro de série : utilisez une autre clé.");

        using var key = LoadKey();
        var license = new DongleLicense
        {
            Id = id.Trim(),
            // ';' would split the register line.
            Client = client.Trim().Replace(';', ','),
            Serial = identity.Serial,
            Issued = DateTime.Today.ToString("yyyy-MM-dd"),
        };
        license.Sign(key);

        var path = Path.Combine(driveRoot, DongleLicense.FileName);
        if (File.Exists(path))
            File.SetAttributes(path, FileAttributes.Normal);
        license.Save(path);
        // Read-only: harder to delete by accident while the stick is used for something else.
        File.SetAttributes(path, FileAttributes.ReadOnly);

        if (DongleLicense.Check(driveRoot, out var reason) == null)
            throw new InvalidOperationException($"Licence écrite mais refusée à la relecture : {reason}");

        if (!File.Exists(RegistryPath))
            File.WriteAllText(RegistryPath, "id;serie;client;emise le;modele" + Environment.NewLine);
        File.AppendAllText(RegistryPath,
            $"{license.Id};{license.Serial};{license.Client};{license.Issued};{identity.Model}{Environment.NewLine}");
        return license;
    }

    private ECDsa LoadKey()
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(PrivateKeyPath));
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or IOException)
        {
            key.Dispose();
            throw new InvalidOperationException("private.pem illisible ou n'est pas une clé privée");
        }

        if (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != LicensePublicKey.Value)
        {
            key.Dispose();
            throw new InvalidOperationException("cette clé privée ne correspond pas à SPIGA Dashboards");
        }
        return key;
    }
}
