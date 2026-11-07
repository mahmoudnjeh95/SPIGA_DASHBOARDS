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
    public string Username { get; set; } = "Stiavelli";

    // DPAPI-protected (CurrentUser scope), base64. Never store the plain password on disk.
    public string EncryptedPassword { get; set; } = "";
}

// One roster row: on cycle day CycleDay, poste ShiftId (1=Matin/2=Après-midi/3=Nuit)
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
    // Defaults = the site's roster as set on the production PC (v1.5.15,
    // 2026-11-06), so a new PC starts right before the shared settings arrive.
    public string[] TeamNames { get; set; } = { "Equipe D", "Equipe B", "Equipe C", "Equipe A" };

    // Anchor date for CycleDay 0. Nudge this to realign the cycle.
    public DateTime RotationStartDate { get; set; } = new(2026, 11, 1);

    public List<TeamRosterEntry> Pattern { get; set; } = BuildDefaultPattern();

    // The site's 8-day cycle: each team works 2 days on each poste (Matin,
    // then Après-midi, then Nuit), then rests 2 days. One row per cycle
    // day: team index (in TeamNames) on Matin, Après-midi, Nuit.
    private static readonly int[][] SiteCycle =
    {
        new[] { 2, 0, 3 },
        new[] { 1, 2, 0 },
        new[] { 1, 2, 0 },
        new[] { 3, 1, 2 },
        new[] { 3, 1, 2 },
        new[] { 0, 3, 1 },
        new[] { 0, 3, 1 },
        new[] { 2, 0, 3 },
    };

    private static List<TeamRosterEntry> BuildDefaultPattern()
    {
        var entries = new List<TeamRosterEntry>();
        for (var cycleDay = 0; cycleDay < SiteCycle.Length; cycleDay++)
        {
            for (var shiftId = 1; shiftId <= 3; shiftId++)
                entries.Add(new TeamRosterEntry { CycleDay = cycleDay, ShiftId = shiftId, TeamIndex = SiteCycle[cycleDay][shiftId - 1] });
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

// One slide of Parametres > Affichage, by SlideDescriptor.Key. The list's
// order is the rotation's order.
public class SlideSetting
{
    public string Key { get; set; } = "";
    public bool Enabled { get; set; } = true;
    // null = SlideDurationSeconds.
    public int? DurationSeconds { get; set; }
}

public class AppSettings
{
    // Default to the real Stiavelli production DB (SQL Server `stats`); the
    // SqlServer defaults below point at the local .\SQLEXPRESS instance. A
    // missing/blank settings.json therefore connects to real data out of the box.
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.SqlServer;
    public SqlServerSettings SqlServer { get; set; } = new();
    public List<string> HiddenMachineIds { get; set; } = new();

    // Lines left out of the Planification calendar on this PC (its "Lignes"
    // list); the alerts and preventive lists still show them.
    public List<string> PlanningHiddenMachineIds { get; set; } = new();

    // How long each slide (sector pair, PDF, annual summary) stays on screen.
    public int SlideDurationSeconds { get; set; } = 15;

    // Rotation order, slides switched off and per-slide durations (Parametres
    // > Affichage). Empty = the default deck, every slide shown. Default =
    // the production PC's order (2026-11-06).
    public List<SlideSetting> Slides { get; set; } = new[]
    {
        "etat:Cumul",
        "machine:CC03", "machine:CC04", "machine:CC01", "machine:CC02",
        "machine:PC01", "machine:PC02", "machine:PC03",
        "machine:PL03", "machine:PL02", "machine:PL01",
        "pdf",
        "synthese:Couscous", "synthese:Pate courte", "synthese:Pate longue",
        "production:Conditionnement 1", "trs:Conditionnement 1",
        "production:Conditionnement 2", "trs:Conditionnement 2",
        "production:Cumul", "trs:Cumul",
    }.Select(key => new SlideSetting { Key = key }).ToList();

    public int SlideSecondsFor(string key)
        => Math.Max(1, Slides.FirstOrDefault(s => s.Key == key)?.DurationSeconds ?? SlideDurationSeconds);

    // How often the slide deck itself is rebuilt (drops/reinstates disconnected
    // machines, refreshes underlying data). Spec calls for 1-5 min, configurable.
    public int DataRefreshIntervalMinutes { get; set; } = 1;

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
    // Defaults = the production PC's table (2026-11-06).
    public List<NominalRateEntry> NominalRates { get; set; } = new()
    {
        new() { MachineId = "CC01", WeightGrams = 800, RatePerMinute = 60, TareGrams = 4 },
        new() { MachineId = "CC01", WeightGrams = 1000, RatePerMinute = 60, TareGrams = 5 },
        new() { MachineId = "CC02", WeightGrams = 500, RatePerMinute = 60 },
        new() { MachineId = "CC02", WeightGrams = 1000, RatePerMinute = 60 },
        new() { MachineId = "CC03", WeightGrams = 1000, RatePerMinute = 50, TareGrams = 6 },
        new() { MachineId = "CC04", WeightGrams = 500, RatePerMinute = 50, TareGrams = 8 },
        new() { MachineId = "CC04", WeightGrams = 800, RatePerMinute = 50, TareGrams = 5 },
        new() { MachineId = "CC04", WeightGrams = 1000, RatePerMinute = 50, TareGrams = 6 },
        new() { MachineId = "PC01", WeightGrams = 400, RatePerMinute = 100 },
        new() { MachineId = "PC02", WeightGrams = 500, RatePerMinute = 100 },
        new() { MachineId = "PC03", WeightGrams = 500, RatePerMinute = 100 },
        new() { MachineId = "PL01", WeightGrams = 400, RatePerMinute = 100, TareGrams = 5 },
        new() { MachineId = "PL02", WeightGrams = 400, RatePerMinute = 100 },
        new() { MachineId = "PL03", WeightGrams = 500, RatePerMinute = 100 },
    };

    // TRS target (%) per machine, shown next to each machine's TRS and used
    // for its colour band. Machines not listed use DefaultTrsTarget.
    // Defaults = the production PC's targets (2026-11-06).
    public Dictionary<string, double> MachineTrsTargets { get; set; } = new()
    {
        ["CC01"] = 45, ["CC02"] = 45, ["CC03"] = 45, ["CC04"] = 45,
        ["PC01"] = 45, ["PC02"] = 45, ["PC03"] = 45,
        ["PL01"] = 45, ["PL02"] = 45, ["PL03"] = 45,
    };

    public const double DefaultTrsTarget = 85;

    // TRS tolerance (points) per machine: orange from target - tolerance,
    // red below. Machines not listed use the Couleurs share of the target.
    public Dictionary<string, double> MachineTrsTolerances { get; set; } = new();

    // Weight-deviation colour limits per machine (Parametres > Machines).
    // Machines not listed use WeightBand.Default.
    public Dictionary<string, WeightBand> MachineWeightBands { get; set; } = new();

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

    // Stop pop-up (lines stopped with no cause yet), on this PC only: shown
    // or not, and where it was dragged to, as an offset from its place at the
    // top right of the slide (1600x900 canvas, same on the TV).
    public bool ShowStopAlerts { get; set; } = true;
    public double StopAlertOffsetX { get; set; }
    public double StopAlertOffsetY { get; set; }

    // Version of the shared settings (see SharedSettings) last received from
    // the base; 0 = never received.
    public int SharedSettingsVersion { get; set; }

    // Green / orange / red limits of the coloured figures (Parametres >
    // Couleurs). Also published as ColorThresholds.Current, read by the
    // colour converters and KpiCalculator (there is one AppSettings per app,
    // and this setter runs on load and when the shared settings arrive).
    private ColorThresholds _colors = new();
    public ColorThresholds Colors
    {
        get => _colors;
        set
        {
            _colors = value ?? new ColorThresholds();
            ColorThresholds.Current = _colors;
        }
    }

    // Speed and colours of the scrolling stop bar (Parametres > Couleurs),
    // published as StopTickerSettings.Current like Colors above.
    private StopTickerSettings _stopTicker = new();
    public StopTickerSettings StopTicker
    {
        get => _stopTicker;
        set
        {
            _stopTicker = value ?? new StopTickerSettings();
            StopTickerSettings.Current = _stopTicker;
        }
    }

    public double TrsTarget(string machineId)
        => MachineTrsTargets.TryGetValue(machineId, out var target) && target > 0 ? target : DefaultTrsTarget;

    public TrsBand TrsBandFor(string machineId)
        => TrsBand.ForTarget(TrsTarget(machineId),
            MachineTrsTolerances.TryGetValue(machineId, out var tolerance) && tolerance >= 0 ? tolerance : null);

    public WeightBand WeightBandFor(string machineId)
        => MachineWeightBands.TryGetValue(machineId, out var band) && band != null ? band : WeightBand.Default;

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
