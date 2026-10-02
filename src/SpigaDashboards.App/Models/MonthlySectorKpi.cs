namespace SpigaDashboards.App.Models;

// One row of a sector's annual synthesis table (spec slides 9-13): cumulative
// figures for one month, across every machine in the sector.
public class MonthlySectorKpi
{
    public int Month { get; set; }
    public string MonthName { get; set; } = "";
    public double AvgWeight { get; set; }
    public double WeightDeviationGrams { get; set; }
    public double TrsPercent { get; set; }
    public long BagsConform { get; set; }
    public long BagsOver { get; set; }
    public long BagsUnder { get; set; }
}
