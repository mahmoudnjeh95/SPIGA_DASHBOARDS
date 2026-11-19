namespace Spiga.Core.Plant;

// The production database keys sectors without accents ("Pate courte");
// these are the keys everywhere, Label is what the screens show.
public static class Sectors
{
    public const string Couscous = "Couscous";
    public const string PateCourte = "Pate courte";
    public const string PateLongue = "Pate longue";

    public static readonly IReadOnlyList<string> All = new[] { Couscous, PateCourte, PateLongue };

    public static string Label(string sector) => sector switch
    {
        PateCourte => "Pâte courte",
        PateLongue => "Pâte longue",
        _ => sector,
    };
}

public static class Shifts
{
    public static readonly IReadOnlyList<int> All = new[] { 1, 2, 3 };

    public static string Label(int shiftId) => shiftId switch
    {
        1 => "Matin",
        2 => "Après-midi",
        3 => "Nuit",
        _ => $"Poste {shiftId}",
    };

    public static string Hours(int shiftId) => shiftId switch
    {
        1 => "06h - 14h",
        2 => "14h - 22h",
        _ => "22h - 06h",
    };

    public static int At(DateTime time) => time.Hour switch
    {
        < 6 => 3,
        < 14 => 1,
        < 22 => 2,
        _ => 3,
    };

    // When a poste of a production day starts (it lasts 8 hours).
    public static DateTime Start(DateTime productionDate, int shiftId)
        => productionDate.Date.AddHours(shiftId switch { 1 => 6, 2 => 14, _ => 22 });

    // The night poste 22h-06h belongs to the day it started.
    public static DateTime ProductionDate(DateTime time) => time.Hour < 6 ? time.Date.AddDays(-1) : time.Date;
}

public sealed record PlantMachine(string Id, string Name, string Sector);

// Read access to the plant's reference data (machines from the production
// database, teams from the roster). Implemented by the hosts.
public interface IPlantReferential
{
    Task<IReadOnlyList<PlantMachine>> GetMachinesAsync();
    IReadOnlyList<string> Teams { get; }
    string TeamOnShift(DateTime productionDate, int shiftId);
}
