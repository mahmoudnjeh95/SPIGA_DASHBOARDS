using System.Data;
using Microsoft.Data.SqlClient;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.Data;

public class SqlServerConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public SqlServerConnectionFactory(SqlServerSettings settings)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = settings.Host,
            InitialCatalog = settings.Database,
            // 10 s: a remote named instance (HOST\SQLEXPRESS) goes through SQL Browser first.
            ConnectTimeout = 10,
            // Year-long sector aggregations over sub-minute snapshots can take a
            // while on SQLEXPRESS; give them room beyond the 30 s default.
            CommandTimeout = 60,
            ApplicationName = "SpigaDashboards",
            TrustServerCertificate = true,
        };

        if (settings.AuthMode == SqlAuthMode.Windows)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = settings.Username;
            builder.Password = PasswordProtector.Unprotect(settings.EncryptedPassword);
        }

        _connectionString = builder.ConnectionString;
    }

    // Same server and credentials, another database (SPIGA Suivi Production
    // keeps its own tables in AppSettings.AppDatabaseName).
    public string ConnectionStringFor(string database)
        => new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = database, ApplicationName = "SpigaSuivi" }.ConnectionString;

    public DatabaseProvider Provider => DatabaseProvider.SqlServer;

    public IDbConnection CreateConnection() => new SqlConnection(_connectionString);

    public void ResetPool() => SqlConnection.ClearAllPools();
}
