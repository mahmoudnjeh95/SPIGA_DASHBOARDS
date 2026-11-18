using System.IO;
using Spiga.Data;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App;

// Where the application database (spiga_suivi) is, from the station's
// settings. SQL Server on site: same server and credentials as the
// production database, database AppSettings.AppDatabaseName (created by IT,
// see database\spiga_suivi.sql). Dev PC: a SQLite file beside the dev
// production database.
public static class AppDatabase
{
    public static AppDbOptions OptionsFor(AppSettings settings)
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
}
