using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace SpigaDashboards.App;

// What the application opens.
//  - App: SPIGA Suivi Production, with sign-in (the whole application).
//  - Kiosk: SPIGA Dashboard, the dashboard without sign-in, with Historique,
//    Arrêts, the TV window and « Se connecter ».
//  - Tv: the TV kiosk, the slides alone, full screen.
internal enum AppStart { App, Kiosk, Tv }

// How the application starts on this PC.
internal static class StartMode
{
    // Written by the installer when installed with KIOSK=1 (dashboard) or
    // KIOSK=2 (TV): what the PC opens when started without an argument.
    private const string RegistryKey = @"Software\L2I\SpigaDashboards";
    private const string KioskValue = "KioskMode";

    // The shortcut's argument first (--app, --kiosk, --tv: the three
    // shortcuts of the installation); without one (startup task, the restart
    // after repeated errors, an old shortcut), what the PC was installed as.
    public static AppStart Of(string[] args)
    {
        bool Has(string arg) => args.Any(a => string.Equals(a, arg, StringComparison.OrdinalIgnoreCase));
        if (Has("--tv"))
            return AppStart.Tv;
        if (Has("--kiosk"))
            return AppStart.Kiosk;
        if (Has("--app"))
            return AppStart.App;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
            return key?.GetValue(KioskValue) switch
            {
                2 => AppStart.Tv,
                int value when value != 0 => AppStart.Kiosk,
                _ => AppStart.App,
            };
        }
        catch (Exception)
        {
            return AppStart.App;
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
