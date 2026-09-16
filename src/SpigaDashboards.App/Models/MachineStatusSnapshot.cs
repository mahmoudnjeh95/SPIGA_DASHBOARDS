namespace SpigaDashboards.App.Models;

public class MachineStatusSnapshot
{
    public string MachineId { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public bool IsConnected { get; set; }
    public bool BeltRunning { get; set; }
    public bool AlarmActive { get; set; }
}
