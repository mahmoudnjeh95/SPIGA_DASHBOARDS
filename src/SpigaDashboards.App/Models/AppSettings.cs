namespace SpigaDashboards.App.Models;

public enum DatabaseProvider
{
    Sqlite,
    SqlServer,
}

public enum SqlAuthMode
{
    Windows,
    SqlLogin,
}

public enum AppTheme
{
    Dark,
    Light,
}

public class SqlServerSettings
{
    public string Host { get; set; } = "";
    public string Database { get; set; } = "";
    public SqlAuthMode AuthMode { get; set; } = SqlAuthMode.Windows;
    public string Username { get; set; } = "";

    // DPAPI-protected (CurrentUser scope), base64. Never store the plain password on disk.
    public string EncryptedPassword { get; set; } = "";
}

// One roster row: on cycle day CycleDay, poste ShiftId (1=Matin/2=Soir/3=Nuit)
// is worked by TeamNames[TeamIndex]. A cycle day with no entry for a given
// ShiftId means no team is assigned (rest) for that poste that day.
public class TeamRosterEntry
{
    public int CycleDay { get; set; }
    public int ShiftId { get; set; }
    public int TeamIndex { get; set; }
}

public class TeamRotationSettings
{
    public string[] TeamNames { get; set; } = { "Equipe A", "Equipe B", "Equipe C", "Equipe D" };

    // Anchor date for CycleDay 0. Nudge this to realign the cycle once the
    // client's real roster is known.
    public DateTime RotationStartDate { get; set; } = new(2026, 1, 5);

    public List<TeamRosterEntry> Pattern { get; set; } = BuildDefaultPattern();

    // Placeholder 4-day cycle (no client roster yet): each team works 3
    // consecutive days -- a different poste each day (Matin, Soir, Nuit) --
    // then rests on the 4th, before starting the cycle again on Matin.
    private static List<TeamRosterEntry> BuildDefaultPattern()
    {
        var entries = new List<TeamRosterEntry>();
        for (var cycleDay = 0; cycleDay < 4; cycleDay++)
        {
            for (var shiftId = 1; shiftId <= 3; shiftId++)
            {
                var posteIndex = shiftId - 1; // 0=Matin, 1=Soir, 2=Nuit
                entries.Add(new TeamRosterEntry
                {
                    CycleDay = cycleDay,
                    ShiftId = shiftId,
                    TeamIndex = (cycleDay + posteIndex) % 4,
                });
            }
        }
        return entries;
    }
}

public class AppSettings
{
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;
    public SqlServerSettings SqlServer { get; set; } = new();
    public List<string> HiddenMachineIds { get; set; } = new();

    // How long each slide (sector pair, PDF, annual summary) stays on screen.
    public int SlideDurationSeconds { get; set; } = 15;

    // How often the slide deck itself is rebuilt (drops/reinstates disconnected
    // machines, refreshes underlying data). Spec calls for 1-5 min, configurable.
    public int DataRefreshIntervalMinutes { get; set; } = 2;

    public TeamRotationSettings TeamRotation { get; set; } = new();

    // Full path to the PDF shown on the "document externe" slide (consignes,
    // instructions, documents qualite). Empty = that slide is skipped.
    public string PdfDocumentPath { get; set; } = "";

    public AppTheme Theme { get; set; } = AppTheme.Dark;
}
