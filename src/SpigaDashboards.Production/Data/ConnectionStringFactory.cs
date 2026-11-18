using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Data;

// A connection from a plain connection string: the application database
// (spiga_suivi) as the hosts give it, or a production database configured
// by connection string (web host).
public sealed class ConnectionStringFactory(DatabaseProvider provider, string connectionString) : IDbConnectionFactory
{
    public DatabaseProvider Provider => provider;

    public string ConnectionString => connectionString;

    public IDbConnection CreateConnection()
        => provider == DatabaseProvider.SqlServer ? new SqlConnection(connectionString) : new SqliteConnection(connectionString);

    public void ResetPool()
    {
        if (provider == DatabaseProvider.SqlServer)
            SqlConnection.ClearAllPools();
        else
            SqliteConnection.ClearAllPools();
    }
}
