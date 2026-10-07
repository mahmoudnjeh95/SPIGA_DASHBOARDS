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
    public string Host { get; set; } = @".\SQLEXPRESS";
    public string Database { get; set; } = "stats";
    // The production server is reached with a SQL login (user + password).
    public SqlAuthMode AuthMode { get; set; } = SqlAuthMode.SqlLogin;
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

// Ideal line speed (bags/min) for a bag weight: the Performance input for TRS.
public class NominalRateEntry
{
    public double WeightGrams { get; set; }
    public double RatePerMinute { get; set; }
}

public class AppSettings
{
    // Default to the real Stiavelli production DB (SQL Server `stats`); the
    // SqlServer defaults below point at the local .\SQLEXPRESS instance. A
    // missing/blank settings.json therefore connects to real data out of the box.
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.SqlServer;
    public SqlServerSettings SqlServer { get; set; } = new();
    public List<string> HiddenMachineIds { get; set; } = new();

    // How long each slide (sector pair, PDF, annual summary) stays on screen.
    public int SlideDurationSeconds { get; set; } = 15;

    // How often the slide deck itself is rebuilt (drops/reinstates disconnected
    // machines, refreshes underlying data). Spec calls for 1-5 min, configurable.
    public int DataRefreshIntervalMinutes { get; set; } = 2;

    // A line counts as "connected" while its latest produzione snapshot is
    // younger than this many minutes. 0 = automatic: 2.5x the line's own
    // snapshot interval (Stiavelli's cadence is configurable, 30 min down to
    // under 1 min), bounded to 2 min..2 h.
    public int ConnectedWindowMinutes { get; set; } = 0;

    // While the DB is unreachable, the dashboard retries on its own this often.
    public int ReconnectIntervalSeconds { get; set; } = 30;

    public TeamRotationSettings TeamRotation { get; set; } = new();

    // Full path to the PDF shown on the "document externe" slide (consignes,
    // instructions, documents qualite). Empty = that slide is skipped.
    public string PdfDocumentPath { get; set; } = "";

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    // Cadence nominale per bag weight, set in Parametres. A production is
    // matched on its target net weight (see FindNominalRate); 0 = not set yet,
    // so Performance and TRS stay at 0 for that weight.
    public List<NominalRateEntry> NominalRates { get; set; } = new()
    {
        new() { WeightGrams = 200 },
        new() { WeightGrams = 400 },
        new() { WeightGrams = 500 },
        new() { WeightGrams = 800 },
        new() { WeightGrams = 1000 },
    };

    // Rate of the entry closest to targetWeight, if within 5 % of it (Stiavelli
    // targets can be a few grams off the nominal size, e.g. 1005 g for 1 kg).
    public double FindNominalRate(double targetWeight)
    {
        if (targetWeight <= 0)
            return 0;
        var best = NominalRates
            .Where(r => r.WeightGrams > 0 && r.RatePerMinute > 0)
            .MinBy(r => Math.Abs(r.WeightGrams - targetWeight));
        return best != null && Math.Abs(best.WeightGrams - targetWeight) <= 0.05 * best.WeightGrams
            ? best.RatePerMinute
            : 0;
    }
}
