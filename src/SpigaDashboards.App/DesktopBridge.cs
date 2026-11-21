using System.Windows;
using Spiga.UI.Services;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App;

// What the Razor screens ask of the desktop: open the TV screen (the WPF
// dashboard drives it; every page, the dashboard included, is a Razor page
// since 2026-11-21), follow the user's theme. The dashboard is kept while the user
// goes elsewhere, and updated in place when the account's rights change; it
// reads through repositories limited to the machines of the account's scope
// (MachineAccess).
internal sealed class DesktopBridge : IDesktopBridge
{
    private readonly Func<DashboardView> createDashboard;

    public DesktopBridge(Func<DashboardView> createDashboard, AppSettings settings, SwitchableConnectionFactory production,
        SwitchableConnectionFactory appData, SharedSettingsSync sharedSettings)
    {
        this.createDashboard = createDashboard;
        Station = new StationHost(settings, production, appData, sharedSettings, () => OnUi(() => _dashboard?.SettingsApplied()));
    }

    // Paramètres (Razor): this PC's connection, document, stop alert.
    public IStationHost? Station { get; }

    private ShellWindow? _shell;
    private DashboardView? _dashboard;
    private NativeAccess _dashboardAccess = NativeAccess.None;
    private bool? _dark;

    public bool IsAvailable => true;

    public event Action? UserActivity;
    public event Action<NativePage>? NavigateRequested;

    public void Attach(ShellWindow shell)
    {
        _shell = shell;
        shell.NativeActivity += () => UserActivity?.Invoke();
        shell.Closed += (_, _) => _dashboard?.CloseTvForShutdown();
    }

    public void ShowNative(NativePage? page, NativeAccess access) => OnUi(() =>
    {
        // Only the dashboard is a WPF page (NativePages.IsWpf).
        if (page != NativePage.Dashboard)
        {
            _shell?.HideNative();
            return;
        }
        _dashboardAccess = access;
        if (_dashboard != null)
            Apply(_dashboard, access);
        _shell?.ShowNative(Dashboard());
    });

    public void DiscardNativePages() => OnUi(() =>
    {
        _shell?.HideNative();
        // The dashboard is kept (it takes a while to build) but shows
        // nothing until the next account opens it with its own rights.
        _dashboardAccess = NativeAccess.None;
        if (_dashboard != null)
            Apply(_dashboard, NativeAccess.None);
    });

    // The dashboard page is Razor now: the WPF dashboard only drives the TV
    // window, with the rights of the account that opens it.
    public void OpenTvScreen(NativeAccess access) => OnUi(() =>
    {
        _dashboardAccess = access;
        var dashboard = Dashboard();
        Apply(dashboard, access);
        dashboard.OpenTvScreen();
    });

    private static void Apply(DashboardView dashboard, NativeAccess a)
    {
        dashboard.Access.Set(a.MachinesKey, a.Machines is { } rule ? rule.Allows : null);
        dashboard.AllowSettings(a.CanSettings);
        dashboard.AllowTv(a.CanTv);
        dashboard.AllowExport(a.CanExport);
        dashboard.AllowNote(a.CanEditNote);
        dashboard.Author = a.UserLogin.Length > 0 ? ChangeAuthor.Account(a.UserLogin, a.UserName) : ChangeAuthor.Kiosk;
        dashboard.LockTeam(a.Team);
    }

    public void ApplyTheme(bool dark)
    {
        if (_dark == dark)
            return;
        _dark = dark;
        OnUi(() => ThemeService.Apply(dark ? AppTheme.Dark : AppTheme.Light));
    }

    private DashboardView Dashboard()
    {
        if (_dashboard == null)
        {
            _dashboard = createDashboard();
            _dashboard.SetEmbedded(true);
            _dashboard.PageRequested = name =>
            {
                NativePage? page = name switch
                {
                    "arrets" => NativePage.Stops,
                    "parametres" => NativePage.Settings,
                    _ => null,
                };
                if (page is { } p)
                    NavigateRequested?.Invoke(p);
            };
            Apply(_dashboard, _dashboardAccess);
        }
        return _dashboard;
    }

    // After the Razor event handler has returned.
    private static void OnUi(Action action) => Application.Current.Dispatcher.BeginInvoke(action);
}
