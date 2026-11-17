using Spiga.Data;
using Spiga.UI;
using Spiga.UI.Services;
using Spiga.Web.Components;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Production database (Stiavelli "stats", read only). Development: the
// SQLite copy in database\spiga_dashboards.db. Production: section
// Spiga:Production of appsettings.json (SQL Server connection string).
var productionSettings = new SettingsService().Load();
IDbConnectionFactory production;
var productionConnection = builder.Configuration["Spiga:Production:SqlServer"];
if (!string.IsNullOrWhiteSpace(productionConnection))
{
    productionSettings.Provider = DatabaseProvider.SqlServer;
    production = new ConnectionStringFactory(productionConnection);
}
else
{
    production = new SqliteConnectionFactory(DatabasePathResolver.ResolveSqlitePath());
}

// The site's shared settings (nominal rates, TRS targets, teams), as the
// desktop dashboard pulls them.
await new SharedSettingsSync(new SharedSettingsRepository(production), productionSettings, new SettingsService()).PullAsync();

// Application database: SQL Server spiga_suivi on site, a SQLite file
// beside the dev database otherwise.
var appConnection = builder.Configuration["Spiga:AppDatabase:SqlServer"];
var appDb = !string.IsNullOrWhiteSpace(appConnection)
    ? new AppDbOptions { Provider = AppDbProvider.SqlServer, ConnectionString = appConnection }
    : AppDbOptions.ForSqliteFile(builder.Configuration["Spiga:AppDatabase:SqliteFile"]
        ?? Path.Combine(Path.GetDirectoryName(DatabasePathResolver.ResolveSqlitePath())!, "spiga_suivi.db"));

builder.Services.AddScoped<ISessionStore, Spiga.Web.BrowserSessionStore>();
builder.Services.AddSpigaSuivi(appDb, production, productionSettings);
builder.Services.AddSingleton<IDesktopBridge, NoDesktop>();

var app = builder.Build();
await app.Services.GetRequiredService<Migrator>().MigrateAsync();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Routes).Assembly);

app.Run();

// SQL Server production database from a plain connection string (the
// desktop app builds it from its Paramètres instead).
internal sealed class ConnectionStringFactory(string connectionString) : IDbConnectionFactory
{
    public DatabaseProvider Provider => DatabaseProvider.SqlServer;
    public System.Data.IDbConnection CreateConnection() => new Microsoft.Data.SqlClient.SqlConnection(connectionString);
}
