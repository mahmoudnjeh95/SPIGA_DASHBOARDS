using System.Data.Common;
using SpigaDashboards.App.Data;

namespace SpigaDashboards.App.Services;

public static class ConnectionTester
{
    public static async Task<(bool Success, string Message)> TestAsync(IDbConnectionFactory factory)
    {
        AppLogger.Log("Test de connexion en cours...");
        try
        {
            using var connection = factory.CreateConnection();

            if (connection is DbConnection dbConnection)
                await dbConnection.OpenAsync();
            else
                connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.ExecuteScalar();

            AppLogger.Log("Connexion reussie.");
            return (true, "Connexion reussie.");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Echec de connexion : {ex.Message}");
            return (false, ex.Message);
        }
    }
}
