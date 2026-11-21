using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Spiga.Data;
using Spiga.UI.Services;

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
        _startArgs = e.Args;

        var settingsService = new SettingsService();
        var settings = settingsService.Load();

        ChartHelpers.ScreenTextColor = () => ThemeService.ChartTextColor;
        ThemeService.Apply(settings.Theme);

        AppLogger.Log(settings.Provider == DatabaseProvider.SqlServer
            ? $"Démarrage avec SQL Server {settings.SqlServer.Host}/{settings.SqlServer.Database}"
            : "Démarrage avec SQLite (dev)");

        // stats: the production database, only read. appData: the
        // application database (spiga_suivi), where the planning, the note,
        // the machine names and the shared settings are written. Paramètres
        // can switch both (SwitchableConnectionFactory).
        var activeFactory = new SwitchableConnectionFactory(BuildFactory(settings));
        var appData = new SwitchableConnectionFactory(AppData.ConnectionFor(AppDatabase.OptionsFor(settings)));
        var repository = new ProductionRepository(activeFactory, appData, settings);
        var teamSchedule = new TeamScheduleService(settings);
        var sharedSettings = new SharedSettingsSync(new SharedSettingsRepository(appData, activeFactory), settings, settingsService);
        // Kiosk / TV PCs: every machine of the station, no sign-in; their
        // Paramètres ask for an account (KioskSettingsGate).
        DashboardView CreateDashboard()
        {
            var dashboard = new DashboardView(new MainViewModel(repository, settings, teamSchedule, sharedSettings),
                settingsService, settings, activeFactory, appData, repository, sharedSettings);
            dashboard.SettingsGate = owner => KioskSignIn.ForSettingsAsync(owner, settings);
            dashboard.NoteGate = owner => KioskSignIn.ForNoteAsync(owner, settings);
            return dashboard;
        }

        // Signed-in shell: its own repository, showing no machine until the
        // account opening it gives its scope (DesktopBridge, MachineAccess).
        DashboardView CreateScopedDashboard()
        {
            var scoped = new ProductionRepository(activeFactory, appData, settings, MachineAccess.Nothing());
            return new(new MainViewModel(scoped, settings, teamSchedule, sharedSettings),
                settingsService, settings, activeFactory, appData, scoped, sharedSettings);
        }

        // IT: the table changes of this version, with an administrator login.
        if (e.Args.Contains("--update-database", StringComparer.OrdinalIgnoreCase))
        {
            // Its own windows come and go: the application ends when it is done.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.InvokeAsync(async () =>
            {
                await DatabaseUpdateTool.RunAsync(settings, activeFactory);
                Shutdown();
            });
            return;
        }

        // The application database first, in every mode: the dashboard reads
        // its planning, note and shared settings there. What older versions
        // wrote in stats is copied over once.
        var appDb = AppDatabase.OptionsFor(settings);
        Exception? appDbError = null;
        try
        {
            // Off the UI thread: the awaits inside must not wait for it.
            Task.Run(async () =>
            {
                await new Migrator(new AppDb(appDb)).MigrateAsync();
                await AppData.ImportLegacyAsync(activeFactory, appDb);
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            appDbError = ex;
            AppLogger.Log("Base applicative SPIGA Suivi Production injoignable : " + ex);
        }

        // TV / kiosk PCs: "SpigaDashboards.App.exe --kiosk", or a PC installed
        // as a screen (KIOSK=1, see StartMode), opens the dashboard directly,
        // without sign-in, as before.
        if (StartMode.IsKiosk(e.Args))
        {
            if (appDbError == null)
                Task.Run(() => sharedSettings.PullAsync()).GetAwaiter().GetResult();
            AppLogger.Log("Démarrage en mode écran (kiosque)");
            ShowWindow(new KioskWindow(CreateDashboard()));
            return;
        }

        // The site's shared settings (nominal rates, TRS targets, teams), as
        // the dashboard pulls them, so both compute with the same values.
        if (appDbError == null)
            Task.Run(() => sharedSettings.PullAsync()).GetAwaiter().GetResult();

        // The sign-in shell is drawn by Microsoft Edge WebView2 (present on
        // Windows 10 / 11). Missing: the dashboard alone, with the reason.
        if (StartMode.WebView2Version() is not { } webView2)
        {
            AppLogger.Log("WebView2 introuvable : démarrage du tableau de bord seul");
            MessageBox.Show(
                "Le composant Microsoft Edge WebView2 n'est pas installé sur ce poste : l'écran de connexion de " +
                "SPIGA Suivi Production ne peut pas s'afficher.\n\nInstallez « WebView2 Runtime » (Microsoft, gratuit), " +
                "puis relancez l'application. En attendant, le tableau de bord s'ouvre seul.",
                "SPIGA Suivi Production", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowWindow(new KioskWindow(CreateDashboard()));
            return;
        }
        AppLogger.Log($"WebView2 {webView2}");

        if (appDbError is { } appDbException)
        {
            // An update waiting for IT says what to do itself.
            var hint = appDbException is SchemaUpdateRequiredException || settings.Provider != DatabaseProvider.SqlServer
                ? ""
                : $"\n\nSur le serveur SQL, la base « {settings.AppDatabaseName} » doit exister et le compte des postes " +
                  "doit pouvoir y lire et écrire : script spiga_suivi.sql, dans le dossier d'installation, à exécuter une fois.";
            MessageBox.Show(
                "La base de SPIGA Suivi Production est injoignable :\n" + appDbException.Message + hint +
                "\n\nLe tableau de bord s'ouvre seul, sans connexion utilisateur.",
                "SPIGA Suivi Production", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowWindow(new KioskWindow(CreateDashboard()));
            return;
        }

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        // Paramètres (Razor) saves through the dashboard's own instances.
        services.AddSingleton(settingsService);
        services.AddSingleton(sharedSettings);
        services.AddSpigaSuivi(appDb, activeFactory, settings);
        var bridge = new DesktopBridge(CreateScopedDashboard, settings, activeFactory, appData, sharedSettings);
        services.AddSingleton<IDesktopBridge>(bridge);
        var shell = new ShellWindow(services.BuildServiceProvider());
        bridge.Attach(shell);
        ShowWindow(shell);
    }

    private void ShowWindow(Window window)
    {
        MainWindow = window;
        window.Show();
        UiFreezeWatchdog.Start(Dispatcher);

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

    // Given again on a restart: a TV PC started with --kiosk stays in kiosk.
    private string[] _startArgs = Array.Empty<string>();

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
                Process.Start(new ProcessStartInfo(exe, _startArgs) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
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
