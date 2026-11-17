using System.Security.Cryptography;
using System.Text;

namespace SpigaDashboards.App.Services;

// Wraps DPAPI so the SQL login password never sits in settings.json as plain text.
// Machine scope: whichever Windows account opens the dashboard on this PC
// (operator, team leader, admin) connects without retyping it. Values saved by
// older versions (user scope) still decrypt for the user who saved them.
public static class PasswordProtector
{
    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return "";

        var bytes = Encoding.UTF8.GetBytes(plainText);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted))
            return "";

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encrypted);
        }
        catch (FormatException)
        {
            return "";
        }

        foreach (var scope in new[] { DataProtectionScope.LocalMachine, DataProtectionScope.CurrentUser })
        {
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, scope));
            }
            catch (CryptographicException)
            {
            }
        }
        // Encrypted on another machine (e.g. settings.json copied over) - treat as unset.
        return "";
    }
}
