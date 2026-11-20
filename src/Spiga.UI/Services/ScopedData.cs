using Spiga.Core.Security;
using Spiga.Data;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;

namespace Spiga.UI.Services;

// Repositories limited to the machines of the account's scope for one
// right (MachineAccess): nothing else can be listed, read or written
// through them. Used by the pages that read the production and planning data.
public static class ScopedData
{
    public static (ProductionRepository Production, PlanningRepository Planning) For(
        Actor actor, string permission, IDbConnectionFactory production, AppDbOptions appDb, AppSettings settings)
    {
        var rule = actor.MachineRuleFor(permission);
        var access = MachineAccess.Nothing();
        access.Set(rule?.Key ?? "*", rule is { } r ? r.Allows : null);
        var app = AppData.ConnectionFor(appDb);
        return (new ProductionRepository(production, app, settings, access), new PlanningRepository(app, production, access));
    }
}
