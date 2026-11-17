namespace Spiga.Core.Security;

// The part of the plant a user works on. Empty lists = no restriction.
// A chef d'équipe gets Team; an opérateur gets Sectors and/or Machines.
public sealed record DataScope(IReadOnlyList<string> Sectors, string? Team, IReadOnlyList<string> Machines)
{
    public static readonly DateTime Forever = new(9999, 12, 31);

    public static DataScope Everything { get; } = new(Array.Empty<string>(), null, Array.Empty<string>());

    public bool IsUnrestricted => Sectors.Count == 0 && string.IsNullOrEmpty(Team) && Machines.Count == 0;

    public bool AllowsSector(string sector)
        => Sectors.Count == 0 || Sectors.Contains(sector, StringComparer.OrdinalIgnoreCase);

    public bool AllowsMachine(string machineId, string sector)
        => AllowsSector(sector) && (Machines.Count == 0 || Machines.Contains(machineId, StringComparer.OrdinalIgnoreCase));

    public bool AllowsTeam(string? team)
        => string.IsNullOrEmpty(Team) || string.Equals(Team, team, StringComparison.OrdinalIgnoreCase);

    public string Describe()
    {
        if (IsUnrestricted)
            return "Toute l'usine";
        var parts = new List<string>();
        if (Sectors.Count > 0)
            parts.Add(string.Join(", ", Sectors.Select(Plant.Sectors.Label)));
        if (Machines.Count > 0)
            parts.Add(string.Join(", ", Machines));
        if (!string.IsNullOrEmpty(Team))
            parts.Add(Team);
        return string.Join(" · ", parts);
    }

    public static string ToCsv(IEnumerable<string> values) => string.Join(",", values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));

    public static IReadOnlyList<string> FromCsv(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
