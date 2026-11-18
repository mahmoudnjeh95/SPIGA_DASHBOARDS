using System.Security.Cryptography;
using System.Text;

namespace SpigaDashboards.App.Services;

// Wraps DPAPI so the SQL login password never sits in settings.json as plain
// text. User scope: settings.json is in the Windows user's own profile
// (%APPDATA%\SpigaDashboards), and only that Windows account can decrypt its
// password -- not any account of the PC, as with the machine scope used up to
// version 2.0 (any local user could read it back). The extra entropy keeps
// other programs of the same user from decrypting it blindly.
//
// Values saved by older versions (machine scope, or user scope without
// entropy) still decrypt, and SettingsService re-encrypts them at load.
// Recommended on site: Windows authentication (no password stored at all).
public static class PasswordProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SpigaDashboards.SqlLogin.v2");

    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return "";

        var bytes = Encoding.UTF8.GetBytes(plainText);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string encrypted) => Decrypt(encrypted).Plain;

    // A value of an older version (machine scope, or no entropy): the same
    // password protected the current way; null when nothing to change.
    public static string? Upgrade(string encrypted)
    {
        var (plain, current) = Decrypt(encrypted);
        return current || plain.Length == 0 ? null : Protect(plain);
    }

    private static (string Plain, bool Current) Decrypt(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted))
            return ("", true);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encrypted);
        }
        catch (FormatException)
        {
            return ("", true);
        }

        var attempts = new (byte[]? Entropy, DataProtectionScope Scope)[]
        {
            (Entropy, DataProtectionScope.CurrentUser),
            (null, DataProtectionScope.CurrentUser),
            (null, DataProtectionScope.LocalMachine),
        };
        for (var i = 0; i < attempts.Length; i++)
        {
            try
            {
                return (Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, attempts[i].Entropy, attempts[i].Scope)), i == 0);
            }
            catch (CryptographicException)
            {
            }
        }
        // Encrypted on another machine or by another Windows user (settings.json
        // copied over): treat as unset, the password is asked again.
        return ("", true);
    }
}
