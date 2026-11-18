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

// IT: "dotnet Spiga.Web.dll --update-database [--switch-plant-data]" applies a version's table
// changes with the administrator connection Spiga:AppDatabase:AdminSqlServer
// (not kept in the running site's settings), then stops. The site's own
// login then only needs to read and write.
if (args.Contains("--update-database", StringComparer.OrdinalIgnoreCase))
{
    var admin = builder.Configuration["Spiga:AppDatabase:AdminSqlServer"];
    var updateOptions = string.IsNullOrWhiteSpace(admin) ? appDb : new AppDbOptions { Provider = AppDbProvider.SqlServer, ConnectionString = admin };
    var ran = await new Migrator(new AppDb(updateOptions)).MigrateAsync();
    await AppData.ImportLegacyAsync(production, updateOptions);
    Console.WriteLine($"{ran} mise(s) à jour appliquée(s) ; base en version {Migrator.CurrentVersion}.");
    // Once every PC runs 2.1 (see DatabaseUpdateTool): planning, note and
    // shared settings move from stats to the application database.
    if (args.Contains("--switch-plant-data", StringComparer.OrdinalIgnoreCase))
        foreach (var line in await PlantDataLocation.SwitchToApplicationAsync(
                     AppData.ConnectionFor(updateOptions), production, Environment.UserName, Console.WriteLine))
            Console.WriteLine(line);
    var place = (await PlantDataLocation.ReadAsync(AppData.ConnectionFor(updateOptions)))?.Mode ?? PlantDataMode.Application;
    Console.WriteLine("Planification, note et réglages partagés : " + AppData.Describe(place) + ".");
    return;
}

await app.Services.GetRequiredService<Migrator>().MigrateAsync();
// What versions up to 2.0 wrote in stats (planning, shared settings, note):
// copied once into the application database, stats being only read now.
await AppData.ImportLegacyAsync(production, appDb);

// The site's shared settings (nominal rates, TRS targets, teams), as the
// desktop dashboard pulls them (application database).
await new SharedSettingsSync(new SharedSettingsRepository(AppData.ConnectionFor(appDb), production), productionSettings, new SettingsService()).PullAsync();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Routes).Assembly);

app.Run();
