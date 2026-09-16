using System.Data;

namespace SpigaDashboards.App.Data;

// Lets Settings swap the live DB target (SQLite dev DB <-> SQL Server) without
// tearing down and rebuilding the repository/view-model graph.
public class SwitchableConnectionFactory : IDbConnectionFactory
{
    private IDbConnectionFactory _inner;

    public SwitchableConnectionFactory(IDbConnectionFactory inner)
    {
        _inner = inner;
    }

    public void SwitchTo(IDbConnectionFactory inner) => _inner = inner;

    public IDbConnection CreateConnection() => _inner.CreateConnection();
}
