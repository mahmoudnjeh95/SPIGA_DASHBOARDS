using Spiga.Core.Security;

namespace Spiga.Core.Audit;

// Reading the audit trail. It cannot be split by machine or team (accounts,
// rights, entries of every line), so it is for the accounts holding
// « Journal d'audit › Consulter » over the whole plant and every team —
// checked here, whatever the screen shows.
public sealed class AuditTrailService(IAuditLog log)
{
    public static string Right => Perm.View(Modules.Audit);

    public static bool CanRead(Actor? actor) => actor?.CanUnrestricted(Right) == true;

    public Task<IReadOnlyList<AuditEntry>> QueryAsync(Actor actor, AuditQuery query)
    {
        if (!CanRead(actor))
            throw new AccessDeniedException(
                $"Le journal d'audit demande le droit « {PermissionCatalog.Label(Right)} » sur toute l'usine et toutes les équipes.");
        return log.QueryAsync(query);
    }
}
