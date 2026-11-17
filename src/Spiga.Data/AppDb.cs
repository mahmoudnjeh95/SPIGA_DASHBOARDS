using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace Spiga.Data;

public enum AppDbProvider
{
    Sqlite,
    SqlServer,
}

public sealed class AppDbOptions
{
    public AppDbProvider Provider { get; set; } = AppDbProvider.Sqlite;
    public string ConnectionString { get; set; } = "";

    public static AppDbOptions ForSqliteFile(string path)
        => new() { Provider = AppDbProvider.Sqlite, ConnectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString() };
}

// Opens connections to the application database and hides the few SQL
// differences between SQLite and SQL Server.
public sealed class AppDb(AppDbOptions options)
{
    public AppDbProvider Provider => options.Provider;

    public async Task<IDbConnection> OpenAsync()
    {
        if (options.Provider == AppDbProvider.SqlServer)
        {
            var sql = new SqlConnection(options.ConnectionString);
            await sql.OpenAsync();
            return sql;
        }

        var sqlite = new SqliteConnection(options.ConnectionString);
        await sqlite.OpenAsync();
        using var pragma = sqlite.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync();
        return sqlite;
    }

    // Appended to an INSERT to get the new row's id back.
    public string ReturnId => Provider == AppDbProvider.SqlServer
        ? "; SELECT CAST(SCOPE_IDENTITY() AS BIGINT);"
        : "; SELECT last_insert_rowid();";

    public string Key => Provider == AppDbProvider.SqlServer ? "[Key]" : "Key";
}
