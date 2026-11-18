using System.Windows;
using Spiga.UI.Services;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels.Planning;

namespace SpigaDashboards.App;

// What the Razor screens ask of the desktop: show a WPF page (dashboard,
// arrêts, qualification / statistiques / planification des arrêts,
// paramètres) in the main window beside the menu,
// open the TV screen, follow the user's theme. One view per page, kept while
// the user goes elsewhere (no edit lost), dropped when it is closed with its
// own button or on sign-out.
internal sealed class DesktopBridge(Func<DashboardView> createDashboard) : IDesktopBridge
{
    private ShellWindow? _shell;
    private DashboardView? _dashboard;
    private readonly Dictionary<NativePage, FrameworkElement> _views = new();
    private readonly HashSet<NativePage> _creating = new();
    private NativePage? _wanted;
    private bool _allowSettings;
    private bool? _dark;
    // Shared by the three stop follow-up pages, so they link up.
    private PlanningViewModel? _planning;

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

    public void ShowNative(NativePage? page, bool allowSettings) => OnUi(async () =>
    {
        _wanted = page;
        _allowSettings = allowSettings;
        if (_dashboard != null)
            _dashboard.AllowSettings(allowSettings);
        if (page is not { } p)
        {
            _shell?.HideNative();
            return;
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
    });

    public void OpenTvScreen(bool allowSettings) => OnUi(() => Dashboard().OpenTvScreen());

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
        }
        _dashboard.AllowSettings(_allowSettings);
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
                    var stops = new StopsView(Dashboard().CreateStopsViewModel());
                    stops.CloseRequested += () => Close(page);
                    view = stops;
                    break;
                case NativePage.StopQualification or NativePage.StopStatistics or NativePage.StopPlanning:
                    var section = new PlanningSectionView(Planning(), Section(page));
                    section.CloseRequested += () => Close(page);
                    view = section;
                    break;
                default:
                    var settings = new SettingsView(await Dashboard().CreateSettingsViewModelAsync());
                    settings.CloseRequested += () => Close(page);
                    view = settings;
                    break;
            }
            _views[page] = view;
            return view;
        }
        finally
        {
            _creating.Remove(page);
        }
    }

    private PlanningViewModel Planning()
    {
        if (_planning == null)
        {
            _planning = Dashboard().CreatePlanningViewModel();
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
