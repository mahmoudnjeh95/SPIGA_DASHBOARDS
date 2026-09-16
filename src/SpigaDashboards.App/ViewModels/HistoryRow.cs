namespace SpigaDashboards.App.ViewModels;

// One history-browser row: a ProductionLot flattened for display, with Team
// computed via TeamScheduleService since it isn't stored on the lot itself.
public class HistoryRow
{
    public DateTime StartTime { get; init; }
    public string MachineId { get; init; } = "";
    public string ShiftName { get; init; } = "";
    public string Team { get; init; } = "";
    public string ProductName { get; init; } = "";
    public double TargetNetWeight { get; init; }
    public double AvgWeight { get; init; }
    public int TotalBags { get; init; }
    public int BagsInTolerance { get; init; }
    public int BagsOverTolerance { get; init; }
    public int BagsUnderTolerance { get; init; }
    public bool IsConform { get; init; }
}
