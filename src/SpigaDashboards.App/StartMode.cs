using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace SpigaDashboards.App;

// How the application starts on this PC.
internal static class StartMode
{
    // Written by the installer when installed with KIOSK=1 (TV / hall PCs).
    private const string RegistryKey = @"Software\L2I\SpigaDashboards";
    private const string KioskValue = "KioskMode";

    // Kiosk (dashboard alone, no sign-in) when started with --kiosk, or on a
    // PC installed as a screen: however the app is launched there (shortcut,
    // startup task, the restart after repeated errors), it stays a screen.
    public static bool IsKiosk(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--kiosk", StringComparison.OrdinalIgnoreCase)))
            return true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
            return key?.GetValue(KioskValue) is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Version of the Edge WebView2 runtime, null when it is not installed.
    public static string? WebView2Version()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
