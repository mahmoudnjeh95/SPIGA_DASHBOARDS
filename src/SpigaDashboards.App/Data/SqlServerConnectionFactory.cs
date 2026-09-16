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
            ConnectTimeout = 5,
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

    public IDbConnection CreateConnection() => new SqlConnection(_connectionString);
}
