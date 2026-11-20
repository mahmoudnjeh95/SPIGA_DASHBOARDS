using Spiga.Core.Plant;

namespace Spiga.Core.Staff;

// The codes of the paper sheet.
public enum AbsenceCode
{
    A,
    M,
    AT,
    C,
}

public static class AbsenceCodes
{
    public static readonly IReadOnlyList<AbsenceCode> All = Enum.GetValues<AbsenceCode>();

    public static string Label(AbsenceCode code) => code switch
    {
        AbsenceCode.A => "Absent",
        AbsenceCode.M => "Maladie",
        AbsenceCode.AT => "Accident de travail",
        _ => "Congé",
    };
}

// AConfirmer: prefilled with the poste's hours, not checked by the chef yet.
// Present: he confirmed or changed the hours. Absent: with a code.
// Ailleurs: the employee is pointed in another team's sheet (renfort) at
// these hours; shown read only, filled from that sheet.
public enum LineStatus
{
    AConfirmer,
    Present,
    Absent,
    Ailleurs,
}

public enum SheetStatus
{
    Brouillon,
    Validee,
}

// One row of a pointage sheet. Who, function and team are copied from the
// employee's posting when the row is made: the sheet keeps what was true
// that day, whatever changes later.
public sealed class AttendanceLine
{
    public long Id { get; set; }
    public long SheetId { get; set; }
    public int EmployeeId { get; set; }
    public string FullName { get; set; } = "";
    public StaffFunction Function { get; set; }
    public string HomeTeam { get; set; } = "";
    public bool IsRenfort { get; set; }

    public LineStatus Status { get; set; }
    public AbsenceCode? Absence { get; set; }
    public DateTime? EntryAt { get; set; }
    public DateTime? ExitAt { get; set; }

    // The employee's own post that poste.
    public string? MachineId { get; set; }
    public int? LineId { get; set; }

    // Temporary assignment: from ActingFrom the employee holds the post of
    // ReplacesEmployeeId (absent, or moved to another post himself), with
    // that person's function, machine or ligne.
    public int? ReplacesEmployeeId { get; set; }
    public StaffFunction? ActingFunction { get; set; }
    public string? ActingMachineId { get; set; }
    public int? ActingLineId { get; set; }
    public DateTime? ActingFrom { get; set; }

    public bool IsActing => ReplacesEmployeeId != null;

    // Holds a higher post than his own (the polyvalence record).
    public bool IsStepUp => ActingFunction is { } f && StaffFunctions.Rank(f) > StaffFunctions.Rank(Function);

    public AttendanceLine Clone() => (AttendanceLine)MemberwiseClone();

    public string Describe() => Status switch
    {
        LineStatus.Present => $"présent {EntryAt:HH:mm}–{ExitAt:HH:mm}"
                              + (IsActing ? $", remplace sur {ActingMachineId ?? "ligne"} dès {ActingFrom:HH:mm}" : ""),
        LineStatus.Absent => $"{Absence} ({(Absence is { } a ? AbsenceCodes.Label(a) : "")})",
        LineStatus.Ailleurs => "en renfort dans une autre équipe",
        _ => "à confirmer",
    };
}

// The pointage of one team on one poste.
public sealed class AttendanceSheet
{
    public long Id { get; set; }
    public DateTime ProductionDate { get; set; }
    public int ShiftId { get; set; }
    public string Team { get; set; } = "";
    public SheetStatus Status { get; set; }
    public int Version { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? ValidatedBy { get; set; }
    public DateTime? ValidatedAt { get; set; }

    public List<AttendanceLine> Lines { get; set; } = new();

    public bool IsNew => Id == 0;
    public DateTime Start => Shifts.Start(ProductionDate, ShiftId);
    public DateTime End => Start.AddHours(8);

    public string Poste => $"{Shifts.Label(ShiftId)} du {ProductionDate:dd/MM/yyyy} · {Team}";

    public AttendanceSheet Clone()
    {
        var copy = (AttendanceSheet)MemberwiseClone();
        copy.Lines = Lines.Select(l => l.Clone()).ToList();
        return copy;
    }

    public string Summary()
    {
        var present = Lines.Count(l => l.Status == LineStatus.Present);
        var absent = Lines.Count(l => l.Status == LineStatus.Absent);
        var renfort = Lines.Count(l => l.IsRenfort);
        var pending = Lines.Count(l => l.Status == LineStatus.AConfirmer);
        return $"{present} présent(s), {absent} absent(s)"
               + (renfort > 0 ? $", {renfort} renfort(s)" : "")
               + (pending > 0 ? $", {pending} à confirmer" : "");
    }
}

// One state of a sheet, written by every save: who, when, what changed.
public sealed class AttendanceRevision
{
    public long Id { get; set; }
    public long SheetId { get; set; }
    public int Version { get; set; }
    public DateTime At { get; set; }
    public string UserLogin { get; set; } = "";
    public string Action { get; set; } = "";
    public string Changes { get; set; } = "";
    public string Note { get; set; } = "";
}

public interface IAttendanceStore
{
    // Sheets of production dates [fromDate, toDate], with their lines.
    Task<IReadOnlyList<AttendanceSheet>> ListAsync(DateTime fromDate, DateTime toDate);
    Task<AttendanceSheet?> FindAsync(DateTime productionDate, int shiftId, string team);
    Task<AttendanceSheet?> GetAsync(long id);
    Task<long> InsertAsync(AttendanceSheet sheet, AttendanceRevision revision);

    // Saves the sheet and its lines (its Version already raised) if the
    // stored version is still expectedVersion. False = changed in between.
    Task<bool> UpdateAsync(AttendanceSheet sheet, int expectedVersion, AttendanceRevision revision);
    Task<IReadOnlyList<AttendanceRevision>> ListRevisionsAsync(long sheetId);
}
