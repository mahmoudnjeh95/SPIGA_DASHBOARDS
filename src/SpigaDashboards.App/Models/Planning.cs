namespace SpigaDashboards.App.Models;

// Planification window (pannes, statistics, planning board). These records
// live in the app's own Plan* tables (PlanningRepository), never in the
// Stiavelli tables.

public enum CauseFamily
{
    Panne,
    Process,
    Organisation,
    Planifie,
}

public sealed record StopCause(string Code, string Label, CauseFamily Family);

// The causes a stop can be given, grouped by family. Codes are stored; labels
// can be reworded freely.
public static class StopCauses
{
    public static readonly IReadOnlyList<StopCause> All = new StopCause[]
    {
        new("MECA", "Panne mécanique", CauseFamily.Panne),
        new("ELEC", "Panne électrique", CauseFamily.Panne),
        new("PNEU", "Panne pneumatique", CauseFamily.Panne),
        new("AUTO", "Automate / capteur", CauseFamily.Panne),
        new("FORMAT", "Changement de format", CauseFamily.Process),
        new("REGLAGE", "Réglage", CauseFamily.Process),
        new("MATIERE", "Manque matière", CauseFamily.Process),
        new("EMBALL", "Manque film / carton", CauseFamily.Process),
        new("QUALITE", "Contrôle qualité", CauseFamily.Process),
        new("NETT", "Nettoyage", CauseFamily.Organisation),
        new("OPERATEUR", "Absence opérateur", CauseFamily.Organisation),
        new("PAUSE", "Pause / réunion", CauseFamily.Organisation),
        new("NODATA", "Pas de données", CauseFamily.Organisation),
        new("PREV", "Maintenance préventive", CauseFamily.Planifie),
        new("NOPLAN", "Pas de production prévue", CauseFamily.Planifie),
    };

    private static readonly Dictionary<string, StopCause> ByCode = All.ToDictionary(c => c.Code);

    public static StopCause? Find(string? code) => code != null && ByCode.TryGetValue(code, out var c) ? c : null;

    public static string Label(string? code) => Find(code)?.Label ?? code ?? "";

    public static string FamilyLabel(CauseFamily family) => family switch
    {
        CauseFamily.Panne => "Panne",
        CauseFamily.Process => "Process",
        CauseFamily.Organisation => "Organisation",
        _ => "Planifié",
    };

    // Suggested components (the list also learns what was typed before).
    public static readonly IReadOnlyList<string> DefaultComponents = new[]
    {
        "Mâchoires de soudure", "Doseur / peseuse", "Bobine de film", "Capteur", "Convoyeur",
        "Tapis", "Vérin", "Moteur", "Automate", "Chaîne de transfert", "Autre",
    };
}

// The cause given to one stop: a stop detected by StopDetector (matched on
// machine + start) or a panne declared by hand (IsManual).
public class StopQualification
{
    public int Id { get; set; }
    public string MachineId { get; set; } = "";
    public DateTime StopStart { get; set; }
    public DateTime StopEnd { get; set; }
    public DateTime ShiftDate { get; set; }
    public int ShiftId { get; set; }
    public string Team { get; set; } = "";
    public string CauseCode { get; set; } = "";
    public string? Component { get; set; }
    public string? Technician { get; set; }
    public string? Comment { get; set; }
    public bool IsManual { get; set; }
    public DateTime CreatedAt { get; set; }

    public double Minutes => Math.Max(0, (StopEnd - StopStart).TotalMinutes);
    public StopCause? Cause => StopCauses.Find(CauseCode);
    public bool IsPanne => Cause?.Family == CauseFamily.Panne;
}

public enum MaintenanceKind
{
    Calendar,
    Usage,
}

// A preventive maintenance rule: every IntervalDays (Calendar) or every
// IntervalBags bags made by the line (Usage), counted from LastDoneAt.
public class MaintenancePlan
{
    public int Id { get; set; }
    public string MachineId { get; set; } = "";
    public string Task { get; set; } = "";
    public MaintenanceKind Kind { get; set; }
    public int IntervalDays { get; set; }
    public long IntervalBags { get; set; }
    public int DurationMinutes { get; set; } = 60;
    public DateTime? LastDoneAt { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum PlanStatus
{
    Planned,
    Done,
    Cancelled,
}

// A maintenance slot on the planning board (from a plan, or one-off).
public class MaintenanceEvent
{
    public int Id { get; set; }
    public int? PlanId { get; set; }
    public string MachineId { get; set; } = "";
    public string Task { get; set; } = "";
    public DateTime PlannedStart { get; set; }
    public int DurationMinutes { get; set; } = 60;
    public PlanStatus Status { get; set; }
    public DateTime? DoneAt { get; set; }
    public string? Technician { get; set; }
    public string? Comment { get; set; }

    public DateTime PlannedEnd => PlannedStart.AddMinutes(DurationMinutes);
}

// An ordre de fabrication: so many bags of a product on a line.
public class ProductionOrder
{
    public int Id { get; set; }
    public string OrderNo { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string Product { get; set; } = "";
    public double TargetWeightGrams { get; set; }
    public long QuantityBags { get; set; }
    public DateTime PlannedStart { get; set; }
    public DateTime PlannedEnd { get; set; }
    public PlanStatus Status { get; set; }
    public string? Comment { get; set; }
}

// Postes: Matin 06-14, Après-midi 14-22, Nuit 22-06 (dated by its 22h start).
public static class Postes
{
    public static (DateTime ShiftDate, int ShiftId, DateTime Start) Of(DateTime time)
    {
        var h = time.Hour;
        if (h >= 6 && h < 14)
            return (time.Date, 1, time.Date.AddHours(6));
        if (h >= 14 && h < 22)
            return (time.Date, 2, time.Date.AddHours(14));
        var date = h >= 22 ? time.Date : time.Date.AddDays(-1);
        return (date, 3, date.AddHours(22));
    }

    // Start of the next poste after `time` (or `time` itself on a boundary).
    public static DateTime NextStart(DateTime time)
    {
        var start = Of(time).Start;
        return start == time ? time : start.AddHours(8);
    }
}
