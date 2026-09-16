namespace SpigaDashboards.App.Models;

public class ProductionLot
{
    public int LotId { get; set; }
    public string MachineId { get; set; } = "";
    public string ProductName { get; set; } = "";
    public int LotNumber { get; set; }
    public int ShiftId { get; set; }
    public int EmployeeId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double TargetNetWeight { get; set; }
    public double NominalRatePerMinute { get; set; }
    public double MinWeight { get; set; }
    public double MaxWeight { get; set; }
    public int TotalBags { get; set; }
    public int BagsUnderTolerance { get; set; }
    public int BagsInTolerance { get; set; }
    public int BagsOverTolerance { get; set; }
    public int BagsIndeterminate { get; set; }
    public double StdDev { get; set; }
    public double AvgWeight { get; set; }
    public double CorrectedAvgWeight { get; set; }
    public int MetalRejects { get; set; }
    public bool IsConform { get; set; }

    public double DurationMinutes => (EndTime - StartTime).TotalMinutes;
}
