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

// Per machine and bag weight: ideal line speed (bags/min, the Performance
// input for TRS) and weight of the empty bag (sachet vide), deducted from
// the weights Stiavelli records. MachineId "" = every machine (fallback when
// the machine has no row of its own for that weight).
public class NominalRateEntry
{
    public string MachineId { get; set; } = "";
    public double WeightGrams { get; set; }
    public double RatePerMinute { get; set; }
    public double TareGrams { get; set; }
}

// A planned break of every day (e.g. 10:00 for 20 min). Time the line is
// stopped inside it is left out of Disponibilite and of the stop list; a
// line that keeps running through it still gets the credit.
public class PlannedBreak
{
    public TimeSpan Start { get; set; }
    public int DurationMinutes { get; set; }
}

// Where the TV window (dashboard only, for the production-floor screen) was
// last left, so it comes back on the same screen after a restart.
public class TvScreenSettings
{
    public bool IsOpen { get; set; }
    public bool FullScreen { get; set; }
    // null = never placed yet (opens centred on the operator's screen).
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 960;
    public double Height { get; set; } = 540;
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

    // Snapshots and lots saved before this date are ignored everywhere
    // (dashboard, annual slides, history, exports): the stats DB still holds
    // 2024-2025 test data and machine counters from before go-live.
    public DateTime DataStartDate { get; set; } = new(2026, 10, 20);

    public TeamRotationSettings TeamRotation { get; set; } = new();

    // Full path to the PDF shown on the "document externe" slide (consignes,
    // instructions, documents qualite). Empty = that slide is skipped.
    public string PdfDocumentPath { get; set; } = "";

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    public TvScreenSettings TvScreen { get; set; } = new();

    // Cadence nominale and sachet vide per machine and bag weight, set in
    // Parametres. A production is matched on its line and target net weight
    // (see FindNominalRate / FindTare); rate 0 = not set yet, so Performance
    // and TRS stay unknown for that weight.
    public List<NominalRateEntry> NominalRates { get; set; } = new()
    {
        new() { WeightGrams = 200 },
        new() { WeightGrams = 400 },
        new() { WeightGrams = 500 },
        new() { WeightGrams = 800 },
        new() { WeightGrams = 1000 },
    };

    // TRS target (%) per machine, shown next to each machine's TRS and used
    // for its colour band. Machines not listed use DefaultTrsTarget.
    public Dictionary<string, double> MachineTrsTargets { get; set; } = new();

    public const double DefaultTrsTarget = 85;

    // Daily planned breaks (pauses), set in Parametres. Defaults = the site's
    // 6 breaks, two per poste.
    public List<PlannedBreak> PlannedBreaks { get; set; } = new()
    {
        new() { Start = new TimeSpan(10, 0, 0), DurationMinutes = 20 },
        new() { Start = new TimeSpan(13, 40, 0), DurationMinutes = 20 },
        new() { Start = new TimeSpan(18, 0, 0), DurationMinutes = 20 },
        new() { Start = new TimeSpan(21, 40, 0), DurationMinutes = 20 },
        new() { Start = new TimeSpan(2, 0, 0), DurationMinutes = 20 },
        new() { Start = new TimeSpan(5, 40, 0), DurationMinutes = 20 },
    };

    // A counter frozen (outside planned breaks) for at least this many
    // minutes is listed as a stop (Arrets window / printed sheet).
    public int StopThresholdMinutes { get; set; } = 5;

    public double TrsTarget(string machineId)
        => MachineTrsTargets.TryGetValue(machineId, out var target) && target > 0 ? target : DefaultTrsTarget;

    // Rate for this machine and weight: the machine's own row first, then the
    // "every machine" row. A per-article rate in Products still wins (see
    // ProductionRepository).
    public double FindNominalRate(string machineId, double targetWeight)
        => (FindEntry(machineId, targetWeight, r => r.RatePerMinute > 0)
            ?? FindEntry("", targetWeight, r => r.RatePerMinute > 0))?.RatePerMinute ?? 0;

    // Empty bag weight: the machine's own row for that weight if there is one
    // (even at 0 g), otherwise the "every machine" row.
    public double FindTare(string machineId, double targetWeight)
        => (FindEntry(machineId, targetWeight, _ => true)
            ?? FindEntry("", targetWeight, _ => true))?.TareGrams ?? 0;

    // Entry closest to targetWeight, if within 5 % of it (Stiavelli targets
    // can be a few grams off the nominal size, e.g. 1005 g for 1 kg).
    private NominalRateEntry? FindEntry(string machineId, double targetWeight, Func<NominalRateEntry, bool> usable)
    {
        if (targetWeight <= 0)
            return null;
        var best = NominalRates
            .Where(r => r.WeightGrams > 0 && (r.MachineId ?? "") == machineId && usable(r))
            .MinBy(r => Math.Abs(r.WeightGrams - targetWeight));
        return best != null && Math.Abs(best.WeightGrams - targetWeight) <= 0.05 * best.WeightGrams ? best : null;
    }
}
