using System.Windows;
using Spiga.UI.Services;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels.Planning;

namespace SpigaDashboards.App;

// What the Razor screens ask of the desktop: show a WPF page (dashboard,
// arrêts, qualification / statistiques / planification des arrêts,
// paramètres) in the main window beside the menu,
// open the TV screen, follow the user's theme. One view per page, kept while
// the user goes elsewhere (no edit lost), dropped when it is closed with its
// own button or on sign-out, and rebuilt when the account's rights there
// changed. Each page reads through repositories limited to the machines of
// the account's scope for that page (MachineAccess): nothing else can be
// listed, read or written from it.
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
    private readonly Dictionary<NativePage, FrameworkElement> _views = new();
    private readonly HashSet<NativePage> _creating = new();
    private NativePage? _wanted;
    // The rights each page was built with: a page whose rights changed is
    // rebuilt (the dashboard, kept, is updated in place).
    private readonly Dictionary<NativePage, string> _builtWith = new();
    private NativeAccess _dashboardAccess = NativeAccess.None;
    private NativeAccess _access = NativeAccess.None;
    private bool? _dark;
    // Shared by the three stop follow-up pages, so they link up.
    private PlanningViewModel? _planning;
    private string? _planningKey;

    public bool IsAvailable => true;

    public event Action? UserActivity;
    public event Action<NativePage>? NativeClosed;
    public event Action<NativePage>? NavigateRequested;

    public void Attach(ShellWindow shell)
    {
        _shell = shell;
        shell.NativeActivity += () => UserActivity?.Invoke();
        shell.Closed += (_, _) => _dashboard?.CloseTvForShutdown();
    }

    public void ShowNative(NativePage? page, NativeAccess access) => OnUi(async () =>
    {
        _wanted = page;
        _access = access;
        if (page is not { } p)
        {
            _shell?.HideNative();
            return;
        }
        if (p == NativePage.Dashboard)
        {
            _dashboardAccess = access;
            if (_dashboard != null)
                Apply(_dashboard, access);
        }
        else if (_builtWith.TryGetValue(p, out var built) && built != KeyOf(access))
        {
            // Rights changed (another account, a role or platform edited):
            // the page starts again with the new ones.
            if (IsPlanning(p))
                DropPlanning();
            else
                Drop(p);
        }
        var view = await ViewAsync(p);
        // The user may have moved on while the page was being built.
        if (view != null && _wanted == p)
            _shell?.ShowNative(view);
    });

    public void DiscardNativePages() => OnUi(() =>
    {
        _shell?.HideNative();
        foreach (var page in _views.Keys.Where(k => k != NativePage.Dashboard).ToList())
            Drop(page);
        _planning = null;
        // The dashboard is kept (it takes a while to build) but shows
        // nothing until the next account opens it with its own rights.
        _dashboardAccess = _access = NativeAccess.None;
        if (_dashboard != null)
            Apply(_dashboard, NativeAccess.None);
    });

    public void OpenTvScreen() => OnUi(() => Dashboard().OpenTvScreen());

    public void PlanPreventive(NativeAccess access, string machineId, string component) => OnUi(() =>
    {
        // The planning model the board will be built with (same rights).
        Planning(access).QueuePreventive(machineId, component);
        NavigateRequested?.Invoke(NativePage.StopPlanning);
    });

    private static string KeyOf(NativeAccess a)
        => $"{a.UserLogin}|{a.MachinesKey}|{a.Team}|{a.CanExport}|{a.CanValidate}|{a.CanSettings}|{a.CanTv}|{a.CanEditNote}";

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

    // A repository scope for a new page.
    private static MachineAccess AccessFor(NativeAccess a)
    {
        var access = MachineAccess.Nothing();
        access.Set(a.MachinesKey, a.Machines is { } rule ? rule.Allows : null);
        return access;
    }

    private static bool IsPlanning(NativePage page)
        => page is NativePage.StopQualification or NativePage.StopStatistics or NativePage.StopPlanning;

    private void DropPlanning()
    {
        foreach (var page in _views.Keys.Where(IsPlanning).ToList())
            Drop(page);
        _planning = null;
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
            _views[NativePage.Dashboard] = _dashboard;
            Apply(_dashboard, _dashboardAccess);
        }
        return _dashboard;
    }

    private async Task<FrameworkElement?> ViewAsync(NativePage page)
    {
        if (_views.TryGetValue(page, out var existing))
            return existing;
        if (page == NativePage.Dashboard)
            return Dashboard();
        if (!_creating.Add(page))
            return null;
        try
        {
            FrameworkElement view;
            switch (page)
            {
                case NativePage.Stops:
                    var stops = new StopsView(Dashboard().CreateStopsViewModel(AccessFor(_access), _access.Team, _access.CanExport));
                    view = stops;
                    break;
                case NativePage.StopQualification or NativePage.StopStatistics or NativePage.StopPlanning:
                    var section = new PlanningSectionView(Planning(_access), Section(page));
                    view = section;
                    break;
                default:
                    // Station-wide: AppShell opens it only for an account
                    // covering the whole plant (NativePages.IsAllowed).
                    if (!_access.CanSettings)
                        return null;
                    var settings = new SettingsView(await Dashboard().CreateSettingsViewModelAsync(
                        ChangeAuthor.Account(_access.UserLogin, _access.UserName)));
                    settings.CloseRequested += () => Close(page);
                    view = settings;
                    break;
            }
            _views[page] = view;
            _builtWith[page] = KeyOf(_access);
            return view;
        }
        finally
        {
            _creating.Remove(page);
        }
    }

    // Shared by the three stop follow-up pages, built with the rights of the
    // first one opened; DropPlanning when another needs other rights.
    private PlanningViewModel Planning(NativeAccess access)
    {
        if (_planning != null && _planningKey != KeyOf(access))
            DropPlanning();
        if (_planning == null)
        {
            _planningKey = KeyOf(access);
            _planning = Dashboard().CreatePlanningViewModel(AccessFor(access), access.CanValidate,
                ChangeAuthor.Account(access.UserLogin, access.UserName));
            _planning.SectionRequested += s => NavigateRequested?.Invoke(Page(s));
        }
        return _planning;
    }

    private static PlanningSection Section(NativePage page) => page switch
    {
        NativePage.StopQualification => PlanningSection.Qualification,
        NativePage.StopStatistics => PlanningSection.Statistics,
        _ => PlanningSection.Planning,
    };

    private static NativePage Page(PlanningSection section) => section switch
    {
        PlanningSection.Qualification => NativePage.StopQualification,
        PlanningSection.Statistics => NativePage.StopStatistics,
        _ => NativePage.StopPlanning,
    };

    // Closed with its own button (Fermer, Enregistrer, Annuler): next time it
    // opens fresh, and the Razor shell goes back to the previous screen.
    private void Close(NativePage page)
    {
        Drop(page);
        NativeClosed?.Invoke(page);
    }

    private void Drop(NativePage page)
    {
        _builtWith.Remove(page);
        if (!_views.Remove(page, out var view))
            return;
        if (_wanted == page)
            _shell?.HideNative();
        _shell?.RemoveNative(view);
    }

    // After the Razor event handler has returned.
    private static void OnUi(Action action) => Application.Current.Dispatcher.BeginInvoke(action);

    private static void OnUi(Func<Task> action) => Application.Current.Dispatcher.BeginInvoke((Action)(async () => await action()));
}
