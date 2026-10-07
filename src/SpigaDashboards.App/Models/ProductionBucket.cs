namespace SpigaDashboards.App.Models;

// Production of one line over one time bucket (an hour, or a day for the
// annual slides), aggregated on the server from Stiavelli's produzione
// snapshots (see ProductionRepository). Counters are what was produced inside
// the bucket, so buckets can be summed over any period whatever Stiavelli's
// snapshot cadence is (30 min or under a minute).
public class ProductionBucket
{
    public string MachineId { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string? ProductName { get; set; }
    public double NominalRatePerMinute { get; set; }

    // produzione.turno is always 0: derived from StartTime by the repository.
    // Hour buckets never straddle a poste change (06h / 14h / 22h).
    public int ShiftId { get; set; }

    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double TargetNetWeight { get; set; }
    public long TotalBags { get; set; }
    public long BagsUnderTolerance { get; set; }
    public long BagsInTolerance { get; set; }
    public long BagsOverTolerance { get; set; }
    public long BagsIndeterminate { get; set; }
    public long MetalRejects { get; set; }

    // Minutes covered by snapshot intervals in this bucket, and the part of
    // them in which the line produced bags (Disponibilite / speed inputs).
    public double ObservedMinutes { get; set; }
    public double RunMinutes { get; set; }

    // Bags whose Stiavelli running average looked sane (within 50 % of the
    // target -- CC01 reports e.g. 2.9 g on 1000 g), with the bags-weighted
    // sums of that average and of its gap to the target.
    public long WeighedBags { get; set; }
    public double WeighedWeightSum { get; set; }
    public double WeighedDeviationSum { get; set; }

    public bool IsProducing => TotalBags > 0;
    public bool HasWeightData => WeighedBags > 0;
    public double AvgWeight => HasWeightData ? WeighedWeightSum / WeighedBags : 0;
    public bool IsConform => TotalBags > 0 && (double)(BagsUnderTolerance + BagsOverTolerance) / TotalBags < 0.08;
}
