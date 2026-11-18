namespace Spiga.Core.Audit;

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string UserLogin { get; set; } = "";
    public string Module { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Details { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string Workstation { get; set; } = "";
}

public sealed record AuditQuery(DateTime From, DateTime To, string? UserLogin = null, string? Module = null, int Limit = 500);

public interface IAuditLog
{
    Task WriteAsync(AuditEntry entry);
    Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query);
}

// Event names stored in AuditLog.Action, shown in the journal.
public static class AuditActions
{
    public const string LoginOk = "Connexion";
    public const string LoginFailed = "Échec de connexion";
    public const string LoginRefused = "Connexion refusée";
    public const string AccountLocked = "Compte bloqué";
    public const string Logout = "Déconnexion";
    public const string SessionLocked = "Session verrouillée";
    public const string PasswordChanged = "Mot de passe modifié";
    public const string PasswordReset = "Mot de passe réinitialisé";
    public const string AccountUnlocked = "Compte débloqué";
    public const string UserCreated = "Utilisateur créé";
    public const string UserUpdated = "Utilisateur modifié";
    public const string RoleCreated = "Rôle créé";
    public const string RoleUpdated = "Droits du rôle modifiés";
    public const string RoleRenamed = "Propriétés du rôle modifiées";
    public const string RoleDeleted = "Rôle supprimé";
    public const string PlatformCreated = "Plateforme créée";
    public const string PlatformUpdated = "Plateforme modifiée";
    public const string PlatformDeleted = "Plateforme supprimée";
    // Written by the dashboard (SpigaDashboards.Production, AuditRows).
    public const string NoteUpdated = "Note du tableau de bord modifiée";
    public const string MachineRenamed = "Machine renommée";
    public const string PolicyUpdated = "Règles de sécurité modifiées";
    public const string SetupDone = "Installation : premier administrateur";
    public const string EntryCreated = "Saisie créée";
    public const string EntryUpdated = "Saisie modifiée";
    public const string EntryValidated = "Saisie validée";
    public const string EntryCorrected = "Saisie validée corrigée";
    public const string EntryDeleted = "Saisie supprimée";
}
