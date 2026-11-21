using System.Windows;
using Spiga.UI.Services;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App;

// What the Razor screens ask of the desktop: open the TV window (the web
// Écran TV, WebTvWindow), follow the user's theme, this PC's part of
// Paramètres. Every page, the dashboard included, is a Razor page since
// 2026-11-21: no WPF page is shown beside the menu any more.
internal sealed class DesktopBridge : IDesktopBridge
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;

    public DesktopBridge(AppSettings settings, SettingsService settingsService, SwitchableConnectionFactory production,
        SwitchableConnectionFactory appData, SharedSettingsSync sharedSettings)
    {
        _settings = settings;
        _settingsService = settingsService;
        // The Razor screens read the settings as they go: nothing to reload.
        Station = new StationHost(settings, production, appData, sharedSettings, () => { });
    }

    // Paramètres (Razor): this PC's connection, document, stop alert.
    public IStationHost? Station { get; }

    // The services of the Razor screens, for the TV window's web view.
    public IServiceProvider? Services { get; set; }

    private ShellWindow? _shell;
    private WebTvWindow? _tv;
    private bool? _dark;

    public bool IsAvailable => true;

    public event Action? UserActivity;
    public event Action<NativePage>? NavigateRequested { add { } remove { } }

    // The main window (signed-in shell or kiosk). The TV comes back if it was
    // left open, and closes with it (remembering it was open).
    public void Attach(ShellWindow shell)
    {
        _shell = shell;
        shell.NativeActivity += () => UserActivity?.Invoke();
        shell.Closed += (_, _) => _tv?.CloseForShutdown();
        if (_settings.TvScreen.IsOpen)
            shell.Loaded += (_, _) => OnUi(() => OpenTv(activate: false));
    }

    public void ShowNative(NativePage? page, NativeAccess access) => OnUi(() => _shell?.HideNative());

    public void DiscardNativePages() => OnUi(() => _shell?.HideNative());

    public void OpenTvScreen() => OnUi(() => OpenTv(activate: true));

    private void OpenTv(bool activate)
    {
        if (Services == null)
            return;
        if (_tv != null)
        {
            if (_tv.WindowState == WindowState.Minimized)
                _tv.WindowState = WindowState.Normal;
            _tv.Activate();
            return;
        }
        _tv = new WebTvWindow(Services, _settings, _settingsService) { ShowActivated = activate };
        _tv.Closed += (_, _) => _tv = null;
        _tv.Show();
    }

    public void ApplyTheme(bool dark)
    {
        if (_dark == dark)
            return;
        _dark = dark;
        OnUi(() => ThemeService.Apply(dark ? AppTheme.Dark : AppTheme.Light));
    }

    // After the Razor event handler has returned.
    private static void OnUi(Action action) => Application.Current.Dispatcher.BeginInvoke(action);
}
