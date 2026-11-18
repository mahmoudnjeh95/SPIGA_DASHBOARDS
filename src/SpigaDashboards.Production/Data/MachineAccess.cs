using SpigaDashboards.App.Models;
using Dapper;

namespace SpigaDashboards.App.Data;

// The machines the signed-in account may see and change in the WPF pages of
// SPIGA Suivi Production (the scope of the right the page needs: its team
// and platforms). Enforced by the repositories themselves, so no screen can
// list, read or write a machine outside it, whatever it asks for.
//
// Everything: kiosk / TV screen, which show the station's machines with no
// sign-in. Nothing: a page created before the account is known.
public sealed class MachineAccess
{
    private Func<string, string, bool>? _rule;
    private Dictionary<string, string>? _sectors;

    private MachineAccess(string key, Func<string, string, bool>? rule)
    {
        Key = key;
        _rule = rule;
    }

    public static MachineAccess Everything() => new("*", null);

    public static MachineAccess Nothing() => new("-", (_, _) => false);

    // Changes whenever the allowed set may have changed.
    public string Key { get; private set; }

    public bool IsRestricted => _rule != null;

    // Raised (on the caller's thread) when Set changed the scope: pages
    // holding data reload.
    public event Action? Changed;

    // rule: (machine id, sector) => allowed; null = every machine.
    public void Set(string key, Func<string, string, bool>? rule)
    {
        if (key == Key)
            return;
        Key = key;
        _rule = rule;
        Changed?.Invoke();
    }

    public bool Allows(string machineId, string? sector)
        => _rule == null || (sector != null && _rule(machineId, sector));

    // Keeps the machines' sectors, read with the machine list.
    internal void Remember(IEnumerable<Machine> machines)
        => _sectors = machines.ToDictionary(m => m.MachineId, m => m.Sector, StringComparer.OrdinalIgnoreCase);

    // id => allowed, for queries that only carry the machine id. A machine
    // unknown to the Machines table is refused.
    internal async Task<Func<string, bool>> FilterAsync(IDbConnectionFactory factory)
    {
        if (_rule == null)
            return _ => true;
        if (_sectors == null)
        {
            using var connection = factory.CreateConnection();
            Remember(await connection.QueryAsync<Machine>("SELECT MachineId, Sector FROM Machines"));
        }
        var sectors = _sectors!;
        return id => id != null && sectors.TryGetValue(id, out var sector) && Allows(id, sector);
    }

    internal async Task DemandAsync(IDbConnectionFactory factory, string machineId)
    {
        if (!(await FilterAsync(factory))(machineId))
            throw new UnauthorizedAccessException($"La machine {machineId} est hors de votre périmètre (équipe et plateformes) : opération refusée.");
    }
}
