namespace Spiga.Core.Security;

// The part of the plant a user works on. Empty lists = no restriction.
// Usually a team and one or more platforms (named groups of machines,
// defined by the administrators); sectors and machines picked one by one
// remain for the scopes given before platforms existed. Every restriction
// given applies: a machine is open when it is in the sectors, in the
// machines and in one of the active platforms of the scope.
public sealed record DataScope(IReadOnlyList<string> Sectors, string? Team, IReadOnlyList<string> Machines)
{
    public DataScope(IReadOnlyList<string> sectors, string? team, IReadOnlyList<string> machines, IReadOnlyList<Platform> platforms)
        : this(sectors, team, machines)
    {
        Platforms = platforms;
    }

    public static readonly DateTime Forever = new(9999, 12, 31);

    public static DataScope Everything { get; } = new(Array.Empty<string>(), null, Array.Empty<string>());

    public static DataScope Of(string? team, IReadOnlyList<Platform> platforms)
        => new(Array.Empty<string>(), string.IsNullOrEmpty(team) ? null : team, Array.Empty<string>(), platforms);

    // The platforms of the scope, as loaded with it (an inactive or deleted
    // platform is kept here and opens nothing).
    public IReadOnlyList<Platform> Platforms { get; init; } = Array.Empty<Platform>();

    public bool IsUnrestricted => Sectors.Count == 0 && string.IsNullOrEmpty(Team) && Machines.Count == 0 && Platforms.Count == 0;

    // The machines the platforms open (active platforms only). Meaningful
    // only when the scope has platforms.
    public IReadOnlyList<PlatformMachine> PlatformMachines
        => Platforms.Where(p => p.IsActive).SelectMany(p => p.Machines)
            .DistinctBy(m => m.MachineId, StringComparer.OrdinalIgnoreCase).ToList();

    public bool AllowsSector(string sector)
        => (Sectors.Count == 0 || Sectors.Contains(sector, StringComparer.OrdinalIgnoreCase))
           && (Platforms.Count == 0 || PlatformMachines.Any(m => string.Equals(m.Sector, sector, StringComparison.OrdinalIgnoreCase)));

    public bool AllowsMachine(string machineId, string sector)
        => AllowsSector(sector)
           && (Machines.Count == 0 || Machines.Contains(machineId, StringComparer.OrdinalIgnoreCase))
           && (Platforms.Count == 0 || Platforms.Any(p => p.IsActive && p.Contains(machineId)));

    public bool AllowsTeam(string? team)
        => string.IsNullOrEmpty(Team) || string.Equals(Team, team, StringComparison.OrdinalIgnoreCase);

    // True when everything this scope allows, outer allows too (a delegated
    // administrator can only give access inside their own scope).
    public bool IsWithin(DataScope outer)
    {
        var mine = Platforms.Count > 0 ? PlatformMachines : null;
        if (outer.Sectors.Count > 0)
        {
            var bySectors = Sectors.Count > 0 && Sectors.All(s => outer.Sectors.Contains(s, StringComparer.OrdinalIgnoreCase));
            var byPlatforms = mine != null && mine.All(m => outer.Sectors.Contains(m.Sector, StringComparer.OrdinalIgnoreCase));
            if (!bySectors && !byPlatforms)
                return false;
        }
        if (!string.IsNullOrEmpty(outer.Team) && !string.Equals(Team, outer.Team, StringComparison.OrdinalIgnoreCase))
            return false;
        if (outer.Machines.Count > 0)
        {
            var byMachines = Machines.Count > 0 && Machines.All(m => outer.Machines.Contains(m, StringComparer.OrdinalIgnoreCase));
            var byPlatforms = mine != null && mine.All(m => outer.Machines.Contains(m.MachineId, StringComparer.OrdinalIgnoreCase));
            if (!byMachines && !byPlatforms)
                return false;
        }
        if (outer.Platforms.Count > 0)
        {
            var open = outer.PlatformMachines.Select(m => m.MachineId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var byMachines = Machines.Count > 0 && Machines.All(open.Contains);
            var byPlatforms = mine != null && mine.All(m => open.Contains(m.MachineId));
            if (!byMachines && !byPlatforms)
                return false;
        }
        return true;
    }

    public string Describe()
    {
        if (IsUnrestricted)
            return "Toute l'usine";
        var parts = new List<string>();
        if (Platforms.Count > 0)
            parts.Add(string.Join(", ", Platforms.Select(p => p.IsActive ? p.Name : p.Name + " (inactive)")));
        if (Sectors.Count > 0)
            parts.Add(string.Join(", ", Sectors.Select(Plant.Sectors.Label)));
        if (Machines.Count > 0)
            parts.Add(string.Join(", ", Machines));
        if (!string.IsNullOrEmpty(Team))
            parts.Add(Team);
        else if (Platforms.Count > 0 || Sectors.Count > 0 || Machines.Count > 0)
            parts.Add("toutes les équipes");
        return string.Join(" · ", parts);
    }

    public static string ToCsv(IEnumerable<string> values) => string.Join(",", values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));

    public static IReadOnlyList<string> FromCsv(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
