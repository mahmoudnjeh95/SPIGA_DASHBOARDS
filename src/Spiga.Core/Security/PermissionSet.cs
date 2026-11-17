namespace Spiga.Core.Security;

// The permissions of a role. Granting a module's permission grants its
// "voir"; withdrawing "voir" withdraws the whole module. Unknown keys (a
// permission removed from the catalogue) are ignored.
public sealed class PermissionSet
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public PermissionSet() { }

    public PermissionSet(IEnumerable<string> keys)
    {
        foreach (var key in keys)
            Set(key, true);
    }

    public IReadOnlyCollection<string> Keys => _keys;

    public int Count => _keys.Count;

    public bool Has(string key) => _keys.Contains(key);

    public bool Can(string module, AppAction action) => PermissionCatalog.Key(module, action) is { } key && Has(key);

    public bool CanSeeAny(IEnumerable<string> modules) => modules.Any(m => Has(Perm.View(m)));

    public bool HasModule(string module) => Has(Perm.View(module));

    public void Set(string key, bool on)
    {
        var def = PermissionCatalog.Find(key);
        if (def == null)
            return;
        if (on)
        {
            _keys.Add(key);
            _keys.Add(Perm.View(def.Module));
        }
        else if (def.IsView)
        {
            _keys.RemoveWhere(k => PermissionCatalog.Find(k)?.Module == def.Module);
        }
        else
        {
            _keys.Remove(key);
        }
    }

    public PermissionSet With(string key, bool on = true)
    {
        var copy = Clone();
        copy.Set(key, on);
        return copy;
    }

    public PermissionSet Clone() => new(_keys);

    public static PermissionSet Full() => new(PermissionCatalog.All.Select(p => p.Key));

    public static PermissionSet Union(IEnumerable<PermissionSet> sets) => new(sets.SelectMany(s => s.Keys));

    public bool SameAs(PermissionSet other) => _keys.SetEquals(other._keys);

    // "+ Module › right" / "- Module › right", one line per change, catalogue order.
    public static string Diff(PermissionSet before, PermissionSet after)
        => string.Join("\n", PermissionCatalog.All
            .Where(p => before.Has(p.Key) != after.Has(p.Key))
            .Select(p => (after.Has(p.Key) ? "+ " : "- ") + PermissionCatalog.Label(p.Key)));

    public string Describe()
        => string.Join("\n", ModuleCatalog.All
            .Where(m => HasModule(m.Key))
            .Select(m => m.Label + " : " + string.Join(", ", PermissionCatalog.ForModule(m.Key).Where(p => Has(p.Key)).Select(p => p.Label))));
}
