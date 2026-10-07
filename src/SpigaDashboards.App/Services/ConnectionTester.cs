using System.Data;
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

            Scalar(connection, "SELECT 1");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Echec de connexion : {ex.Message}");
            return (false, ex.Message);
        }

        // Login works: now check the dashboard objects exist in that database
        // (they're created by database/stiavelli_compat.sql, not by Stiavelli).
        try
        {
            using var connection = factory.CreateConnection();
            connection.Open();

            var machines = Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM Machines"));
            // Via the newest progressivo rather than MAX(tempo_fine), which would
            // scan millions of rows once Stiavelli snapshots every few seconds.
            var latest = Scalar(connection,
                "SELECT tempo_fine FROM produzione WHERE progressivo = (SELECT MAX(progressivo) FROM produzione)");
            Scalar(connection, "SELECT COUNT(*) FROM Products WHERE 1 = 0");

            var latestText = latest is null or DBNull
                ? "aucun releve dans produzione"
                : $"dernier releve Stiavelli : {Convert.ToDateTime(latest):dd/MM/yyyy HH:mm}";
            var message = $"Connexion reussie - {machines} machine(s), {latestText}.";
            AppLogger.Log(message);
            return (true, message);
        }
        catch (Exception ex)
        {
            var message = "Connexion reussie, mais la base n'est pas preparee pour le tableau de bord " +
                          $"(executez database/stiavelli_compat.sql sur le serveur) : {ex.Message}";
            AppLogger.Log(message);
            return (false, message);
        }
    }

    private static object? Scalar(IDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
