namespace SpigaDashboards.App.ViewModels;

// One history-browser row: a ProductionBucket (one machine, one hour) flattened for display, with Team
// computed via TeamScheduleService since it isn't stored on the lot itself.
public class HistoryRow
{
    public DateTime StartTime { get; init; }
    public string MachineId { get; init; } = "";
    public string ShiftName { get; init; } = "";
    public string Team { get; init; } = "";
    public string ProductName { get; init; } = "";
    public double TargetNetWeight { get; init; }

    // Null = no trustworthy weight data for that hour (left blank in the grid and exports).
    public double? AvgWeight { get; init; }
    public long TotalBags { get; init; }
    public long BagsInTolerance { get; init; }
    public long BagsOverTolerance { get; init; }
    public long BagsUnderTolerance { get; init; }
    public bool IsConform { get; init; }

    // Same wording as the Excel/PDF exports.
    public string ConformLabel => IsConform ? "Oui" : "Non";
}
