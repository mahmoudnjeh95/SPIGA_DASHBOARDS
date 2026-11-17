using Spiga.Core.Audit;
using Spiga.Core.Plant;
using Spiga.Core.Security;
using Spiga.Core.Waste;
using Spiga.Data;

namespace Spiga.Tests;

public sealed class FixedClock(DateTime now) : TimeProvider
{
    public DateTime Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

public sealed class FakePlant : IPlantReferential
{
    public Task<IReadOnlyList<PlantMachine>> GetMachinesAsync() => Task.FromResult<IReadOnlyList<PlantMachine>>(new[]
    {
        new PlantMachine("CC01", "Contrôle CC01", Sectors.Couscous),
        new PlantMachine("PC01", "Peseuse PC01", Sectors.PateCourte),
        new PlantMachine("PC02", "Peseuse PC02", Sectors.PateCourte),
        new PlantMachine("PL01", "Ligne PL01", Sectors.PateLongue),
    });

    public IReadOnlyList<string> Teams { get; } = new[] { "Equipe A", "Equipe B" };

    // Matin = Equipe B, other postes = Equipe A.
    public string TeamOnShift(DateTime productionDate, int shiftId) => shiftId == 1 ? "Equipe B" : "Equipe A";
}

// A fresh application database (temporary SQLite file) with every service
// wired as the hosts wire them.
public sealed class TestApp : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"spiga-test-{Guid.NewGuid():N}.db");

    public TestApp()
    {
        Db = new AppDb(AppDbOptions.ForSqliteFile(_path));
        new Migrator(Db).MigrateAsync().GetAwaiter().GetResult();
        Users = new UserStore(Db);
        Roles = new RoleStore(Db);
        Settings = new SettingsStore(Db);
        Audit = new AuditStore(Db);
        Waste = new WasteStore(Db);
        var client = new ClientInfo { Workstation = "TEST-PC" };
        Auth = new AuthService(Users, Roles, Settings, Audit, Clock, client);
        Admin = new AccessAdminService(Users, Roles, Settings, Audit, Clock, client);
        WasteService = new WasteService(Waste, new FakePlant(), Settings, Audit, Clock, client);
    }

    public FixedClock Clock { get; } = new(new DateTime(2026, 11, 17, 10, 0, 0));
    public AppDb Db { get; }
    public UserStore Users { get; }
    public RoleStore Roles { get; }
    public SettingsStore Settings { get; }
    public AuditStore Audit { get; }
    public WasteStore Waste { get; }
    public AuthService Auth { get; }
    public AccessAdminService Admin { get; }
    public WasteService WasteService { get; }

    public const string AdminPassword = "Admin2026secure";

    public Task<Actor> SetupAdminAsync(bool templates = true)
        => Auth.CreateFirstAdminAsync("admin", "Administrateur L2I", AdminPassword, templates);

    public static RoleAssignment As(Role role, DataScope? scope = null, DateTime? from = null, DateTime? to = null)
        => new() { RoleId = role.Id, RoleName = role.Name, Scope = scope ?? DataScope.Everything, ValidFrom = from, ValidTo = to };

    public async Task<Role> RoleAsync(string code) => await Roles.GetByCodeAsync(code) ?? throw new InvalidOperationException(code);

    // Creates an account with one role (by code) and signs it in with a known password.
    public async Task<Actor> UserAsync(Actor admin, string login, string roleCode, DataScope? scope = null)
        => await UserAsync(admin, login, As(await RoleAsync(roleCode), scope));

    public async Task<Actor> UserAsync(Actor admin, string login, params RoleAssignment[] assignments)
    {
        var temporary = await Admin.CreateUserAsync(admin, new UserDraft(login, login, assignments));
        var first = await Auth.LoginAsync(login, temporary);
        await Auth.ChangePasswordAsync(first.Actor!, temporary, "Password2026x");
        return (await Auth.LoginAsync(login, "Password2026x")).Actor!;
    }

    public static DataScope Sector(string sector) => new(new[] { sector }, null, Array.Empty<string>());
    public static DataScope Team(string team) => new(Array.Empty<string>(), team, Array.Empty<string>());

    public async Task<IReadOnlyList<AuditEntry>> AuditAsync()
        => await Audit.QueryAsync(new AuditQuery(DateTime.MinValue, DateTime.MaxValue));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }
}
