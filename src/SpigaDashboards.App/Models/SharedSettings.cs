using System.Text.Json;

namespace SpigaDashboards.App.Models;

// The part of AppSettings every PC must agree on, because it changes the
// numbers (TRS, speeds, stops, teams) or what every screen shows (slide
// rotation): kept in the shared database (dbo.AppSharedSettings) so a change
// made on one PC reaches the others. The rest (DB connection, PDF path,
// theme, TV screen, hidden machines, refresh cadences) belongs to each
// screen and stays in its settings.json.
public class SharedSettings
{
    public List<NominalRateEntry> NominalRates { get; set; } = new();
    public Dictionary<string, double> MachineTrsTargets { get; set; } = new();
    // null = saved by a version before per-machine tolerances: kept as is.
    public Dictionary<string, double>? MachineTrsTolerances { get; set; }
    public List<PlannedBreak> PlannedBreaks { get; set; } = new();
    public int StopThresholdMinutes { get; set; }
    public TeamRotationSettings TeamRotation { get; set; } = new();
    public DateTime DataStartDate { get; set; }
    // Slide rotation (Parametres > Affichage). null = saved by a version
    // before 1.5.13, which didn't share it: each PC keeps its own.
    public List<SlideSetting>? Slides { get; set; }
    public int? SlideDurationSeconds { get; set; }
    // Colour limits (Parametres > Couleurs). null = saved by a version
    // before they could be set: each PC keeps its own.
    public ColorThresholds? Colors { get; set; }

    public static SharedSettings From(AppSettings settings) => new()
    {
        NominalRates = settings.NominalRates,
        MachineTrsTargets = settings.MachineTrsTargets,
        MachineTrsTolerances = settings.MachineTrsTolerances,
        PlannedBreaks = settings.PlannedBreaks,
        StopThresholdMinutes = settings.StopThresholdMinutes,
        TeamRotation = settings.TeamRotation,
        DataStartDate = settings.DataStartDate,
        Slides = settings.Slides,
        SlideDurationSeconds = settings.SlideDurationSeconds,
        Colors = settings.Colors,
    };

    public void ApplyTo(AppSettings settings)
    {
        settings.NominalRates = NominalRates;
        settings.MachineTrsTargets = MachineTrsTargets;
        if (MachineTrsTolerances != null)
            settings.MachineTrsTolerances = MachineTrsTolerances;
        settings.PlannedBreaks = PlannedBreaks;
        settings.StopThresholdMinutes = StopThresholdMinutes;
        settings.TeamRotation = TeamRotation;
        settings.DataStartDate = DataStartDate;
        if (Slides != null)
            settings.Slides = Slides;
        if (SlideDurationSeconds is int seconds)
            settings.SlideDurationSeconds = seconds;
        if (Colors != null)
            settings.Colors = Colors;
    }

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(From(settings));

    public static SharedSettings? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SharedSettings>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
