using Dapper;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.Data;

public enum BucketSize
{
    Hour,
    Day,
}

// Reads Stiavelli's dbo.produzione directly. Each row there is a SNAPSHOT of
// the production running on a line (ID_produzione), with counters cumulative
// since that production started. Stiavelli's snapshot cadence is
// configurable (30 min by default, can go under 1 min), so every query here:
//   - only reads the snapshots of its own time range (+ SnapshotMargin around
//     it, to find the counters each interval starts from) -- seeks on the
//     IX_produzione_linea_fine index created by stiavelli_compat.sql;
//   - turns consecutive snapshots into per-interval deltas (LAG) and sums them
//     into hour/day buckets ON THE SERVER, so what reaches the app doesn't
//     grow with the snapshot frequency.
// Each interval is attributed to the bucket containing its midpoint.
// Deltas are computed per ID_produzione: one production = one article run on
// one line, and a new one is opened at each changeover, so a new production
// simply starts again from zero.
//
// Line status (connected / belt running / availability) is derived from the
// same snapshots -- Stiavelli doesn't store it anywhere.
//
// Date bounds are always DateTime parameters, never strings: a 'yyyy-MM-dd'
// string compared to a SQL Server DATETIME is parsed with the login's
// language (French = DMY on this site) and silently swaps day/month.
public class ProductionRepository
{
    // How far before/after the requested range snapshots are read. Covers the
    // interval crossing each boundary as long as Stiavelli wasn't silent for
    // longer than this; beyond that, the first interval after the gap is
    // dropped rather than guessed.
    private static readonly TimeSpan SnapshotMargin = TimeSpan.FromHours(6);

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly AppSettings _settings;

    public ProductionRepository(IDbConnectionFactory connectionFactory, AppSettings settings)
    {
        _connectionFactory = connectionFactory;
        _settings = settings;
    }

    // Drops pooled connections, which are all dead after a network/server drop
    // and would otherwise make the first queries after a reconnect fail again.
    public void ResetConnections() => _connectionFactory.ResetPool();

    public async Task<List<Machine>> GetMachinesAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        var machines = await connection.QueryAsync<Machine>(
            "SELECT MachineId, MachineName, MachineType, Sector, IPAddress, IsActive FROM Machines ORDER BY MachineId");
        return machines.ToList();
    }

    private sealed class SnapshotRow
    {
        public string ProductionId { get; set; } = "";
        public DateTime SnapshotTime { get; set; }
        public DateTime? ProductionStart { get; set; }
        public bool Terminated { get; set; }
        public string? ArticleCode { get; set; }
        public string? ProductName { get; set; }
        public long TotalBags { get; set; }
        public long BagsInTolerance { get; set; }
        public long BagsUnderTolerance { get; set; }
        public long BagsOverTolerance { get; set; }
        public double? AvgWeight { get; set; }
        public double? TargetNetWeight { get; set; }
        public DateTime ServerNow { get; set; }
    }

    // Connected = the line's latest snapshot is recent, judged against the
    // line's own cadence (2.5x the gap between its last two snapshots, 2 min to
    // 2 h) and the SERVER clock, so a drifting kiosk clock can't flag lines as
    // disconnected when snapshots are seconds apart. Belt running = the
    // counter moved between those two snapshots. Alarm: no source, always false.
    // Also returns the line's current production (its latest snapshot).
    public async Task<MachineStatusSnapshot?> GetLatestStatusAsync(string machineId)
    {
        var d = Dialect.For(_connectionFactory.Provider);
        using var connection = _connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<SnapshotRow>(
            $@"SELECT {d.Top2} RTRIM(p.ID_produzione) AS ProductionId, p.tempo_fine AS SnapshotTime,
                      p.tempo_inizio AS ProductionStart, COALESCE(p.terminata, 0) AS Terminated,
                      RTRIM(p.codice_articolo) AS ArticleCode, pr.ProductName,
                      COALESCE(p.totale_confezioni, 0)     AS TotalBags,
                      COALESCE(p.confezioni_ok, 0)         AS BagsInTolerance,
                      COALESCE(p.confezioni_sotto_toll, 0) AS BagsUnderTolerance,
                      COALESCE(p.confezioni_sopra_toll, 0) AS BagsOverTolerance,
                      CAST(p.media_totale AS {d.Float})         AS AvgWeight,
                      CAST(p.peso_netto_impostato AS {d.Float}) AS TargetNetWeight,
                      {d.Now} AS ServerNow
               FROM produzione p
               LEFT JOIN Products pr ON pr.Code = RTRIM(p.codice_articolo)
               WHERE p.numero_linea = @machineId AND p.tempo_fine >= {d.NowMinus6Hours}
               ORDER BY p.tempo_fine DESC {d.Limit2}",
            new { machineId = Line(machineId) })).ToList();

        if (rows.Count == 0)
            return new MachineStatusSnapshot { MachineId = machineId, IsConnected = false };

        var latest = rows[0];
        TimeSpan window;
        if (_settings.ConnectedWindowMinutes > 0)
            window = TimeSpan.FromMinutes(_settings.ConnectedWindowMinutes);
        else if (rows.Count > 1)
            window = TimeSpan.FromSeconds(Math.Clamp(
                2.5 * (latest.SnapshotTime - rows[1].SnapshotTime).TotalSeconds, 120, 7200));
        else
            window = TimeSpan.FromHours(1);

        var connected = latest.ServerNow - latest.SnapshotTime <= window;
        var producing = rows.Count > 1 && rows[1].ProductionId == latest.ProductionId
            ? latest.TotalBags > rows[1].TotalBags
            : latest.TotalBags > 0;

        return new MachineStatusSnapshot
        {
            MachineId = machineId,
            Timestamp = latest.SnapshotTime,
            IsConnected = connected,
            BeltRunning = connected && producing,
            AlarmActive = false,
            CurrentProduction = ToProductionInfo(latest),
        };
    }

    private static ProductionInfo ToProductionInfo(SnapshotRow row)
    {
        // ID_produzione = line + article + start: fallback for blank columns.
        var parts = ProductionIdParts.Parse(row.ProductionId);
        var article = string.IsNullOrWhiteSpace(row.ArticleCode) ? parts.ArticleCode : row.ArticleCode;
        var target = row.TargetNetWeight ?? 0;
        var avg = row.AvgWeight;
        return new ProductionInfo
        {
            ProductionId = row.ProductionId,
            ProductName = string.IsNullOrWhiteSpace(row.ProductName) ? ProductionIdParts.DisplayArticle(article) : row.ProductName,
            StartTime = row.ProductionStart ?? parts.StartTime ?? row.SnapshotTime,
            LastSnapshot = row.SnapshotTime,
            IsTerminated = row.Terminated,
            TotalBags = row.TotalBags,
            BagsInTolerance = row.BagsInTolerance,
            BagsUnderTolerance = row.BagsUnderTolerance,
            BagsOverTolerance = row.BagsOverTolerance,
            TargetNetWeight = target,
            AvgWeight = avg.HasValue && target > 0 && avg >= 0.5 * target && avg <= 1.5 * target ? avg : null,
        };
    }

    // Hourly buckets for one machine (dashboard slides, on/off chart, team history).
    public Task<List<ProductionBucket>> GetLotsAsync(string machineId, DateTime from, DateTime to)
        => QueryBucketsAsync("p.numero_linea = @machineId", BucketSize.Hour, from, to, new { machineId = Line(machineId) });

    // Daily buckets for every machine of a sector (annual synthesis slides).
    public Task<List<ProductionBucket>> GetLotsForSectorAsync(string sector, DateTime from, DateTime to)
        => QueryBucketsAsync(
            "p.numero_linea IN (SELECT MachineId FROM Machines WHERE Sector = @sector)",
            BucketSize.Day, from, to, new { sector });

    // History browser (spec S6): one row per machine per hour that produced.
    // Poste is derived from the hour, so it's filtered here; team is filtered
    // by the caller (HistoryViewModel) via TeamScheduleService.
    public async Task<List<ProductionBucket>> GetLotsFilteredAsync(
        DateTime from, DateTime to, int? shiftId, string? machineId)
    {
        var buckets = string.IsNullOrEmpty(machineId)
            ? await QueryBucketsAsync("p.numero_linea IN (SELECT MachineId FROM Machines)", BucketSize.Hour, from, to, new { })
            : await QueryBucketsAsync("p.numero_linea = @machineId", BucketSize.Hour, from, to, new { machineId = Line(machineId) });

        return buckets
            .Where(b => b.IsProducing && (!shiftId.HasValue || b.ShiftId == shiftId.Value))
            .OrderByDescending(b => b.StartTime)
            .ThenBy(b => b.MachineId)
            .ToList();
    }

    private async Task<List<ProductionBucket>> QueryBucketsAsync(
        string lineFilter, BucketSize size, DateTime from, DateTime to, object filterParams)
    {
        var d = Dialect.For(_connectionFactory.Provider);
        var bucket = size == BucketSize.Hour ? d.Hour("x.mid_t") : d.Day("x.mid_t");

        var sql = $@"
WITH rng AS (
    SELECT p.numero_linea, p.ID_produzione, p.progressivo, p.tempo_inizio, p.tempo_fine,
           -- ID_produzione = line(4) + article(20) + start: fallback for a blank article column.
           COALESCE(NULLIF(RTRIM(p.codice_articolo), ''), SUBSTRING(p.ID_produzione, 5, 20)) AS codice_articolo,
           p.peso_netto_impostato AS target, p.media_totale AS media,
           COALESCE(p.totale_confezioni, 0)        AS tot,
           COALESCE(p.confezioni_sotto_toll, 0)    AS sotto,
           COALESCE(p.confezioni_ok, 0)            AS ok,
           COALESCE(p.confezioni_sopra_toll, 0)    AS sopra,
           COALESCE(p.confezioni_indeterminate, 0) AS indet,
           COALESCE(p.espulsi_metal, 0)            AS metal
    FROM produzione p
    WHERE {lineFilter}
      AND p.tempo_fine >= @readFrom AND p.tempo_fine < @readTo
),
lagged AS (
    SELECT r.*,
           LAG(r.tempo_fine) OVER (PARTITION BY r.numero_linea, r.ID_produzione ORDER BY r.progressivo) AS prev_fine,
           LAG(r.tot)        OVER (PARTITION BY r.numero_linea, r.ID_produzione ORDER BY r.progressivo) AS prev_tot,
           LAG(r.sotto)      OVER (PARTITION BY r.numero_linea, r.ID_produzione ORDER BY r.progressivo) AS prev_sotto,
           LAG(r.ok)         OVER (PARTITION BY r.numero_linea, r.ID_produzione ORDER BY r.progressivo) AS prev_ok,
           LAG(r.sopra)      OVER (PARTITION BY r.numero_linea, r.ID_produzione ORDER BY r.progressivo) AS prev_sopra,
           LAG(r.indet)      OVER (PARTITION BY r.numero_linea, r.ID_produzione ORDER BY r.progressivo) AS prev_indet,
           LAG(r.metal)      OVER (PARTITION BY r.numero_linea, r.ID_produzione ORDER BY r.progressivo) AS prev_metal
    FROM rng r
),
iv AS (
    SELECT l.*,
           COALESCE(l.prev_fine, l.tempo_inizio, l.tempo_fine) AS start_t,
           -- No earlier snapshot read: fine if the production started inside the
           -- read window (its counters ARE the delta), unknown baseline otherwise.
           CASE WHEN l.prev_tot IS NULL AND (l.tempo_inizio IS NULL OR l.tempo_inizio < @readFrom) THEN 0 ELSE 1 END AS known,
           -- Counter went backwards = machine reset: the new value is the delta.
           CASE WHEN l.prev_tot IS NULL OR l.tot < l.prev_tot THEN 1 ELSE 0 END AS restart
    FROM lagged l
),
dl AS (
    SELECT i.numero_linea, i.codice_articolo, i.target, i.media,
           {d.Midpoint("i.start_t", "i.tempo_fine")} AS mid_t,
           CASE WHEN i.tempo_fine > i.start_t THEN {d.Seconds("i.start_t", "i.tempo_fine")} ELSE 0 END AS dur,
           i.known * CASE WHEN i.restart = 1 THEN i.tot   ELSE i.tot   - i.prev_tot   END AS d_tot,
           i.known * CASE WHEN i.restart = 1 THEN i.sotto ELSE i.sotto - i.prev_sotto END AS d_sotto,
           i.known * CASE WHEN i.restart = 1 THEN i.ok    ELSE i.ok    - i.prev_ok    END AS d_ok,
           i.known * CASE WHEN i.restart = 1 THEN i.sopra ELSE i.sopra - i.prev_sopra END AS d_sopra,
           i.known * CASE WHEN i.restart = 1 THEN i.indet ELSE i.indet - i.prev_indet END AS d_indet,
           i.known * CASE WHEN i.restart = 1 THEN i.metal ELSE i.metal - i.prev_metal END AS d_metal
    FROM iv i
),
x AS (
    -- weighed = Stiavelli's running average looks sane (CC01 reports 2.9 g on 1000 g).
    SELECT dl.*,
           CASE WHEN dl.d_tot > 0 AND dl.target > 0
                 AND dl.media BETWEEN 0.5 * dl.target AND 1.5 * dl.target THEN 1 ELSE 0 END AS weighed
    FROM dl
)
SELECT a.*, pr.ProductName, CAST(COALESCE(pr.NominalRatePerMinute, 0) AS {d.Float}) AS NominalRatePerMinute
FROM (
    SELECT x.numero_linea                       AS MachineId,
           {bucket}                             AS StartTime,
           RTRIM(MAX(x.codice_articolo))        AS ProductCode,
           CAST(MAX(x.target) AS {d.Float})     AS TargetNetWeight,
           CAST(SUM(x.d_tot)   AS BIGINT)       AS TotalBags,
           CAST(SUM(x.d_sotto) AS BIGINT)       AS BagsUnderTolerance,
           CAST(SUM(x.d_ok)    AS BIGINT)       AS BagsInTolerance,
           CAST(SUM(x.d_sopra) AS BIGINT)       AS BagsOverTolerance,
           CAST(SUM(x.d_indet) AS BIGINT)       AS BagsIndeterminate,
           CAST(SUM(x.d_metal) AS BIGINT)       AS MetalRejects,
           CAST(SUM(x.dur) AS {d.Float}) / 60   AS ObservedMinutes,
           CAST(SUM(CASE WHEN x.d_tot > 0 THEN x.dur ELSE 0 END) AS {d.Float}) / 60 AS RunMinutes,
           CAST(SUM(CASE WHEN x.weighed = 1 THEN x.d_tot ELSE 0 END) AS BIGINT)                AS WeighedBags,
           CAST(SUM(CASE WHEN x.weighed = 1 THEN x.media * x.d_tot ELSE 0 END) AS {d.Float})    AS WeighedWeightSum,
           CAST(SUM(CASE WHEN x.weighed = 1 THEN (x.media - x.target) * x.d_tot ELSE 0 END) AS {d.Float}) AS WeighedDeviationSum
    FROM x
    WHERE x.mid_t >= @from AND x.mid_t < @to
    GROUP BY x.numero_linea, {bucket}
) a
LEFT JOIN Products pr ON pr.Code = a.ProductCode
ORDER BY a.StartTime, a.MachineId";

        var parameters = new DynamicParameters(filterParams);
        parameters.Add("from", from);
        parameters.Add("to", to);
        parameters.Add("readFrom", from - SnapshotMargin);
        parameters.Add("readTo", to + SnapshotMargin);

        using var connection = _connectionFactory.CreateConnection();
        var buckets = (await connection.QueryAsync<ProductionBucket>(sql, parameters)).ToList();

        var length = size == BucketSize.Hour ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        foreach (var b in buckets)
        {
            // CHAR(4) numero_linea comes back padded if a code is ever shorter.
            b.MachineId = b.MachineId.TrimEnd();
            b.EndTime = b.StartTime + length;
            b.ShiftId = KpiCalculator.CurrentShiftId(b.StartTime);
            // A per-article rate in Products wins; otherwise use the per-weight
            // table from Parametres.
            if (b.NominalRatePerMinute <= 0)
                b.NominalRatePerMinute = _settings.FindNominalRate(b.TargetNetWeight);
            // Products row missing or unnamed: show the article code without
            // its zero padding (00000000NOUVELLEPROD -> NOUVELLEPROD).
            if (string.IsNullOrWhiteSpace(b.ProductName))
                b.ProductName = b.ProductCode.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : b.ProductCode;
        }
        return buckets;
    }

    // numero_linea is CHAR(4): send the id as varchar so SQL Server can seek
    // the index instead of converting every row to nvarchar.
    private static DbString Line(string machineId) => new() { Value = machineId, IsAnsi = true, Length = 10 };

    private sealed record Dialect(
        Func<string, string, string> Seconds,
        Func<string, string, string> Midpoint,
        Func<string, string> Hour,
        Func<string, string> Day,
        string Float,
        string Now,
        string NowMinus6Hours,
        string Top2,
        string Limit2)
    {
        private static readonly Dialect SqlServer = new(
            (a, b) => $"DATEDIFF(SECOND, {a}, {b})",
            (a, b) => $"DATEADD(SECOND, DATEDIFF(SECOND, {a}, {b}) / 2, {a})",
            x => $"DATEADD(HOUR, DATEDIFF(HOUR, 0, {x}), 0)",
            x => $"DATEADD(DAY, DATEDIFF(DAY, 0, {x}), 0)",
            "FLOAT",
            "GETDATE()",
            "DATEADD(HOUR, -6, GETDATE())",
            "TOP 2",
            "");

        // Dev DB: timestamps stored as 'yyyy-MM-dd HH:mm:ss' text.
        private static readonly Dialect Sqlite = new(
            (a, b) => $"CAST(ROUND((julianday({b}) - julianday({a})) * 86400) AS INTEGER)",
            (a, b) => $"datetime((julianday({a}) + julianday({b})) / 2)",
            x => $"strftime('%Y-%m-%d %H:00:00', {x})",
            x => $"strftime('%Y-%m-%d 00:00:00', {x})",
            "REAL",
            "datetime('now', 'localtime')",
            "datetime('now', 'localtime', '-6 hours')",
            "",
            "LIMIT 2");

        public static Dialect For(DatabaseProvider provider)
            => provider == DatabaseProvider.SqlServer ? SqlServer : Sqlite;
    }

    public async Task<NoteEntry?> GetActiveNoteAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<NoteEntry>(
            @"SELECT NoteId, Content, CreatedByEmployeeId, CreatedAt, IsActive FROM Notes
              WHERE IsActive = 1 AND CreatedAt = (SELECT MAX(CreatedAt) FROM Notes WHERE IsActive = 1)");
    }

    public async Task SetActiveNoteAsync(string content)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var employeeId = await connection.ExecuteScalarAsync<int?>(
            "SELECT MIN(EmployeeId) FROM Employees", transaction: transaction) ?? 1;

        await connection.ExecuteAsync("UPDATE Notes SET IsActive = 0 WHERE IsActive = 1", transaction: transaction);
        await connection.ExecuteAsync(
            @"INSERT INTO Notes (Content, CreatedByEmployeeId, CreatedAt, IsActive)
              VALUES (@content, @employeeId, @createdAt, 1)",
            new { content, employeeId, createdAt = DateTime.Now }, transaction: transaction);

        transaction.Commit();
    }

    public async Task UpdateMachineNameAsync(string machineId, string machineName)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE Machines SET MachineName = @machineName WHERE MachineId = @machineId",
            new { machineId, machineName });
    }
}
