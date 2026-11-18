using System.Data;
using Dapper;

namespace SpigaDashboards.App.Data;

// Who changes something from the dashboard or its Paramètres: the signed-in
// account of SPIGA Suivi Production (or the one that signed in to open the
// kiosk's Paramètres), and the PC. Written with the change and in the audit
// trail.
public sealed record ChangeAuthor(string Login, string Name, string Workstation)
{
    // A kiosk / TV PC, no account signed in.
    public static ChangeAuthor Kiosk { get; } = new("kiosque", "Écran (sans connexion)", Environment.MachineName);

    public static ChangeAuthor Account(string login, string name) => new(login, name, Environment.MachineName);
}

// Rows of the application database's audit trail (AuditLog, Spiga.Data),
// written in the same transaction as the change they record.
internal static class AuditRows
{
    // Same names as Spiga.Core's Modules / AuditActions, shown by the journal.
    public const string ModuleConditionnement = "conditionnement";
    public const string ModuleParametres = "parametres";
    public const string ModuleMaintenance = "maintenance";
    public const string NoteUpdated = "Note du tableau de bord modifiée";
    public const string MachineRenamed = "Machine renommée";

    public static Task WriteAsync(IDbConnection connection, IDbTransaction transaction, ChangeAuthor author,
        string module, string action, string target, string? oldValue, string? newValue)
        => connection.ExecuteAsync(
            @"INSERT INTO AuditLog (At, UserLogin, Module, Action, Target, Details, OldValue, NewValue, Workstation)
              VALUES (@at, @login, @module, @action, @target, @details, @oldValue, @newValue, @workstation)",
            new
            {
                at = DateTime.Now, login = author.Login, module, action, target,
                details = author.Name, oldValue, newValue, workstation = author.Workstation,
            },
            transaction);
}
