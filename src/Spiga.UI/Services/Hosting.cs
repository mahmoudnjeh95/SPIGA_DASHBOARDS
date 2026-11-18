using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spiga.Core.Audit;
using Spiga.Core.Plant;
using Spiga.Core.Security;
using Spiga.Core.Waste;
using Spiga.Data;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace Spiga.UI.Services;

// The SPIGA Dashboards screens that are WPF, not Razor: in the Windows
// application they are shown in the main window beside the menu.
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

    // The station's hall screen: for NativeAccess.CanTv only.
    void OpenTvScreen();

    // The WPF pages follow the signed-in user's light / dark choice.
    void ApplyTheme(bool dark);

    // Clicks and keys in a WPF page (not seen by the Razor screens).
    event Action? UserActivity;

    // A page was closed with its own button (Fermer, Enregistrer, Annuler).
    event Action<NativePage>? NativeClosed;

    // The dashboard's own Arrêts / Paramètres buttons, and the links between
    // the stop follow-up pages ("Qualifier →", "Planifier un préventif").
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
    public void ApplyTheme(bool dark) { }
    public event Action? UserActivity { add { } remove { } }
    public event Action<NativePage>? NativeClosed { add { } remove { } }
    public event Action<NativePage>? NavigateRequested { add { } remove { } }
}

// The application database (spiga_suivi) as a connection factory for the
// production-side repositories (planning, shared settings, note, machine
// names), which moved there from "stats" in version 4.
public static class AppData
{
    public static ConnectionStringFactory ConnectionFor(AppDbOptions options)
        => new(options.Provider == AppDbProvider.SqlServer ? DatabaseProvider.SqlServer : DatabaseProvider.Sqlite, options.ConnectionString);

    // At start-up, after the migrations: copies once what older versions
    // wrote in stats (LegacyDataImporter). Never stops the start-up.
    public static async Task ImportLegacyAsync(IDbConnectionFactory production, AppDbOptions options)
    {
        try
        {
            foreach (var line in await new LegacyDataImporter(production, ConnectionFor(options)).ImportAsync())
                AppLogger.Log("Reprise des données de la base de production : " + line);
        }
        catch (Exception ex)
        {
            AppLogger.Log("Reprise des données de la base de production impossible pour l'instant (nouvel essai au prochain démarrage) : " + ex.Message);
        }
    }
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

        services.AddSingleton(productionSettings);
        services.AddSingleton(production);
        // stats is only read; what the screens write goes to the application database.
        services.AddSingleton(sp => new ProductionRepository(production, AppData.ConnectionFor(appDb), productionSettings));
        services.AddSingleton(sp => new TeamScheduleService(productionSettings));
        services.AddSingleton<PlantData>();
        services.AddSingleton<IPlantReferential>(sp => sp.GetRequiredService<PlantData>());

        services.AddScoped<ClientInfo>();
        services.AddScoped<AuthService>();
        services.AddScoped<AccessAdminService>();
        services.AddScoped<AuditTrailService>();
        services.AddScoped<WasteService>();
        services.AddScoped<UserSession>();
        services.AddScoped<DashboardQueries>();
        services.AddScoped<SourceStatusService>();
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

    // Menu: the screens planned for a later version ("à venir") are folded
    // away unless the user opens them.
    public bool ShowUpcoming { get; set; }

    // How the user set up each chart (SpigaChart), by chart key.
    public Dictionary<string, ChartView> Charts { get; } = new();
}

public enum ChartKind { Auto, Bars, Line, Points, Area }

public sealed record ChartView(ChartKind Kind, IReadOnlySet<string> Hidden);

// One series of a SpigaChart. Reference = a target / threshold: always a
// dashed line, never changed into bars or points.
public sealed record ChartSeries(string Name, IReadOnlyList<double?> Values, string Color,
    ChartKind Kind = ChartKind.Bars, bool Reference = false);
