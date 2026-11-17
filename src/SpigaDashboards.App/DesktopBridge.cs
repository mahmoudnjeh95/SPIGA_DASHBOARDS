using System.Windows;
using Spiga.UI.Services;

namespace SpigaDashboards.App;

// Opens the SPIGA Dashboards windows that are not Razor screens yet
// (slides, TV, arrêts, planification) from the new shell. One dashboard
// window at a time, created on first use.
internal sealed class DesktopBridge(Func<MainWindow> createDashboard) : IDesktopBridge
{
    private MainWindow? _dashboard;

    public bool IsAvailable => true;

    public void OpenDashboard(bool allowSettings) => OnUi(() => Dashboard(allowSettings));

    public void OpenTvScreen(bool allowSettings) => OnUi(() => Dashboard(allowSettings).OpenTvScreen());

    public void OpenStops(bool allowSettings) => OnUi(() => Dashboard(allowSettings).ShowStops());

    public void OpenPlanning(bool allowSettings) => OnUi(() => Dashboard(allowSettings).ShowPlanning());

    private MainWindow Dashboard(bool allowSettings)
    {
        if (_dashboard == null)
        {
            _dashboard = createDashboard();
            _dashboard.Closed += (_, _) => _dashboard = null;
            _dashboard.Show();
        }
        else
        {
            if (_dashboard.WindowState == WindowState.Minimized)
                _dashboard.WindowState = WindowState.Normal;
            _dashboard.Activate();
        }
        _dashboard.AllowSettings(allowSettings);
        return _dashboard;
    }

    // After the Razor event handler has returned (the windows open modal dialogs).
    private static void OnUi(Action action) => Application.Current.Dispatcher.BeginInvoke(action);
}
