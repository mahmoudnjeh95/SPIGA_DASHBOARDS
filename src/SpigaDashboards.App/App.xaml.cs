using System.IO;
using System.Windows;
using System.Windows.Markup;
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
            ? $"Demarrage avec SQL Server {settings.SqlServer.Host}/{settings.SqlServer.Database}"
            : "Demarrage avec SQLite (dev)");

        var activeFactory = new SwitchableConnectionFactory(BuildFactory(settings));
        var repository = new ProductionRepository(activeFactory, settings);
        var teamSchedule = new TeamScheduleService(settings);
        var mainViewModel = new MainViewModel(repository, settings, teamSchedule);

        var license = new LicenseService();
        license.Start();

        var window = new MainWindow(mainViewModel, settingsService, settings, activeFactory, repository, license);
        window.Show();
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
