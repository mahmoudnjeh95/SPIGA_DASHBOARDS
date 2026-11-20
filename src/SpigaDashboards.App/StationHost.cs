using System.IO;
using System.Windows;
using Microsoft.Win32;
using Spiga.UI.Services;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App;

// The part of Paramètres (Razor page, StationSettingsEditor) that only this
// PC has: its database connection, the slide document on its disk, its log
// console. What SettingsViewModel did for the WPF window.
internal sealed class StationHost(
    AppSettings settings, SwitchableConnectionFactory production, SwitchableConnectionFactory appData,
    SharedSettingsSync sharedSettings, Action applied) : IStationHost
{
    public async Task<(bool Success, string Message)> TestAsync(DatabaseProvider provider, SqlServerSettings sql)
    {
        AppLogger.Log(provider == DatabaseProvider.SqlServer
            ? $"Cible : SQL Server {sql.Host}/{sql.Database} (auth {sql.AuthMode})"
            : "Cible : SQLite (dev)");
        return await Task.Run(() => ConnectionTester.TestAsync(Factory(provider, sql)));
    }

    // settings now holds the connection chosen in the page: both databases
    // follow it; the application database is brought to this version (and
    // what older versions wrote in stats copied) before the shared settings
    // are sent there.
    public async Task SwitchAsync()
    {
        production.SwitchTo(Factory(settings.Provider, settings.SqlServer));
        var appDb = AppDatabase.OptionsFor(settings);
        appData.SwitchTo(AppData.ConnectionFor(appDb));
        try
        {
            await Task.Run(async () =>
            {
                await new Spiga.Data.Migrator(new Spiga.Data.AppDb(appDb)).MigrateAsync();
                await AppData.ImportLegacyAsync(production, appDb);
            });
        }
        catch (Exception ex)
        {
            AppLogger.Log("Base applicative injoignable après changement de connexion : " + ex);
        }
        sharedSettings.Reset();
    }

    public Task<string?> BrowsePdfAsync()
        => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFileDialog { Filter = "Documents PDF (*.pdf)|*.pdf", Title = "Choisir le document externe" };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }).Task;

    public string PdfStatus(string path)
        => string.IsNullOrWhiteSpace(path) ? "Aucun document configuré : la diapositive document sera ignorée."
            : !File.Exists(path) ? "Fichier introuvable à ce chemin."
            : $"Mis à jour le {File.GetLastWriteTime(path):dd/MM/yyyy HH:mm}.";

    public void OpenConsole() => Application.Current.Dispatcher.BeginInvoke(AppLogger.OpenConsole);

    public void Applied()
    {
        AppLogger.Log("Paramètres de connexion enregistrés et appliqués.");
        applied();
    }

    private static IDbConnectionFactory Factory(DatabaseProvider provider, SqlServerSettings sql)
        => provider == DatabaseProvider.Sqlite
            ? new SqliteConnectionFactory(DatabasePathResolver.ResolveSqlitePath())
            : new SqlServerConnectionFactory(sql);
}
