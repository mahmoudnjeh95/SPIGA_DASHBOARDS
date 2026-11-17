using System.IO;
using System.Windows;
using System.Windows.Markup;
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
        MainWindow CreateDashboard()
            => new(new MainViewModel(repository, settings, teamSchedule, sharedSettings),
                settingsService, settings, activeFactory, repository, sharedSettings);

        // TV / kiosk PCs: "SpigaDashboards.App.exe --kiosk" opens the
        // dashboard directly, without sign-in, as before.
        if (e.Args.Any(a => string.Equals(a, "--kiosk", StringComparison.OrdinalIgnoreCase)))
        {
            ShowWindow(CreateDashboard());
            return;
        }

        // The site's shared settings (nominal rates, TRS targets, teams), as
        // the dashboard pulls them, so both compute with the same values.
        Task.Run(() => sharedSettings.PullAsync()).GetAwaiter().GetResult();

        var appDb = AppDatabaseOptions(settings);
        try
        {
            // Off the UI thread: the awaits inside must not wait for it.
            Task.Run(() => new Migrator(new AppDb(appDb)).MigrateAsync()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppLogger.Log("Base applicative SPIGA Suivi Production injoignable : " + ex);
            MessageBox.Show(
                "La base de SPIGA Suivi Production est injoignable :\n" + ex.Message +
                "\n\nLe tableau de bord s'ouvre seul, sans connexion utilisateur.",
                "SPIGA Suivi Production", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowWindow(CreateDashboard());
            return;
        }

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddSpigaSuivi(appDb, activeFactory, settings);
        services.AddSingleton<IDesktopBridge>(new DesktopBridge(CreateDashboard));
        ShowWindow(new ShellWindow(services.BuildServiceProvider()));
    }

    private void ShowWindow(Window window)
    {
        MainWindow = window;
        window.Show();
    }

    // SQL Server on site: same server and credentials as the production
    // database, database AppSettings.AppDatabaseName (created by IT, see
    // database\spiga_suivi.sql). Dev PC: a SQLite file beside the settings.
    private static AppDbOptions AppDatabaseOptions(AppSettings settings)
    {
        if (settings.Provider == DatabaseProvider.SqlServer)
        {
            var factory = new SqlServerConnectionFactory(settings.SqlServer);
            return new AppDbOptions { Provider = AppDbProvider.SqlServer, ConnectionString = factory.ConnectionStringFor(settings.AppDatabaseName) };
        }
        string folder;
        try
        {
            folder = Path.GetDirectoryName(DatabasePathResolver.ResolveSqlitePath())!;
        }
        catch (FileNotFoundException)
        {
            folder = SettingsService.SettingsFolder;
        }
        return AppDbOptions.ForSqliteFile(Path.Combine(folder, "spiga_suivi.db"));
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
