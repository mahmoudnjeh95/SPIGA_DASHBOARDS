namespace SpigaDashboards.App.Models;

public class Machine
{
    public string MachineId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string MachineType { get; set; } = "";
    public string Sector { get; set; } = "";
    public string IPAddress { get; set; } = "";
    public bool IsActive { get; set; }

    // Machines.Sector holds "Pate courte" / "Pate longue" without the accent
    // (filters and slide keys compare on it); this is the text to show.
    public static string SectorLabel(string sector) => sector switch
    {
        "Pate courte" => "Pâte courte",
        "Pate longue" => "Pâte longue",
        "Pate" => "Pâte",
        _ => sector,
    };
}
