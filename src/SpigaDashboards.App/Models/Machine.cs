namespace SpigaDashboards.App.Models;

public class Machine
{
    public string MachineId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string MachineType { get; set; } = "";
    public string Sector { get; set; } = "";
    public string IPAddress { get; set; } = "";
    public bool IsActive { get; set; }
}
