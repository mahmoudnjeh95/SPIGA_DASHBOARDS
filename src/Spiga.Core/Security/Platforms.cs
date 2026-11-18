namespace Spiga.Core.Security;

// One machine of a platform, with its sector (taken from the plant
// referential when the machine was added) so that a scope made of platforms
// also knows which sectors it opens.
public sealed record PlatformMachine(string MachineId, string Sector);

// A named group of machines, defined by the administrators (« Plateforme
// pâtes courtes ligne 1 », « Conditionnement couscous »...). A role is given
// over a team and one or more platforms: its holder sees, enters, validates...
// (whatever the role allows) on the machines of those platforms only.
public sealed class Platform
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    // An inactive platform opens nothing: the accounts given it lose its
    // machines at once, without being changed one by one.
    public bool IsActive { get; set; } = true;

    public List<PlatformMachine> Machines { get; set; } = new();

    // Role assignments that name this platform.
    public int AssignmentCount { get; set; }

    public bool Contains(string machineId) => Machines.Any(m => string.Equals(m.MachineId, machineId, StringComparison.OrdinalIgnoreCase));

    public string Describe()
        => $"{Name} — {Description} — {(IsActive ? "active" : "inactive")} — machines : "
           + (Machines.Count == 0 ? "aucune" : string.Join(", ", Machines.Select(m => m.MachineId).OrderBy(m => m, StringComparer.OrdinalIgnoreCase)));

    // Stands for a platform id that no longer exists: opens nothing, so a
    // scope never widens because its platform disappeared.
    public static Platform Missing(int id) => new() { Id = id, Name = $"Plateforme {id} (supprimée)", IsActive = false };

    public Platform Clone() => new()
    {
        Id = Id, Name = Name, Description = Description, IsActive = IsActive,
        Machines = Machines.ToList(), AssignmentCount = AssignmentCount,
    };
}

public interface IPlatformStore
{
    // With their machines and the number of assignments naming them.
    Task<IReadOnlyList<Platform>> ListAsync();
    Task<int> InsertAsync(Platform platform);
    Task UpdateAsync(Platform platform);
    Task DeleteAsync(int id);
}
