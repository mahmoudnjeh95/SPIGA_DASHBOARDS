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
    production = new ConnectionStringFactory(DatabaseProvider.SqlServer, productionConnection);
}
else
{
    // Development: database\spiga_dashboards.db, or another copy (a demo
    // replayed to the present day) given in Spiga:Production:SqliteFile.
    var sqliteFile = builder.Configuration["Spiga:Production:SqliteFile"];
    production = new SqliteConnectionFactory(string.IsNullOrWhiteSpace(sqliteFile) ? DatabasePathResolver.ResolveSqlitePath() : sqliteFile);
}

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
// What versions up to 2.0 wrote in stats (planning, shared settings, note):
// copied once into the application database, stats being only read now.
await AppData.ImportLegacyAsync(production, appDb);

// The site's shared settings (nominal rates, TRS targets, teams), as the
// desktop dashboard pulls them (application database).
await new SharedSettingsSync(new SharedSettingsRepository(AppData.ConnectionFor(appDb)), productionSettings, new SettingsService()).PullAsync();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Routes).Assembly);

app.Run();
