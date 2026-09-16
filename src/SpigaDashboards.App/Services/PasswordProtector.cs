using System.Security.Cryptography;
using System.Text;

namespace SpigaDashboards.App.Services;

// Wraps DPAPI so the SQL login password never sits in settings.json as plain text.
// Scoped to the current Windows user/machine, which matches how the app is installed.
public static class PasswordProtector
{
    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return "";

        var bytes = Encoding.UTF8.GetBytes(plainText);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted))
            return "";

        try
        {
            var bytes = Convert.FromBase64String(encrypted);
            var plainBytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException)
        {
            // Encrypted under a different user/machine (e.g. settings.json copied over) - treat as unset.
            return "";
        }
    }
}
