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

// What only the desktop app can do: open the SPIGA Dashboards windows that
// are not yet Razor screens. The web host registers NoDesktop.
// allowSettings: whether the user may open the station's Paramètres from
// those windows (Paramètres > Administrer).
public interface IDesktopBridge
{
    bool IsAvailable { get; }
    void OpenDashboard(bool allowSettings);
    void OpenTvScreen(bool allowSettings);
    void OpenStops(bool allowSettings);
    void OpenPlanning(bool allowSettings);
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
    public void OpenDashboard(bool allowSettings) { }
    public void OpenTvScreen(bool allowSettings) { }
    public void OpenStops(bool allowSettings) { }
    public void OpenPlanning(bool allowSettings) { }
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
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<IAuditLog, AuditStore>();
        services.AddSingleton<IWasteStore, WasteStore>();

        services.AddSingleton(productionSettings);
        services.AddSingleton(production);
        services.AddSingleton(sp => new ProductionRepository(production, productionSettings));
        services.AddSingleton(sp => new TeamScheduleService(productionSettings));
        services.AddSingleton<PlantData>();
        services.AddSingleton<IPlantReferential>(sp => sp.GetRequiredService<PlantData>());

        services.AddScoped<ClientInfo>();
        services.AddScoped<AuthService>();
        services.AddScoped<AccessAdminService>();
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
}
