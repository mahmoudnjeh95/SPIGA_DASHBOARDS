using Spiga.Core.Plant;

namespace Spiga.Core.Staff;

// Two people of two teams swap their postes for one production day: A works
// B's poste (in B's team sheet, as a renfort) and B works A's. Each one's
// own sheet shows him "ailleurs" (permuté). Kept on its own, not in the
// sheets: the other poste is often later that day, its sheet not open yet;
// each sheet applies it when it opens.
public sealed class AttendancePermutation
{
    public long Id { get; set; }
    public DateTime ProductionDate { get; set; }

    public int EmployeeA { get; set; }
    public string NameA { get; set; } = "";
    public string TeamA { get; set; } = "";
    public int ShiftA { get; set; }

    public int EmployeeB { get; set; }
    public string NameB { get; set; } = "";
    public string TeamB { get; set; } = "";
    public int ShiftB { get; set; }

    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledAt { get; set; }

    public bool IsActive => CancelledAt == null;

    public bool Involves(int employeeId) => EmployeeA == employeeId || EmployeeB == employeeId;

    public bool Touches(DateTime date, int shiftId, string team)
        => ProductionDate.Date == date.Date
           && (ShiftA == shiftId && Same(TeamA, team) || ShiftB == shiftId && Same(TeamB, team));

    // Who leaves the poste (shiftId, team) for the other one, and who comes.
    public (int Out, string OutName, int In, string InName)? On(int shiftId, string team)
    {
        if (ShiftA == shiftId && Same(TeamA, team))
            return (EmployeeA, NameA, EmployeeB, NameB);
        if (ShiftB == shiftId && Same(TeamB, team))
            return (EmployeeB, NameB, EmployeeA, NameA);
        return null;
    }

    // The poste of the partner of `employeeId` (where he goes).
    public (int ShiftId, string Team) PosteOf(int employeeId) => employeeId == EmployeeA ? (ShiftB, TeamB) : (ShiftA, TeamA);

    public string PartnerName(int employeeId) => employeeId == EmployeeA ? NameB : NameA;

    public string Describe()
        => $"{NameA} ({TeamA}, {Shifts.Label(ShiftA)}) ⇄ {NameB} ({TeamB}, {Shifts.Label(ShiftB)}) le {ProductionDate:dd/MM/yyyy}";

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

// Stored in the settings (SettingKeys.PermutationRule).
public sealed class PermutationRule
{
    // Only two people of the same function swap (a conducteur with a conducteur).
    public bool SameFunction { get; set; } = true;
}

public interface IPermutationStore
{
    // Every permutation of production dates [fromDate, toDate], cancelled ones too.
    Task<IReadOnlyList<AttendancePermutation>> ListAsync(DateTime fromDate, DateTime toDate);
    Task<AttendancePermutation?> GetAsync(long id);
    Task<long> InsertAsync(AttendancePermutation permutation);
    Task<bool> CancelAsync(long id, string by, DateTime at);
}
