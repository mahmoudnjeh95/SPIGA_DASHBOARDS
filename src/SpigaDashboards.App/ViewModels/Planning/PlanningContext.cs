using System.Globalization;
using System.Windows.Media;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels.Planning;

// What the three tabs of the Planification window share: data access, the
// settings, the team rotation and the machine list (Parametres names).
public sealed class PlanningContext
{
    public static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public ProductionRepository Production { get; }
    public PlanningRepository Planning { get; }
    public AppSettings Settings { get; }
    public SettingsService SettingsService { get; }
    public TeamScheduleService Teams { get; }
    public List<Machine> Machines { get; private set; } = new();

    // « Valider les interventions » (SPIGA Suivi Production): marking a
    // maintenance done. Without it the board shows them but cannot close
    // them. Production and Planning are limited to the account's machines.
    public bool CanValidate { get; init; } = true;

    public PlanningContext(ProductionRepository production, PlanningRepository planning, AppSettings settings,
        SettingsService settingsService, TeamScheduleService teams)
    {
        Production = production;
        Planning = planning;
        Settings = settings;
        SettingsService = settingsService;
        Teams = teams;
    }

    public async Task LoadMachinesAsync()
    {
        var all = await Production.GetMachinesAsync();
        Machines = all
            .Where(m => m.IsActive && !Settings.HiddenMachineIds.Contains(m.MachineId))
            .OrderBy(m => SectorOrder(m.Sector)).ThenBy(m => m.MachineId)
            .ToList();
    }

    public string NameOf(string machineId)
        => Machines.FirstOrDefault(m => m.MachineId == machineId)?.MachineName ?? machineId;

    public string TeamOf(DateTime shiftDate, int shiftId)
        => Teams.GetTeamForShift(shiftDate, shiftId) is { Length: > 0 } team ? team : TeamScheduleService.NoTeam;

    private static int SectorOrder(string sector) => sector switch
    {
        "Couscous" => 0,
        "Pate courte" => 1,
        "Pate longue" => 2,
        _ => 3,
    };

    // One hue per sector (blue / aqua / violet), the same on every tab.
    public static Color SectorColor(string sector) => sector switch
    {
        "Couscous" => Color.FromRgb(0x39, 0x87, 0xE5),
        "Pate courte" => Color.FromRgb(0x19, 0x9E, 0x70),
        "Pate longue" => Color.FromRgb(0x90, 0x85, 0xE9),
        _ => Color.FromRgb(0x9A, 0xA4, 0xB2),
    };

    public static Color FamilyColor(CauseFamily family) => family switch
    {
        CauseFamily.Panne => Color.FromRgb(0xD0, 0x3B, 0x3B),
        CauseFamily.Process => Color.FromRgb(0x39, 0x87, 0xE5),
        CauseFamily.Organisation => Color.FromRgb(0x9A, 0xA4, 0xB2),
        _ => Color.FromRgb(0xFA, 0xB2, 0x19),
    };

    public static SolidColorBrush Brush(Color color, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    // "7 min", "1 h 05", and from a day on "2 j", "1 j 6 h", "2 j 4 h 30"
    public static string Duration(double minutes)
    {
        var total = (int)Math.Round(minutes);
        if (total < 24 * 60)
            return ProductionStop.FormatDuration(total);
        var (days, hours, mins) = (total / 1440, total % 1440 / 60, total % 60);
        return $"{days} j" + (hours > 0 || mins > 0 ? $" {hours} h" : "") + (mins > 0 ? $" {mins:00}" : "");
    }

    // "Jeu 29"
    public static string Day(DateTime date)
    {
        var name = French.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek).TrimEnd('.');
        return char.ToUpper(name[0]) + name[1..] + " " + date.Day;
    }

    public static string Bags(double count) => count.ToString("N0", French);
}
