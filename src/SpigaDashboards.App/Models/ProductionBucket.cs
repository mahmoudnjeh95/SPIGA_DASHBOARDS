namespace SpigaDashboards.App.Models;

// Production of one line, for ONE production (article run), over one time
// bucket (an hour, or a day for long synthesis ranges), aggregated on the
// server from Stiavelli's produzione snapshots (see ProductionRepository).
// Counters are what was produced inside the bucket, so buckets can be summed
// over any period whatever Stiavelli's snapshot cadence is (30 min or under
// a minute).
//
// A changeover inside an hour gives two buckets for that hour, one per
// production, each with its own target: weight figures and filters by weight
// never mix two articles. Anything per hour (on/off, speed charts) groups
// buckets by StartTime first.
public class ProductionBucket
{
    public string MachineId { get; set; } = "";
    public string ProductionId { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string? ProductName { get; set; }
    public double TargetNetWeight { get; set; }

    // produzione.turno is always 0: derived from StartTime by the repository.
    // Hour buckets never straddle a poste change (06h / 14h / 22h).
    public int ShiftId { get; set; }

    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
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

    // Stopped minutes inside a planned break (AppSettings.PlannedBreaks):
    // left out of the time the line was required to run.
    public double PlannedStopMinutes { get; set; }
    public double RequiredMinutes => ObservedMinutes - PlannedStopMinutes;

    // Fastest snapshot interval of the bucket (bags/min), 0 = none measured.
    public double MaxSpeedBagsPerMinute { get; set; }

    // Performance inputs, only when the production's nominal rate is known:
    // minutes the bags would have taken at the nominal rate, and the
    // producing minutes they actually took.
    public double IdealMinutes { get; set; }
    public double RatedRunMinutes { get; set; }

    // Weight data (net of the empty bag): Stiavelli lots (lotto, real lot
    // averages) when the line has lots in the range, otherwise Stiavelli's
    // running average when it looks sane (see ProductionRepository).
    public List<WeightPart> WeightParts { get; } = new();

    public long WeighedBags => WeightParts.Sum(p => p.Bags);
    public double WeighedWeightSum => WeightParts.Sum(p => p.WeightSum);
    public double WeighedTargetSum => WeightParts.Sum(p => p.Target * p.Bags);
    public double WeighedDeviationSum => WeighedWeightSum - WeighedTargetSum;

    public bool IsProducing => TotalBags > 0;
    public bool HasWeightData => WeighedBags > 0;
    public double? AvgWeight => HasWeightData ? WeighedWeightSum / WeighedBags : null;
    public double SpeedBagsPerMinute => RunMinutes > 0 ? TotalBags / RunMinutes : 0;
    public bool IsConform => TotalBags > 0 && (double)(BagsUnderTolerance + BagsOverTolerance) / TotalBags < 0.08;
}

// Bags weighed for one production in one bucket: WeightSum = average net weight x Bags.
public sealed record WeightPart(string ProductionId, double Target, long Bags, double WeightSum);
