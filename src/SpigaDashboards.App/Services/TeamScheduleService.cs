using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Services;

// Which of the 4 rotating teams is on a given poste, as a pure function of
// (shift date, ShiftId) against the configurable roster in AppSettings.TeamRotation.
// Deliberately stateless and DB-free: team attribution for a past ProductionBucket
// (Stage 6 history) uses the exact same lookup as the live "who's on now"
// display, just fed that lot's own StartTime/ShiftId instead of DateTime.Now.
public class TeamScheduleService
{
    private readonly AppSettings _settings;

    public TeamScheduleService(AppSettings settings)
    {
        _settings = settings;
    }

    // Configured team names, in roster order (blank entries skipped).
    public IReadOnlyList<string> TeamNames
        => _settings.TeamRotation.TeamNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();

    // Nuit (22h-06h) spans midnight: a lot or "now" between 00:00 and 06:00
    // belongs to the Nuit poste that started the previous calendar day.
    public static DateTime GetShiftDate(DateTime timestamp, int shiftId)
        => shiftId == 3 && timestamp.Hour < 6 ? timestamp.Date.AddDays(-1) : timestamp.Date;

    public string GetCurrentTeam(DateTime now)
    {
        var shiftId = KpiCalculator.CurrentShiftId(now);
        return GetTeamForShift(GetShiftDate(now, shiftId), shiftId);
    }

    public string GetTeamForShift(DateTime shiftDate, int shiftId)
    {
        var rotation = _settings.TeamRotation;
        var cycleLength = rotation.Pattern.Count == 0 ? 0 : rotation.Pattern.Max(p => p.CycleDay) + 1;
        if (cycleLength <= 0)
            return "";

        var daysSinceStart = (int)(shiftDate.Date - rotation.RotationStartDate.Date).TotalDays;
        var cycleDay = ((daysSinceStart % cycleLength) + cycleLength) % cycleLength;

        var entry = rotation.Pattern.FirstOrDefault(p => p.CycleDay == cycleDay && p.ShiftId == shiftId);
        if (entry is null || entry.TeamIndex < 0 || entry.TeamIndex >= rotation.TeamNames.Length)
            return "";

        return rotation.TeamNames[entry.TeamIndex];
    }
}
