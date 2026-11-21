using Spiga.Core.Security;
using SpigaDashboards.App.Models;

namespace Spiga.UI.Services;

// Who a hall screen is (kiosk PC, TV window): no account, the dashboard of
// the whole plant, its Historique and Arrêts, read and exported, nothing
// written. The note, Paramètres and the rest need a real sign-in
// ("Se connecter" on the kiosk).
public static class ScreenAccount
{
    public const string Login = "ecran";

    public static Actor Create(AppSettings settings)
    {
        var keys = new List<string>
        {
            Perm.TableauDetaille,
            Perm.EcranTv,
            Perm.View(Modules.Conditionnement),
            Perm.View(Modules.Maintenance),
        };
        foreach (var module in new[] { Modules.Conditionnement, Modules.Maintenance })
            if (PermissionCatalog.Key(module, AppAction.Exporter) is { } export)
                keys.Add(export);
        var role = new Role { Code = Login, Name = "Écran", Permissions = new PermissionSet(keys) };
        var user = new AppUser
        {
            Id = 0,
            Login = Login,
            DisplayName = "Écran du poste",
            Theme = settings.Theme == AppTheme.Dark ? "dark" : "light",
        };
        return new Actor(user, new[] { new Actor.Grant(role, new RoleAssignment { RoleName = role.Name, Scope = DataScope.Everything }) });
    }
}
