using System.Data;
using Microsoft.Data.Sqlite;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Data;

public class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            // ReadWrite (not ReadOnly): the dashboard owns the sticky note and machine
            // display names, and writes those back. Production data (lots/status log)
            // is still only ever read, never written, by this app.
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString();
    }

    public DatabaseProvider Provider => DatabaseProvider.Sqlite;

    public IDbConnection CreateConnection() => new SqliteConnection(_connectionString);
}
