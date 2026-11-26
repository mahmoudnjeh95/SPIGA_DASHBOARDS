using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;

namespace Spiga.Tests;

// A production database with two lines that ran the whole of a past day
// (snapshots every 2 minutes, lots every hour), as StatsViewer writes them.
public sealed class SnapshotDb : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"spiga-snap-{Guid.NewGuid():N}.db");

    public static readonly DateTime Day = DateTime.Today.AddDays(-3);

    public SnapshotDb()
    {
        using var c = new SqliteConnection($"Data Source={_path}");
        c.Execute(@"
            CREATE TABLE Machines (MachineId TEXT PRIMARY KEY, MachineName TEXT NOT NULL, MachineType TEXT NOT NULL, Sector TEXT NOT NULL, IPAddress TEXT NOT NULL, IsActive INTEGER NOT NULL DEFAULT 1);
            INSERT INTO Machines VALUES ('CC01','Contrôle CC01','','Couscous','',1), ('PC01','Peseuse PC01','','Pate courte','',1);
            CREATE TABLE Products (ProductId INTEGER PRIMARY KEY AUTOINCREMENT, Code TEXT NOT NULL UNIQUE, ProductName TEXT NOT NULL,
                TargetWeightGrams REAL NOT NULL DEFAULT 0, PackagingSize TEXT NOT NULL DEFAULT '', NominalRatePerMinute REAL NOT NULL DEFAULT 0);
            INSERT INTO Products (Code, ProductName, TargetWeightGrams, NominalRatePerMinute) VALUES ('000000000COUSCOUSFIN', 'Couscous fin', 1000, 12), ('0000000000000PENNE05', 'Penne', 500, 0);
            CREATE TABLE produzione (progressivo INTEGER PRIMARY KEY, ID_produzione TEXT, numero_linea TEXT, tempo_inizio TEXT, tempo_fine TEXT,
                terminata INTEGER, codice_articolo TEXT, operatore TEXT, turno INTEGER, linea_produzione TEXT, peso_netto_impostato INTEGER,
                peso_max_registrato REAL, peso_min_registrato REAL, totale_confezioni INTEGER, confezioni_sotto_toll INTEGER, confezioni_ok INTEGER,
                confezioni_sopra_toll INTEGER, confezioni_indeterminate INTEGER, media_totale REAL, deviazione_std REAL, espulsi_metal INTEGER);
            CREATE INDEX IX_produzione_linea_fine ON produzione(numero_linea, tempo_fine);
            CREATE TABLE lotto (ID_produzione TEXT, ora_fine TEXT, dimensione INTEGER, media_effettiva REAL);
            CREATE INDEX IX_lotto_ora_fine ON lotto(ora_fine);");
        Run(c, "CC01", "000000000COUSCOUSFIN", 1000, 20, Day.AddHours(-8), Day.AddHours(30), lots: true);
        Run(c, "PC01", "0000000000000PENNE05", 500, 50, Day.AddHours(-8), Day.AddHours(30), lots: false);
    }

    public static string Production(string line, string article, DateTime start) => line + article + start.ToString("dd.MM.yyyy-HH:mm:ss");

    // One production from start to end, bagsPer2Min every 2 minutes, but
    // stopped from 10:00 to 11:00 on Day when stops.
    public void Run(SqliteConnection c, string line, string article, int target, int bagsPer2Min, DateTime start, DateTime end, bool lots, bool stops = true)
    {
        var id = Production(line, article, start);
        var tot = 0;
        for (var t = start.AddMinutes(2); t <= end; t = t.AddMinutes(2))
        {
            if (!stops || !(t > Day.AddHours(10) && t <= Day.AddHours(11)))
                tot += bagsPer2Min;
            c.Execute(@"INSERT INTO produzione (ID_produzione, numero_linea, tempo_inizio, tempo_fine, terminata, codice_articolo, operatore, turno, linea_produzione,
                    peso_netto_impostato, totale_confezioni, confezioni_sotto_toll, confezioni_ok, confezioni_sopra_toll, confezioni_indeterminate, media_totale, espulsi_metal)
                VALUES (@id, @line, @start, @t, 0, @article, '', 0, @line, @target, @tot, 0, @tot, 0, 0, @avg, 0)",
                new { id, line, start = Text(start), t = Text(t), article, target, tot, avg = target * 1.01 });
            if (lots && t.Minute == 0)
                c.Execute("INSERT INTO lotto VALUES (@id, @t, @n, @avg)", new { id, t = Text(t), n = bagsPer2Min * 30, avg = target * 1.004 });
        }
    }

    public static string Text(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss");

    public SqliteConnection Open() => new($"Data Source={_path}");

    public IDbConnectionFactory Factory => new SqliteConnectionFactory(_path);

    public void Dispose()
    {
        SqliteFiles.Release(_path);
        try { File.Delete(_path); } catch (IOException) { }
    }
}

// Finished days are kept in the application database (ProductionDayCache):
// read again from there, same buckets, and read again from the production
// database when what they were made from has changed.
public class DayStoreTests
{
    private static AppSettings Settings() => new() { DataStartDate = SnapshotDb.Day.AddDays(-10), PlannedBreaks = new() };

    private static string Figures(IEnumerable<ProductionBucket> buckets) => JsonSerializer.Serialize(buckets
        .OrderBy(b => b.MachineId).ThenBy(b => b.StartTime).ThenBy(b => b.ProductionId)
        .Select(b => new
        {
            b.MachineId, b.StartTime, b.EndTime, b.ShiftId, b.ProductionId, b.ProductCode, b.ProductName, b.TargetNetWeight, b.TotalBags,
            b.BagsInTolerance, b.ObservedMinutes, b.RunMinutes, b.PlannedStopMinutes, b.MaxSpeedBagsPerMinute, b.SteadyBags, b.SteadyMinutes,
            b.NominalRate, b.IdealMinutes, b.RatedRunMinutes, Weights = b.WeightParts.OrderBy(w => w.ProductionId).ThenBy(w => w.Target).ToList(),
        }));

    private static long Stored(TestApp app)
    {
        using var c = app.AppData.CreateConnection();
        return c.ExecuteScalar<long>("SELECT COUNT(*) FROM ProductionDayCache");
    }

    [Fact]
    public async Task Each_machine_gets_its_own_day()
    {
        using var live = new SnapshotDb();
        using var app = new TestApp();
        ProductionRepository.ClearDayCache();
        var repository = new ProductionRepository(live.Factory, app.AppData, Settings());
        var day = SnapshotDb.Day;
        var cc01 = await repository.GetLotsAsync("CC01", day, day.AddDays(1));
        var pc01 = await repository.GetLotsAsync("PC01", day, day.AddDays(1));
        Assert.All(cc01, b => Assert.Equal("CC01", b.MachineId));
        Assert.All(pc01, b => Assert.Equal("PC01", b.MachineId));
        // 23 hours running: 10 bags/min on CC01, 25 on PC01.
        Assert.Equal(23 * 60 * 10, cc01.Sum(b => b.TotalBags));
        Assert.Equal(23 * 60 * 25, pc01.Sum(b => b.TotalBags));
    }

    [Fact]
    public async Task A_finished_day_is_read_back_from_the_application_database_with_the_same_figures()
    {
        using var live = new SnapshotDb();
        using var app = new TestApp();
        ProductionRepository.ClearDayCache();
        var day = SnapshotDb.Day;
        var machines = new[] { "CC01", "PC01" };
        var first = await new ProductionRepository(live.Factory, app.AppData, Settings()).GetLotsForMachinesAsync(machines, day, day.AddDays(1));
        Assert.Equal(2, Stored(app));

        // Another PC (nothing in memory): from the application database, not
        // read and saved again.
        string SavedAt()
        {
            using var c = app.AppData.CreateConnection();
            return string.Join(",", c.Query<string>("SELECT MachineId || SavedAt FROM ProductionDayCache ORDER BY MachineId"));
        }
        var saved = SavedAt();
        await Task.Delay(1100);
        ProductionRepository.ClearDayCache();
        var again = await new ProductionRepository(live.Factory, app.AppData, Settings()).GetLotsForMachinesAsync(machines, day, day.AddDays(1));
        Assert.Equal(Figures(first), Figures(again));
        Assert.Equal(saved, SavedAt());
        Assert.Contains(first, b => b.WeightParts.Count > 0);

        // A month around it: the stored day is the same inside it as alone.
        ProductionRepository.ClearDayCache();
        var month = await new ProductionRepository(live.Factory, app.AppData, Settings()).GetLotsForMachinesAsync(machines, day.AddDays(-2), day.AddDays(2));
        Assert.Equal(Figures(first), Figures(month.Where(b => b.StartTime >= day && b.StartTime < day.AddDays(1))));

        // One machine alone: its part of the stored day.
        ProductionRepository.ClearDayCache();
        var one = await new ProductionRepository(live.Factory, app.AppData, Settings()).GetLotsAsync("PC01", day, day.AddDays(1));
        Assert.Equal(Figures(first.Where(b => b.MachineId == "PC01")), Figures(one));
    }

    [Fact]
    public async Task A_stored_day_is_read_again_when_its_snapshots_lots_or_settings_change()
    {
        using var live = new SnapshotDb();
        using var app = new TestApp();
        ProductionRepository.ClearDayCache();
        var day = SnapshotDb.Day;
        var settings = Settings();
        async Task<List<ProductionBucket>> Read()
        {
            ProductionRepository.ClearDayCache();
            return await new ProductionRepository(live.Factory, app.AppData, settings).GetLotsAsync("CC01", day, day.AddDays(1));
        }

        var before = await Read();
        // StatsViewer catching up late: a second production in the stopped hour.
        using (var c = live.Open())
            live.Run(c, "CC01", "0000000000000PENNE05", 500, 40, day.AddHours(10), day.AddHours(10).AddMinutes(40), lots: false, stops: false);
        var after = await Read();
        Assert.Equal(before.Sum(b => b.TotalBags) + 20 * 40, after.Sum(b => b.TotalBags));

        // A lot uploaded late.
        using (var c = live.Open())
            c.Execute("INSERT INTO lotto VALUES (@id, @t, 100, 1010)",
                new { id = SnapshotDb.Production("CC01", "000000000COUSCOUSFIN", day.AddHours(-8)), t = SnapshotDb.Text(day.AddHours(15).AddMinutes(30)) });
        var lot = await Read();
        Assert.Equal(after.Sum(b => b.WeightParts.Sum(w => w.Bags)) + 100, lot.Sum(b => b.WeightParts.Sum(w => w.Bags)));

        // A planned break over the stop (Paramètres): the stopped hour is planned.
        settings.PlannedBreaks.Add(new PlannedBreak { Start = new TimeSpan(10, 0, 0), DurationMinutes = 60 });
        var withBreak = await Read();
        Assert.True(withBreak.Sum(b => b.PlannedStopMinutes) > lot.Sum(b => b.PlannedStopMinutes));

        // The empty bag weight and the nominal rates of Paramètres apply to a
        // stored day as it is: kept before them.
        Assert.Equal(1, Stored(app));
        var entry = settings.NominalRates.Single(r => r.MachineId == "CC01" && r.WeightGrams == 1000);
        entry.TareGrams += 10;
        var tared = await Read();
        Assert.Equal(1, Stored(app));
        var weighed = withBreak.Sum(b => b.WeightParts.Where(w => w.Target == 1000).Sum(w => w.Bags));
        Assert.Equal(
            withBreak.Sum(b => b.WeightParts.Where(w => w.Target == 1000).Sum(w => w.WeightSum)) - 10 * weighed,
            tared.Sum(b => b.WeightParts.Where(w => w.Target == 1000).Sum(w => w.WeightSum)), 3);
    }
}
