using System.Data;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Data;

public interface IDbConnectionFactory
{
    // Picks the SQL dialect for the few date expressions that differ.
    DatabaseProvider Provider { get; }

    IDbConnection CreateConnection();

    // Discards pooled connections (no-op for providers without pooling).
    void ResetPool() { }
}
