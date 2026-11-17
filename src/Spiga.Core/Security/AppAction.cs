namespace Spiga.Core.Security;

// What a role may do in a module. A role holds a combination per module
// (RolePermissions.Actions); Voir is implied by every other action.
[Flags]
public enum AppAction
{
    None = 0,
    Voir = 1,
    Saisir = 2,
    Valider = 4,
    Exporter = 8,
    Administrer = 16,
}

public static class AppActions
{
    public static readonly IReadOnlyList<AppAction> All =
        new[] { AppAction.Voir, AppAction.Saisir, AppAction.Valider, AppAction.Exporter, AppAction.Administrer };

    public static string Label(AppAction action) => action switch
    {
        AppAction.Voir => "Voir",
        AppAction.Saisir => "Saisir",
        AppAction.Valider => "Valider",
        AppAction.Exporter => "Exporter",
        AppAction.Administrer => "Administrer",
        _ => action.ToString(),
    };

    public static string Describe(AppAction actions)
    {
        if (actions == AppAction.None)
            return "Aucun accès";
        return string.Join(", ", All.Where(a => actions.HasFlag(a)).Select(Label));
    }
}
