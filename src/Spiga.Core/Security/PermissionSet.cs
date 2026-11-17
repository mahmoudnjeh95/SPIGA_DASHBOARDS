namespace Spiga.Core.Security;

// Actions granted per module. Saisir / Valider / Exporter / Administrer
// always carry Voir, and actions a module does not offer are dropped, so a
// stored set can never hold an impossible right.
public sealed class PermissionSet
{
    private readonly Dictionary<string, AppAction> _grants = new(StringComparer.Ordinal);

    public PermissionSet() { }

    public PermissionSet(IEnumerable<KeyValuePair<string, AppAction>> grants)
    {
        foreach (var (module, actions) in grants)
            Set(module, actions);
    }

    public AppAction this[string module] => _grants.GetValueOrDefault(module);

    public IReadOnlyDictionary<string, AppAction> Grants => _grants;

    public bool Can(string module, AppAction action)
        => action != AppAction.None && (this[module] & action) == action;

    public bool CanSeeAny(IEnumerable<string> modules) => modules.Any(m => Can(m, AppAction.Voir));

    public void Set(string module, AppAction actions)
    {
        var info = ModuleCatalog.Find(module);
        if (info == null)
            return;
        actions &= info.Allowed;
        if (actions != AppAction.None)
            actions |= AppAction.Voir;
        if (actions == AppAction.None)
            _grants.Remove(module);
        else
            _grants[module] = actions;
    }

    public PermissionSet With(string module, AppAction actions)
    {
        var copy = Clone();
        copy.Set(module, actions);
        return copy;
    }

    public PermissionSet Clone() => new(_grants);

    public static PermissionSet Full()
        => new(ModuleCatalog.All.Select(m => new KeyValuePair<string, AppAction>(m.Key, m.Allowed)));

    // One line per changed module, for the audit trail.
    public static string Diff(PermissionSet before, PermissionSet after)
    {
        var lines = new List<string>();
        foreach (var module in ModuleCatalog.All)
        {
            var b = before[module.Key];
            var a = after[module.Key];
            if (a != b)
                lines.Add($"{module.Label} : {AppActions.Describe(b)} → {AppActions.Describe(a)}");
        }
        return string.Join("\n", lines);
    }
}
