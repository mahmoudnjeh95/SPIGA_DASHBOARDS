using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels;

namespace SpigaDashboards.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // WPF bindings format with en-US by default, whatever the Windows
        // culture: switch every StringFormat (N0, 0.0, ...) to French
        // (66,8 % / 1 234 567) before any window or control is created.
        var french = XmlLanguage.GetLanguage("fr-FR");
        // FrameworkElement only: FrameworkContentElement already owns metadata
        // for the same property and overriding it throws at startup.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(french));

        base.OnStartup(e);

        var settingsService = new SettingsService();
        var settings = settingsService.Load();

        ThemeService.Apply(settings.Theme);

        AppLogger.Log(settings.Provider == DatabaseProvider.SqlServer
            ? $"Démarrage avec SQL Server {settings.SqlServer.Host}/{settings.SqlServer.Database}"
            : "Démarrage avec SQLite (dev)");

        var activeFactory = new SwitchableConnectionFactory(BuildFactory(settings));
        var repository = new ProductionRepository(activeFactory, settings);
        var teamSchedule = new TeamScheduleService(settings);
        var sharedSettings = new SharedSettingsSync(new SharedSettingsRepository(activeFactory), settings, settingsService);
        var mainViewModel = new MainViewModel(repository, settings, teamSchedule, sharedSettings);

        var window = new MainWindow(mainViewModel, settingsService, settings, activeFactory, repository, sharedSettings);
        window.Show();

        // From here on the app runs 24/7 on the hall screen: an error in one
        // window (15/11/2026: a font WPF couldn't load while Planification
        // opened) is logged and the app carries on instead of closing. Before
        // this point an error still closes it: there would be no window to
        // show, and the watchdog task starts it again.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLogger.Log($"Erreur fatale, l'application se ferme : {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLogger.Log($"Erreur ignorée (tâche) : {e.Exception.GetBaseException()}");
            e.SetObserved();
        };
    }

    // Errors kept within the last minute: a window failing again and again
    // (every layout pass) means the app is stuck, it then starts afresh.
    private readonly Queue<DateTime> _recentErrors = new();
    private const int MaxErrorsPerMinute = 20;
    private bool _restarting;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var now = DateTime.Now;
        _recentErrors.Enqueue(now);
        while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > TimeSpan.FromMinutes(1))
            _recentErrors.Dequeue();

        // The full error the first times, then one line each.
        AppLogger.Log(_recentErrors.Count <= 3
            ? $"Erreur ignorée, l'application continue : {e.Exception}"
            : $"Erreur ignorée ({_recentErrors.Count} en 1 min) : {e.Exception.GetType().Name}: {e.Exception.Message}");

        if (_recentErrors.Count < MaxErrorsPerMinute || _restarting)
            return;
        _restarting = true;
        AppLogger.Log($"{MaxErrorsPerMinute} erreurs en 1 minute : l'application redémarre.");
        try
        {
            if (Environment.ProcessPath is { } exe)
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Redémarrage impossible : {ex.Message}");
        }
        Environment.Exit(1);
    }

    private static IDbConnectionFactory BuildFactory(AppSettings settings)
    {
        if (settings.Provider == DatabaseProvider.SqlServer)
            return new SqlServerConnectionFactory(settings.SqlServer);

        // A missing dev DB shouldn't crash startup: fall back to the expected path so
        // MainViewModel's own connection error handling (banner + Settings) takes over.
        string dbPath;
        try
        {
            dbPath = DatabasePathResolver.ResolveSqlitePath();
        }
        catch (FileNotFoundException)
        {
            dbPath = Path.Combine(AppContext.BaseDirectory, "spiga_dashboards.db");
        }
        return new SqliteConnectionFactory(dbPath);
    }
}
