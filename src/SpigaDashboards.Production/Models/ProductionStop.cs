namespace SpigaDashboards.App.Models;

public enum LineState
{
    Producing = 0,
    Stopped = 1,
    NoData = 2,
    // Counter moved across a silence longer than MaxIntervalSeconds: bags
    // were made at some unknown time in it. Neither a stop nor a sign the
    // line ran in a given poste.
    Unplaced = 3,
}

// A stretch of one line's timeline in one state (ProductionRepository.GetLineSegmentsAsync).
public class LineSegment
{
    public string MachineId { get; set; } = "";
    public LineState State { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
}

// One stop of a line within one poste (a stop across a poste change is
// listed in both postes), as detected by StopDetector.
public class ProductionStop
{
    public string MachineId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public DateTime ShiftDate { get; set; }
    public int ShiftId { get; set; }
    public string Team { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }

    // Start of the whole stop this piece belongs to (earlier than Start for
    // the part of a long stop falling in a later poste: "Suite 04/11 20:41"),
    // and whether the stop is still going on (End = latest snapshot).
    public DateTime RunStart { get; set; }
    public bool IsLive { get; set; }
    public bool IsContinuation => RunStart < Start.AddMinutes(-1);

    // Part of the stop inside planned breaks (not counted), and part with no
    // snapshot at all (StatsViewer not writing: cause unknown).
    public double BreakMinutes { get; set; }
    public double NoDataMinutes { get; set; }

    // Cause given in the Planification window (matched on machine + start),
    // null = not qualified yet. A panne declared there by hand has no
    // detected stop: it is listed from its qualification alone (IsManual).
    public StopQualification? Qualification { get; set; }

    public double TotalMinutes => (End - Start).TotalMinutes;
    public double NetMinutes => Math.Max(0, TotalMinutes - BreakMinutes);
    public bool IsMostlyNoData => NoDataMinutes >= NetMinutes / 2;
    public bool IsManual => Qualification?.IsManual == true;

    public string ShiftName => Services.KpiCalculator.ShiftName(ShiftId);
    // "Sans données" first: a poste with no reading at all is that, even
    // when the silence began in an earlier poste.
    public string TypeLabel => IsManual ? "Saisie manuelle"
        : IsMostlyNoData ? "Sans données"
        : IsContinuation ? $"Suite {RunStart:dd/MM HH:mm}" : "Arrêt";
    public string EndText => IsLive ? "en cours" : End.ToString("HH:mm");
    public CauseFamily? CauseFamily => Qualification?.Cause?.Family;
    public string CauseLabel => Qualification == null ? "—" : StopCauses.Label(Qualification.CauseCode);
    // "Mâchoires de soudure · Karim · changé la résistance"
    public string DetailText => Qualification == null ? ""
        : string.Join(" · ", new[] { Qualification.Component, Qualification.Technician, Qualification.Comment }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
    public string DurationText => FormatDuration(NetMinutes);
    public string BreakText => BreakMinutes >= 0.5 ? FormatDuration(BreakMinutes) : "";

    // "7 min", "1 h 05"
    public static string FormatDuration(double minutes)
    {
        var total = (int)Math.Round(minutes);
        return total < 60 ? $"{total} min" : $"{total / 60} h {total % 60:00}";
    }
}
