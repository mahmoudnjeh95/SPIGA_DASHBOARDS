namespace Spiga.Core.Security;

// The usual verbs of a module. Each maps to the permission "<module>:<verb>"
// when the module offers it (PermissionCatalog); modules also declare their
// own specific permissions (corriger, débloquer...).
public enum AppAction
{
    Voir,
    Saisir,
    Valider,
    Exporter,
    Administrer,
}

public static class AppActions
{
    public static string Verb(AppAction action) => action switch
    {
        AppAction.Voir => "voir",
        AppAction.Saisir => "saisir",
        AppAction.Valider => "valider",
        AppAction.Exporter => "exporter",
        _ => "administrer",
    };

    public static string Label(AppAction action) => action switch
    {
        AppAction.Voir => "Voir",
        AppAction.Saisir => "Saisir",
        AppAction.Valider => "Valider",
        AppAction.Exporter => "Exporter",
        _ => "Administrer",
    };
}
