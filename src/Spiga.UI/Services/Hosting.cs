using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spiga.Core.Audit;
using Spiga.Core.Plant;
using Spiga.Core.Security;
using Spiga.Core.Staff;
using Spiga.Core.Waste;
using Spiga.Data;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace Spiga.UI.Services;

// The SPIGA Dashboards screens that were WPF pages. Those still WPF
// (NativePages.IsWpf) are shown in the main window beside the menu in the
// Windows application; the others have moved to Razor (web migration) and
// keep their route and rights here.
public enum NativePage
{
    Dashboard,
    Stops,
    StopQualification,
    StopStatistics,
    StopPlanning,
    Settings,
}

public static class NativePages
{
    public static string Route(NativePage page) => page switch
    {
        NativePage.Dashboard => "production/tableau-de-bord",
        NativePage.Stops => "maintenance/arrets",
        NativePage.StopQualification => "maintenance/qualification-arrets",
        NativePage.StopStatistics => "maintenance/statistiques-arrets",
        NativePage.StopPlanning => "maintenance/planification",
        _ => "admin/parametres",
    };

    public static string Permission(NativePage page) => page switch
    {
        NativePage.Dashboard => Perm.TableauDetaille,
        NativePage.Stops => Perm.View(Modules.Maintenance),
        // Statistics only read. Qualifying stops writes, and the planning
        // board has no read-only mode yet.
        NativePage.StopStatistics => Perm.View(Modules.Maintenance),
        NativePage.StopQualification or NativePage.StopPlanning => "maintenance:saisir",
        _ => Perm.ParametresPoste,
    };

    // Still a WPF page (the desktop application shows it beside the menu, a
    // browser a placeholder). None since the web dashboard (2026-11-21):
    // every page is a Razor page, in both hosts. The kiosk PCs (no sign-in)
    // and the desktop TV window keep the WPF dashboard.
    public static bool IsWpf(NativePage page) => false;

    // The preventive rule form of the planning board, for a line and a component.
    public static string PreventiveRoute(string machineId, string component)
        => $"{Route(NativePage.StopPlanning)}?ligne={Uri.EscapeDataString(machineId)}&composant={Uri.EscapeDataString(component)}";

    public static NativePage? FromRoute(string path)
        => Enum.GetValues<NativePage>().Cast<NativePage?>().FirstOrDefault(p => Route(p!.Value) == path);

    // Paramètres du poste act on every machine of the station (TV, rates,
    // hidden lines): only for an account whose right covers the whole plant.
    public static bool IsAllowed(Actor? actor, NativePage page)
        => actor != null && actor.Can(Permission(page)) && (page != NativePage.Settings || actor.CanEverywhere(Perm.ParametresPoste));

    // The export right of a page, if it has exports.
    public static string? ExportPermission(NativePage page) => page switch
    {
        NativePage.Dashboard => PermissionCatalog.Key(Modules.Conditionnement, AppAction.Exporter),
        NativePage.Stops or NativePage.StopStatistics => PermissionCatalog.Key(Modules.Maintenance, AppAction.Exporter),
        _ => null,
    };

    // What the account may see and do in this WPF page: the scope of the
    // page's right (team and machines), and the rights for its buttons, each
    // held only if it covers all that the page shows.
    public static NativeAccess Access(Actor actor, NativePage page)
    {
        var permission = Permission(page);
        var export = ExportPermission(page);
        var validate = PermissionCatalog.Key(Modules.Maintenance, AppAction.Valider);
        return new NativeAccess(
            actor.Login,
            actor.DisplayName,
            actor.Team,
            actor.MachineRuleFor(permission),
            CanExport: export != null && actor.Covers(export, permission),
            CanValidate: validate != null && actor.Covers(validate, permission),
            CanSettings: actor.CanEverywhere(Perm.ParametresPoste),
            CanTv: actor.CanEverywhere(Perm.EcranTv),
            CanEditNote: actor.CanEverywhere(Perm.NoteEcran));
    }
}

// The rights of the signed-in account in one WPF page. Team: its one team
// (null = several or all). Machines: those it may see there (null = all).
public sealed record NativeAccess(
    string UserLogin, string UserName, string? Team, MachineRule? Machines, bool CanExport, bool CanValidate, bool CanSettings, bool CanTv,
    bool CanEditNote = false)
{
    // No account (signed out, locked): nothing.
    public static NativeAccess None { get; } = new("", "", null, new MachineRule("-", (_, _) => false), false, false, false, false);

    // Changes whenever the machines may have changed.
    public string MachinesKey => Machines?.Key ?? "*";
}

// What only the desktop app can do. The web host registers NoDesktop.
// allowSettings: whether the user may open the station's Paramètres from
// the dashboard (Paramètres > Administrer).
public interface IDesktopBridge
{
    bool IsAvailable { get; }

    // A WPF page in the main window beside the navigation rail (null: none),
    // with what the account may see and do there (NativePages.Access): its
    // team's postes only (the others greyed out), the machines of its scope
    // only (enforced by the pages' repositories), and its buttons' rights.
    void ShowNative(NativePage? page, NativeAccess access);

    // Sign-out / lock: the pages are dropped (unsaved edits with them).
    void DiscardNativePages();

    // The station's hall screen (TV window, the web Écran TV of the whole
    // plant): for NativeAccess.CanTv only.
    void OpenTvScreen();

    // "Planifier un préventif" (Statistiques des arrêts): the planning board
    // opens on the preventive rule form for this line and component.
    // access: the account's rights on the planning page.

    // What only this PC has in its Paramètres (database connection, display,
    // stop alert): null in a browser.
    IStationHost? Station { get; }

    // The WPF pages follow the signed-in user's light / dark choice.
    void ApplyTheme(bool dark);

    // Clicks and keys in a WPF page (not seen by the Razor screens).
    event Action? UserActivity;

    // The dashboard's own Arrêts / Paramètres buttons.
    event Action<NativePage>? NavigateRequested;
}

// Keeps the sign-in across a page reload (web host: the browser tab's
// encrypted session storage). The desktop window never reloads: NoSessionStore.
public interface ISessionStore
{
    Task SaveAsync(int userId, DateTime lastActivity);
    Task<(int UserId, DateTime LastActivity)?> LoadAsync();
    Task ClearAsync();
}

public sealed class NoSessionStore : ISessionStore
{
    public Task SaveAsync(int userId, DateTime lastActivity) => Task.CompletedTask;
    public Task<(int UserId, DateTime LastActivity)?> LoadAsync() => Task.FromResult<(int, DateTime)?>(null);
    public Task ClearAsync() => Task.CompletedTask;
}

public sealed class NoDesktop : IDesktopBridge
{
    public bool IsAvailable => false;
    public void ShowNative(NativePage? page, NativeAccess access) { }
    public void DiscardNativePages() { }
    public void OpenTvScreen() { }
    public IStationHost? Station => null;
    public void ApplyTheme(bool dark) { }
    public event Action? UserActivity { add { } remove { } }
    public event Action<NativePage>? NavigateRequested { add { } remove { } }
}

// The application database (spiga_suivi) as a connection factory for the
// production-side repositories (planning, shared settings, note, machine
// names), which moved there from "stats" in version 4.
public static class AppData
{
    public static ConnectionStringFactory ConnectionFor(AppDbOptions options)
        => new(options.Provider == AppDbProvider.SqlServer ? DatabaseProvider.SqlServer : DatabaseProvider.Sqlite, options.ConnectionString);

    // At start-up, after the migrations. A new site (no 2.0 PC ever wrote in
    // stats): the planning and the rest go straight to spiga_suivi. A site
    // with PCs on 2.0: they stay in stats, for every PC, until IT switches
    // them (« Mettre à jour la base SPIGA », PlantDataLocation). Never stops
    // the start-up.
    public static async Task ImportLegacyAsync(IDbConnectionFactory production, AppDbOptions options)
    {
        try
        {
            var location = new PlantDataLocation(ConnectionFor(options), production);
            var mode = await location.ModeAsync();
            AppLogger.Log($"Données de planification, note et réglages partagés : {Describe(mode)}.");
            if (mode == PlantDataMode.Application)
                await new LegacyDataImporter(production, ConnectionFor(options)).ImportAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Log("Emplacement des données de planification non vérifié (nouvel essai au prochain démarrage) : " + ex.Message);
        }
    }

    public static string Describe(PlantDataMode mode) => mode switch
    {
        PlantDataMode.Legacy => "encore dans la base de production (des postes sont en version 2.0)",
        PlantDataMode.Switching => "en cours de transfert vers la base de l'application",
        _ => "dans la base de l'application",
    };
}

public static class SpigaServices
{
    // Everything the screens need. The host gives the application database,
    // the production database (connection factory + settings) and its
    // desktop bridge. Scoped = one per window / browser tab.
    public static IServiceCollection AddSpigaSuivi(
        this IServiceCollection services, AppDbOptions appDb, IDbConnectionFactory production, AppSettings productionSettings)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(appDb);
        services.AddSingleton<AppDb>();
        services.AddSingleton<Migrator>();
        services.AddSingleton<IUserStore, UserStore>();
        services.AddSingleton<IRoleStore, RoleStore>();
        services.AddSingleton<IPlatformStore, PlatformStore>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<IAuditLog, AuditStore>();
        services.AddSingleton<IWasteStore, WasteStore>();
        services.AddSingleton<IStaffStore, StaffStore>();
        services.AddSingleton<IAttendanceStore, AttendanceStore>();

        services.AddSingleton(productionSettings);
        services.AddSingleton(production);
        // stats is only read; what the screens write goes to the application database.
        services.AddSingleton(sp => new ProductionRepository(production, AppData.ConnectionFor(appDb), productionSettings));
        services.AddSingleton(sp => new TeamScheduleService(productionSettings));
        // Paramètres: the settings file and the shared part in the
        // application database. The desktop application registers its own
        // (same instances as its dashboard) before calling this.
        services.TryAddSingleton(_ => new SettingsService());
        services.TryAddSingleton(sp => new SharedSettingsSync(
            new SharedSettingsRepository(AppData.ConnectionFor(appDb), production), productionSettings, sp.GetRequiredService<SettingsService>()));
        services.AddSingleton<PlantData>();
        services.AddSingleton<IPlantReferential>(sp => sp.GetRequiredService<PlantData>());

        services.AddScoped<ClientInfo>();
        services.AddScoped<AuthService>();
        services.AddScoped<AccessAdminService>();
        services.AddScoped<AuditTrailService>();
        services.AddSingleton(sp => new PlantDataLocation(AppData.ConnectionFor(appDb), production));
        services.AddScoped<WasteService>();
        services.AddScoped<StaffService>();
        services.AddScoped<AttendanceService>();
        services.AddScoped<PresenceReport>();
        services.AddScoped<UserSession>();
        services.AddScoped<DashboardQueries>();
        services.AddScoped<WasteDashboard>();
        services.AddScoped<PosteBoard>();
        services.AddScoped<SourceStatusService>();
        services.AddScoped<StopStatsService>();
        services.AddScoped<StopListService>();
        services.AddScoped<StopQualificationService>();
        services.AddScoped<PlanningBoardService>();
        services.AddScoped<DashboardDeckService>();
        services.AddScoped<ProductionHistoryService>();
        services.AddScoped<StationSettingsEditor>();
        services.AddScoped<UiState>();
        services.TryAddScoped<ISessionStore, NoSessionStore>();
        return services;
    }
}

// Per-session UI memory: the filters a user set stay when they come back
// to a screen.
public sealed class UiState
{
    public Period? OverviewPeriod { get; set; }
    public Period? PackagingPeriod { get; set; }
    public string? PackagingSector { get; set; }
    public int? PackagingShift { get; set; }
    public Period? WastePeriod { get; set; }
    public WasteFilter? WasteFilter { get; set; }
    public Period? PresencePeriod { get; set; }
    public StatsFilter? StopStats { get; set; }
    public StopsFilter? Stops { get; set; }
    public QualFilter? Qualification { get; set; }
    public (PlanRange Range, DateTime Start)? Planning { get; set; }
    // Lines left off the planning board (Lignes), for the session.
    public HashSet<string> PlanningHidden { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Menu: the screens planned for a later version ("à venir") are folded
    // away unless the user opens them.
    public bool ShowUpcoming { get; set; }

    // Menu folded to its icons (more room for the sheets).
    public bool RailMini { get; set; }

    // The figures of the dashboard tiles last shown, by slide scope and
    // metric (TileFlash): the change since then glows on the tile.
    public Dictionary<string, double> TileValues { get; } = new();

    // How the user set up each chart (SpigaChart), by chart key.
    public Dictionary<string, ChartView> Charts { get; } = new();
}

// Stacked: the bars of every series piled up (production per weight...).
// Web, after sign-in (ModeChooser): the whole application, the dashboard
// alone (with Historique and Arrêts) or the TV screen.
public enum SpigaMode { App, Dashboard, Tv }

public static class OpenModes
{
    // What the account may open after sign-in, by its rights: « Ouvrir SPIGA
    // Suivi Production », « Ouvrir SPIGA Dashboard », « Ouvrir SPIGA TV »
    // (the two screens also need the detailed dashboard, the TV over the
    // whole plant). One only: it opens directly; none: nothing to open.
    public static List<SpigaMode> For(Actor? actor)
    {
        var modes = new List<SpigaMode>();
        if (actor == null)
            return modes;
        if (actor.Can(Perm.OuvrirApplication))
            modes.Add(SpigaMode.App);
        var dashboard = NativePages.IsAllowed(actor, NativePage.Dashboard);
        if (dashboard && actor.Can(Perm.OuvrirDashboard))
            modes.Add(SpigaMode.Dashboard);
        if (dashboard && actor.CanEverywhere(Perm.EcranTv))
            modes.Add(SpigaMode.Tv);
        return modes;
    }
}

// The change flash of the dashboard tiles (kiosk: TileFx): the figure moved
// by at least minDelta since this tile (same machine, period, filters, poste)
// was last on screen. better: +1 rising is good, -1 falling is good, 0
// closer to zero is good.
public static class TileFlash
{
    public static (string? Text, bool Good) Of(UiState ui, string key, double? value, double minDelta, int better, Func<double, string> format)
    {
        if (value is not { } now)
            return (null, false);
        var seen = ui.TileValues.TryGetValue(key, out var before);
        ui.TileValues[key] = now;
        var delta = now - before;
        if (!seen || Math.Abs(delta) < minDelta)
            return (null, false);
        var good = better == 0 ? Math.Abs(now) < Math.Abs(before) : Math.Sign(delta) == better;
        return ((delta > 0 ? "▲ " : "▼ ") + format(delta), good);
    }
}

public enum ChartKind { Auto, Bars, Stacked, Line, Points, Area }

public sealed record ChartView(ChartKind Kind, IReadOnlySet<string> Hidden);

// One series of a SpigaChart. Reference = a target / threshold: always a
// dashed line, never changed into bars or points. ScaleMax: the series is
// drawn on its own scale, 0 to ScaleMax, not on the chart's axis (another
// unit: a count beside hours, a cumulated %); Format: how its values read
// (else the chart's). Stack: bars with the same Stack are piled up. Step: a
// line that holds its value until the next point (run / stop, speed).
public sealed record ChartSeries(string Name, IReadOnlyList<double?> Values, string Color,
    ChartKind Kind = ChartKind.Bars, bool Reference = false, double? ScaleMax = null, Func<double, string>? Format = null,
    string? Stack = null, bool Step = false);
