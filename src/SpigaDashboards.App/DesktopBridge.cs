using System.Windows;
using Spiga.UI.Services;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App;

// What the Razor screens ask of the desktop: show the classic dashboard in
// the main window, open the TV / Arrêts / Planification windows, follow the
// user's theme. One dashboard view, created on first use.
internal sealed class DesktopBridge(Func<DashboardView> createDashboard) : IDesktopBridge
{
    private ShellWindow? _shell;
    private DashboardView? _view;
    private bool? _dark;

    public bool IsAvailable => true;

    public event Action? UserActivity;

    public void Attach(ShellWindow shell)
    {
        _shell = shell;
        shell.DashboardActivity += () => UserActivity?.Invoke();
        shell.Closed += (_, _) => _view?.CloseTvForShutdown();
    }

    public void ShowDashboard(bool visible, bool allowSettings) => OnUi(() =>
    {
        if (visible)
            _shell?.ShowDashboard(View(allowSettings));
        else
            _shell?.HideDashboard();
    });

    public void OpenTvScreen(bool allowSettings) => OnUi(() => View(allowSettings).OpenTvScreen());

    public void OpenStops(bool allowSettings) => OnUi(() => View(allowSettings).ShowStops());

    public void OpenPlanning(bool allowSettings) => OnUi(() => View(allowSettings).ShowPlanning());

    public void ApplyTheme(bool dark)
    {
        if (_dark == dark)
            return;
        _dark = dark;
        OnUi(() => ThemeService.Apply(dark ? AppTheme.Dark : AppTheme.Light));
    }

    private DashboardView View(bool allowSettings)
    {
        _view ??= createDashboard();
        _view.SetEmbedded(true);
        _view.AllowSettings(allowSettings);
        return _view;
    }

    // After the Razor event handler has returned (the windows open modal dialogs).
    private static void OnUi(Action action) => Application.Current.Dispatcher.BeginInvoke(action);
}
