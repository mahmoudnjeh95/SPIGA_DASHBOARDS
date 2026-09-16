using System.Data;

namespace SpigaDashboards.App.Data;

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}
