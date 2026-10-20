using System.Data;
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
//   - turns consecutive snapshots into per-interval deltas and sums them
//     into hour/day buckets ON THE SERVER, so what reaches the app doesn't
//     grow with the snapshot frequency.
// Each interval runs from the LINE's previous snapshot to this one and is
// attributed to the bucket containing its midpoint. Deltas are computed per
// ID_produzione (one article run on one line; a new one is opened at each
// changeover, so a new production simply starts again from zero), against
// the running max of each counter, so StatsViewer glitches (all-zero rows,
// counters stepping back) never count twice.
//
// Weights come from Stiavelli's lotto table (real lot averages) when the
// line has lots in the range, otherwise from produzione.media_totale when it
// looks sane (see LoadWeightPartsAsync).
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

    // Longest snapshot interval that is counted. The cadence is 20 s - 2 min
    // today (30 min historically), so a longer interval means StatsViewer was
    // silent and nobody knows when its bags were made: the whole interval
    // (bags and time) is dropped. Keeping it put e.g. CC01's 6.9 M bags of
    // 08/09 -> 06/10 or PL03's 1.7 M of 14/03 -> 20/10 into a single hour.
    private const int MaxIntervalSeconds = 7200;

    // Faster than this over an interval's real elapsed time (at least a
    // minute) is a counter glitch, not production: the interval's deltas are
    // dropped. No line here comes anywhere near 300 bags/min.
    private const int MaxBagsPerMinute = 300;

    // Latest snapshots read for the line status: enough to reach the current
    // production's previous snapshot past duplicates and stray rows.
    private const int StatusRows = 60;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly AppSettings _settings;

    // Whether Stiavelli's lotto table is readable (null = not checked yet).
    // The SQLite dev DB has none: weights then come from media_totale.
    private bool? _hasLotTable;

    public ProductionRepository(IDbConnectionFactory connectionFactory, AppSettings settings)
    {
        _connectionFactory = connectionFactory;
        _settings = settings;
    }

    // Drops pooled connections, which are all dead after a network/server drop
    // and would otherwise make the first queries after a reconnect fail again.
    // Settings may also point at another database now: look for lotto again.
    public void ResetConnections()
    {
        _hasLotTable = null;
        _connectionFactory.ResetPool();
    }

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
        public long Progressivo { get; set; }
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
        public double? MinWeight { get; set; }
        public double? MaxWeight { get; set; }
        public DateTime ServerNow { get; set; }

        // ID_produzione = line + article + start: fallback for a blank tempo_inizio.
        public DateTime Start => ProductionStart ?? ProductionIdParts.Parse(ProductionId).StartTime ?? SnapshotTime;
    }

    // Connected = the line's latest valid snapshot is recent, judged against
    // the line's own cadence (2.5x the latest non-zero gap between its
    // snapshots, 2 min to 2 h) and the SERVER clock, so a drifting kiosk clock
    // can't flag lines as disconnected when snapshots are seconds apart. Belt
    // running = the counter moved since the production's previous snapshot.
    // Alarm: no source, always false. Also returns the line's current
    // production (its latest valid snapshot).
    public async Task<MachineStatusSnapshot?> GetLatestStatusAsync(string machineId)
    {
        var d = Dialect.For(_connectionFactory.Provider);
        using var connection = _connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<SnapshotRow>(
            $@"SELECT {d.Top(StatusRows)} RTRIM(p.ID_produzione) AS ProductionId, p.progressivo AS Progressivo,
                      p.tempo_fine AS SnapshotTime, p.tempo_inizio AS ProductionStart, COALESCE(p.terminata, 0) AS Terminated,
                      RTRIM(p.codice_articolo) AS ArticleCode, pr.ProductName,
                      COALESCE(p.totale_confezioni, 0)     AS TotalBags,
                      COALESCE(p.confezioni_ok, 0)         AS BagsInTolerance,
                      COALESCE(p.confezioni_sotto_toll, 0) AS BagsUnderTolerance,
                      COALESCE(p.confezioni_sopra_toll, 0) AS BagsOverTolerance,
                      CAST(p.media_totale AS {d.Float})         AS AvgWeight,
                      CAST(p.peso_netto_impostato AS {d.Float}) AS TargetNetWeight,
                      CAST(p.peso_min_registrato AS {d.Float})  AS MinWeight,
                      CAST(p.peso_max_registrato AS {d.Float})  AS MaxWeight,
                      {d.Now} AS ServerNow
               FROM produzione p
               LEFT JOIN Products pr ON pr.Code = RTRIM(p.codice_articolo)
               WHERE p.numero_linea = @machineId AND p.tempo_fine >= {d.NowMinus6Hours}
               ORDER BY p.tempo_fine DESC {d.Limit(StatusRows)}",
            new { machineId = Line(machineId) }))
            // Ties on tempo_fine (duplicate rows happen) are broken here, so SQL
            // Server reads the index backwards instead of sorting the 6 h range.
            .OrderByDescending(r => r.SnapshotTime)
            .ThenByDescending(r => r.Progressivo)
            .ToList();

        if (rows.Count == 0)
            return new MachineStatusSnapshot { MachineId = machineId, IsConnected = false };

        // Never empty in practice: the production with the latest start always keeps a row.
        var valid = rows.Where(r => !IsStray(r, rows)).ToList();
        var latest = valid.FirstOrDefault() ?? rows[0];

        TimeSpan window;
        if (_settings.ConnectedWindowMinutes > 0)
        {
            window = TimeSpan.FromMinutes(_settings.ConnectedWindowMinutes);
        }
        else
        {
            // Duplicate rows share a tempo_fine: skip zero gaps.
            var gap = valid.Zip(valid.Skip(1), (a, b) => a.SnapshotTime - b.SnapshotTime)
                .FirstOrDefault(g => g > TimeSpan.Zero);
            window = gap > TimeSpan.Zero
                ? TimeSpan.FromSeconds(Math.Clamp(2.5 * gap.TotalSeconds, 120, 7200))
                : TimeSpan.FromHours(1);
        }

        var connected = latest.ServerNow - latest.SnapshotTime <= window;
        // Counter moved since the same production's previous snapshot. A
        // production with a single snapshot so far only counts as running if
        // it started within the window (otherwise its bags are old news).
        var previous = valid.FirstOrDefault(r => r.ProductionId == latest.ProductionId && r.SnapshotTime < latest.SnapshotTime);
        var producing = previous != null
            ? latest.TotalBags > previous.TotalBags
            : latest.TotalBags > 0 && latest.ServerNow - latest.Start <= window;

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

    // StatsViewer glitches: on reconnect it writes one row of a PREVIOUS
    // production of the line with every counter at 0 (tempo_inizio months
    // old, tempo_fine = now), and it has written all-zero rows for current
    // runs too. A zero row is ignored when its production shows bags in
    // another row, or when a production started later exists; a run that
    // really just started (still at 0, latest start) is kept.
    private static bool IsStray(SnapshotRow row, List<SnapshotRow> rows)
        => row.TotalBags == 0
           && rows.Any(o => o.ProductionId == row.ProductionId ? o.TotalBags > 0 : o.Start > row.Start);

    private static ProductionInfo ToProductionInfo(SnapshotRow row)
    {
        // ID_produzione = line + article + start: fallback for blank columns.
        var parts = ProductionIdParts.Parse(row.ProductionId);
        var article = string.IsNullOrWhiteSpace(row.ArticleCode) ? parts.ArticleCode : row.ArticleCode;
        var target = row.TargetNetWeight ?? 0;
        return new ProductionInfo
        {
            ProductionId = row.ProductionId,
            ProductName = string.IsNullOrWhiteSpace(row.ProductName) ? ProductionIdParts.DisplayArticle(article) : row.ProductName,
            StartTime = row.Start,
            LastSnapshot = row.SnapshotTime,
            IsTerminated = row.Terminated,
            TotalBags = row.TotalBags,
            BagsInTolerance = row.BagsInTolerance,
            BagsUnderTolerance = row.BagsUnderTolerance,
            BagsOverTolerance = row.BagsOverTolerance,
            TargetNetWeight = target,
            AvgWeight = IsPlausibleRunAverage(row.AvgWeight, target) && IsPlausibleExtremes(target, row.MinWeight, row.MaxWeight)
                ? row.AvgWeight
                : null,
        };
    }

    // media_totale is a running average since production START and is junk on
    // some runs (CC01: 976.8 g on an 800 g target, min 449 g, max 7483 g). It
    // is only trusted within 15 % of the target (same bounds as MediaBags in
    // QueryBucketsAsync) and when the run's lifetime extremes look like real
    // bags of that target.
    private static bool IsPlausibleRunAverage(double? avg, double target)
        => avg.HasValue && target > 0 && Math.Abs(avg.Value - target) <= 0.15 * target;

    private static bool IsPlausibleExtremes(double target, double? min, double? max)
        => target > 0 && max.HasValue && max.Value <= 2 * target && min.HasValue && min.Value >= 0.5 * target;

    // Hourly buckets for one machine (dashboard slides, on/off chart, team
    // history). includeWeights = false skips the weight reads (lotto) for the
    // charts that don't show weights.
    public Task<List<ProductionBucket>> GetLotsAsync(string machineId, DateTime from, DateTime to, bool includeWeights = true)
        => QueryBucketsAsync("{0} = @machineId", BucketSize.Hour, from, to, new { machineId = Line(machineId) }, includeWeights);

    // Daily buckets for every machine of a sector (annual synthesis slides).
    public Task<List<ProductionBucket>> GetLotsForSectorAsync(string sector, DateTime from, DateTime to)
        => QueryBucketsAsync(
            "{0} IN (SELECT MachineId FROM Machines WHERE Sector = @sector)",
            BucketSize.Day, from, to, new { sector }, includeWeights: true);

    // History browser (spec S6): one row per machine per hour that produced.
    // Poste is derived from the hour, so it's filtered here; team is filtered
    // by the caller (HistoryViewModel) via TeamScheduleService.
    public async Task<List<ProductionBucket>> GetLotsFilteredAsync(
        DateTime from, DateTime to, int? shiftId, string? machineId)
    {
        var buckets = string.IsNullOrEmpty(machineId)
            ? await QueryBucketsAsync("{0} IN (SELECT MachineId FROM Machines)", BucketSize.Hour, from, to, new { }, includeWeights: true)
            : await QueryBucketsAsync("{0} = @machineId", BucketSize.Hour, from, to, new { machineId = Line(machineId) }, includeWeights: true);

        return buckets
            .Where(b => b.IsProducing && (!shiftId.HasValue || b.ShiftId == shiftId.Value))
            .OrderByDescending(b => b.StartTime)
            .ThenBy(b => b.MachineId)
            .ToList();
    }

    // One production's share of one bucket, as aggregated by the server.
    private sealed class BucketPartRow
    {
        public string MachineId { get; set; } = "";
        public DateTime StartTime { get; set; }
        public string ProductionId { get; set; } = "";
        public string ProductCode { get; set; } = "";
        public string? ProductName { get; set; }
        public double ArticleRate { get; set; }
        public double TargetNetWeight { get; set; }
        public long TotalBags { get; set; }
        public long BagsUnderTolerance { get; set; }
        public long BagsInTolerance { get; set; }
        public long BagsOverTolerance { get; set; }
        public long BagsIndeterminate { get; set; }
        public long MetalRejects { get; set; }
        public double ObservedMinutes { get; set; }
        public double RunMinutes { get; set; }
        public long MediaBags { get; set; }
        public double MediaWeightSum { get; set; }
    }

    private static readonly string[] Counters = { "tot", "sotto", "ok", "sopra", "indet", "metal" };

    // lineFilter: SQL condition on the line code, {0} = the column/expression holding it.
    private async Task<List<ProductionBucket>> QueryBucketsAsync(
        string lineFilter, BucketSize size, DateTime from, DateTime to, object filterParams, bool includeWeights)
    {
        // Nothing before DataStartDate is read, not even as the baseline of the
        // first interval: that interval is then dropped (unknown baseline).
        var dataStart = _settings.DataStartDate;
        if (from < dataStart)
            from = dataStart;
        if (to <= from)
            return new List<ProductionBucket>();

        var d = Dialect.For(_connectionFactory.Provider);
        var bucket = size == BucketSize.Hour ? d.Hour("x.mid_t") : d.Day("x.mid_t");
        const string byProduction = "PARTITION BY r.numero_linea, r.ID_produzione ORDER BY r.tempo_fine, r.progressivo";

        var previous = string.Join(",\n", Counters.Select(c =>
            $"           LAG(r.{c}) OVER ({byProduction}) AS prev_{c}"));
        var deltas = string.Join(",\n", Counters.Select(c =>
            $"           g.known * CASE WHEN g.prev_tot IS NULL OR g.restart = 1 THEN g.{c}"
            + $" WHEN g.{c} > g.prev_{c} THEN g.{c} - g.prev_{c} ELSE 0 END AS d_{c}"));
        var saneDeltas = string.Join(",\n", Counters.Select(c => $"           sc.sane * sc.d_{c} AS d_{c}"));

        var sql = $@"
WITH src AS (
    SELECT p.numero_linea, p.ID_produzione, p.progressivo, p.tempo_inizio, p.tempo_fine,
           -- ID_produzione = line(4) + article(20) + start: fallback for a blank article column.
           COALESCE(NULLIF(RTRIM(p.codice_articolo), ''), SUBSTRING(p.ID_produzione, 5, 20)) AS codice_articolo,
           p.peso_netto_impostato AS target, CAST(p.media_totale AS {d.Float}) AS media,
           COALESCE(p.totale_confezioni, 0)        AS tot,
           COALESCE(p.confezioni_sotto_toll, 0)    AS sotto,
           COALESCE(p.confezioni_ok, 0)            AS ok,
           COALESCE(p.confezioni_sopra_toll, 0)    AS sopra,
           COALESCE(p.confezioni_indeterminate, 0) AS indet,
           COALESCE(p.espulsi_metal, 0)            AS metal
    FROM produzione p
    WHERE {string.Format(lineFilter, "p.numero_linea")}
      AND p.tempo_fine >= @readFrom AND p.tempo_fine < @readTo
),
rng AS (
    -- Stray rows: on reconnect StatsViewer writes one row of a PREVIOUS
    -- production with every counter at 0. Dropped when that production
    -- started before the read window and a later one exists on the line; a
    -- run that really starts at 0 inside the window is kept.
    SELECT s.*
    FROM (SELECT src.*, MAX(src.tempo_inizio) OVER (PARTITION BY src.numero_linea) AS line_last_start FROM src) s
    WHERE CASE WHEN s.tot = 0 AND s.tempo_inizio < @readFrom AND s.tempo_inizio < s.line_last_start THEN 1 ELSE 0 END = 0
),
nz AS (
    SELECT r.*, MAX(r.tot) OVER ({byProduction} ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS seen_max
    FROM rng r
),
rz AS (
    -- All-zero glitch rows of a production that already showed bags are
    -- dropped, so the next real snapshot is compared with the last real one.
    SELECT * FROM nz WHERE NOT (nz.tot = 0 AND nz.seen_max > 0)
),
win AS (
    -- Baseline of each counter = the production's previous snapshot (ordered
    -- by tempo_fine, then progressivo: duplicate rows happen). A counter that
    -- steps back contributes 0 and counting resumes from the new value: PL03
    -- once fell 3 375 bags (1 710 281 -> 1 706 906) after a StatsViewer
    -- reconnect, then the machine kept counting up from 1 706 906.
    SELECT r.*,
{previous},
           -- Interval = since the LINE's previous snapshot, whatever its
           -- production: one timeline per line, so observed time is never
           -- counted twice when two productions overlap.
           LAG(r.tempo_fine) OVER (PARTITION BY r.numero_linea ORDER BY r.tempo_fine, r.progressivo) AS line_prev_fine
    FROM rz r
),
flg AS (
    SELECT w.*,
           -- No earlier snapshot read: fine if the production started inside the
           -- read window (its counters ARE the delta), unknown baseline otherwise.
           CASE WHEN w.prev_tot IS NULL AND (w.tempo_inizio IS NULL OR w.tempo_inizio < @readFrom) THEN 0 ELSE 1 END AS known,
           -- Machine reset = the total falls under half the previous one: the new
           -- values are the delta. A smaller step back is a glitch (counts 0).
           CASE WHEN 2 * w.tot < w.prev_tot THEN 1 ELSE 0 END AS restart,
           COALESCE(w.line_prev_fine,
                    CASE WHEN w.prev_tot IS NULL AND w.tempo_inizio >= @readFrom THEN w.tempo_inizio END) AS start_t
    FROM win w
),
g AS (
    SELECT f.*,
           CASE WHEN f.start_t IS NOT NULL AND f.tempo_fine > f.start_t THEN {d.Seconds("f.start_t", "f.tempo_fine")} ELSE 0 END AS gap
    FROM flg f
),
dl AS (
    SELECT g.numero_linea, g.ID_produzione, g.codice_articolo, g.target, g.media, g.tempo_fine, g.gap,
           -- Observed time: 0 without a known baseline or past MaxIntervalSeconds.
           g.known * CASE WHEN g.gap > {MaxIntervalSeconds} THEN 0 ELSE g.gap END AS dur,
{deltas}
    FROM g
),
sc AS (
    -- Interval too long to place in time (MaxIntervalSeconds), or faster than
    -- MaxBagsPerMinute over its real elapsed time (at least a minute): dropped.
    SELECT dl.*,
           CASE WHEN dl.gap > {MaxIntervalSeconds} THEN 0
                WHEN CAST(dl.d_tot AS {d.Float}) * 60
                     > {MaxBagsPerMinute} * CAST(CASE WHEN dl.gap < 60 THEN 60 ELSE dl.gap END AS {d.Float})
                THEN 0 ELSE 1 END AS sane
    FROM dl
),
x AS (
    -- Attributed to the bucket containing the midpoint of the interval.
    SELECT sc.numero_linea, sc.ID_produzione, sc.codice_articolo, sc.target, sc.media, sc.dur,
           {d.MinusSeconds("sc.tempo_fine", "sc.dur / 2")} AS mid_t,
{saneDeltas}
    FROM sc
)
SELECT a.*, pr.ProductName, CAST(COALESCE(pr.NominalRatePerMinute, 0) AS {d.Float}) AS ArticleRate
FROM (
    -- One row per line, bucket AND production: rate and weight figures must
    -- use each production's own target, never a MAX across two articles.
    SELECT x.numero_linea                       AS MachineId,
           {bucket}                             AS StartTime,
           RTRIM(x.ID_produzione)               AS ProductionId,
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
           -- Fallback weight data (used only when the line has no lot): bags
           -- whose running average is within 15 % of the target.
           CAST(SUM(CASE WHEN x.target > 0 AND x.media >= 0.85 * x.target AND x.media <= 1.15 * x.target
                         THEN x.d_tot ELSE 0 END) AS BIGINT) AS MediaBags,
           CAST(SUM(CASE WHEN x.target > 0 AND x.media >= 0.85 * x.target AND x.media <= 1.15 * x.target
                         THEN x.media * x.d_tot ELSE 0 END) AS {d.Float}) AS MediaWeightSum
    FROM x
    WHERE x.mid_t >= @from AND x.mid_t < @to
    GROUP BY x.numero_linea, {bucket}, x.ID_produzione
) a
LEFT JOIN Products pr ON pr.Code = a.ProductCode";

        var parameters = new DynamicParameters(filterParams);
        parameters.Add("from", from);
        parameters.Add("to", to);
        parameters.Add("readFrom", from - SnapshotMargin < dataStart ? dataStart : from - SnapshotMargin);
        parameters.Add("readTo", to + SnapshotMargin);

        using var connection = _connectionFactory.CreateConnection();
        var parts = (await connection.QueryAsync<BucketPartRow>(sql, parameters)).ToList();
        foreach (var p in parts)
            // CHAR(4) numero_linea comes back padded if a code is ever shorter.
            p.MachineId = p.MachineId.TrimEnd();

        var weights = includeWeights
            ? await LoadWeightPartsAsync(connection, d, lineFilter, size, parameters, parts)
            : new List<(string MachineId, DateTime StartTime, WeightPart Part)>();

        var length = size == BucketSize.Hour ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        var partsByKey = parts.ToLookup(p => (p.MachineId, p.StartTime));
        var weightsByKey = weights.ToLookup(w => (w.MachineId, w.StartTime), w => w.Part);
        var keys = partsByKey.Select(g => g.Key)
            .Union(weightsByKey.Select(g => g.Key))
            .OrderBy(k => k.StartTime)
            .ThenBy(k => k.MachineId);

        var buckets = new List<ProductionBucket>();
        foreach (var key in keys)
        {
            var group = partsByKey[key].ToList();
            // Display only: the production that made the most bags in the bucket.
            var main = group.OrderByDescending(p => p.TotalBags).ThenByDescending(p => p.RunMinutes).FirstOrDefault();
            var b = new ProductionBucket
            {
                MachineId = key.MachineId,
                StartTime = key.StartTime,
                EndTime = key.StartTime + length,
                ShiftId = KpiCalculator.CurrentShiftId(key.StartTime),
                ProductCode = main?.ProductCode ?? "",
                // Products row missing or unnamed: show the article code without
                // its zero padding (00000000NOUVELLEPROD -> NOUVELLEPROD).
                ProductName = main == null ? null
                    : string.IsNullOrWhiteSpace(main.ProductName) ? ProductionIdParts.DisplayArticle(main.ProductCode) : main.ProductName,
                TargetNetWeight = main?.TargetNetWeight ?? 0,
                TotalBags = group.Sum(p => p.TotalBags),
                BagsUnderTolerance = group.Sum(p => p.BagsUnderTolerance),
                BagsInTolerance = group.Sum(p => p.BagsInTolerance),
                BagsOverTolerance = group.Sum(p => p.BagsOverTolerance),
                BagsIndeterminate = group.Sum(p => p.BagsIndeterminate),
                MetalRejects = group.Sum(p => p.MetalRejects),
                ObservedMinutes = group.Sum(p => p.ObservedMinutes),
                RunMinutes = group.Sum(p => p.RunMinutes),
            };

            // Performance inputs, each production at its own nominal rate: a
            // per-article rate in Products wins, otherwise the per-weight table
            // from Parametres. Unknown rate (0) = left out of both sides.
            foreach (var p in group.Where(p => p.TotalBags > 0))
            {
                var rate = p.ArticleRate > 0 ? p.ArticleRate : _settings.FindNominalRate(p.TargetNetWeight);
                if (rate <= 0)
                    continue;
                b.IdealMinutes += p.TotalBags / rate;
                b.RatedRunMinutes += p.RunMinutes;
            }

            b.WeightParts.AddRange(weightsByKey[key]);
            buckets.Add(b);
        }
        return buckets;
    }

    private sealed class LotRow
    {
        public string MachineId { get; set; } = "";
        public string ProductionId { get; set; } = "";
        public DateTime StartTime { get; set; }
        public long Bags { get; set; }
        public double WeightSum { get; set; }
    }

    private sealed class ExtremesRow
    {
        public string ProductionId { get; set; } = "";
        public double? MinWeight { get; set; }
        public double? MaxWeight { get; set; }
    }

    // Weight data per (line, bucket, production):
    //  - Stiavelli lots (lotto): media_effettiva weighted by dimensione,
    //    bucketed by ora_fine (lots can be uploaded late, ora_fine is the real
    //    lot time), for every line that has lots in the range;
    //  - otherwise media_totale, only where it is within 15 % of the target
    //    (MediaBags) and the production's lifetime extremes look sane.
    // A lot's target is its production's peso_netto_impostato; lots of a
    // production with no snapshot in the read range are left out. Line =
    // first 4 characters of ID_produzione (odd codes like "PL 3" match no machine).
    private async Task<List<(string MachineId, DateTime StartTime, WeightPart Part)>> LoadWeightPartsAsync(
        IDbConnection connection, Dialect d, string lineFilter, BucketSize size, DynamicParameters parameters, List<BucketPartRow> parts)
    {
        var result = new List<(string MachineId, DateTime StartTime, WeightPart Part)>();
        var targets = parts.Where(p => p.TargetNetWeight > 0)
            .GroupBy(p => p.ProductionId)
            .ToDictionary(g => g.Key, g => g.Max(p => p.TargetNetWeight));

        var linesWithLots = new HashSet<string>();
        if (await HasLotTableAsync(connection, d))
        {
            const string lineOfLot = "SUBSTRING(l.ID_produzione, 1, 4)";
            var bucket = size == BucketSize.Hour ? d.Hour("l.ora_fine") : d.Day("l.ora_fine");
            var lots = await connection.QueryAsync<LotRow>($@"
SELECT {lineOfLot} AS MachineId, RTRIM(l.ID_produzione) AS ProductionId, {bucket} AS StartTime,
       CAST(SUM(l.dimensione) AS BIGINT) AS Bags,
       CAST(SUM(CAST(l.media_effettiva AS {d.Float}) * l.dimensione) AS {d.Float}) AS WeightSum
FROM lotto l
WHERE l.ora_fine >= @from AND l.ora_fine < @to
  AND l.dimensione > 0 AND l.media_effettiva > 0
  AND {string.Format(lineFilter, lineOfLot)}
GROUP BY {lineOfLot}, RTRIM(l.ID_produzione), {bucket}", parameters);

            foreach (var lot in lots)
            {
                var machineId = lot.MachineId.TrimEnd();
                if (!targets.TryGetValue(lot.ProductionId, out var target))
                    continue;
                linesWithLots.Add(machineId);
                result.Add((machineId, lot.StartTime, new WeightPart(lot.ProductionId, target, lot.Bags, lot.WeightSum)));
            }
        }

        var fallback = parts.Where(p => p.MediaBags > 0 && !linesWithLots.Contains(p.MachineId)).ToList();
        if (fallback.Count == 0)
            return result;

        // Lifetime extremes are only read when the fallback is needed: these
        // columns aren't in IX_produzione_linea_fine, each row costs a lookup.
        parameters.Add("fallbackLines", fallback.Select(p => p.MachineId).Distinct().ToList());
        var extremes = (await connection.QueryAsync<ExtremesRow>($@"
SELECT RTRIM(p.ID_produzione) AS ProductionId,
       CAST(MIN(p.peso_min_registrato) AS {d.Float}) AS MinWeight,
       CAST(MAX(p.peso_max_registrato) AS {d.Float}) AS MaxWeight
FROM produzione p
WHERE {string.Format(lineFilter, "p.numero_linea")}
  AND p.numero_linea IN @fallbackLines
  AND p.tempo_fine >= @readFrom AND p.tempo_fine < @readTo
GROUP BY p.ID_produzione", parameters))
            .GroupBy(e => e.ProductionId)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var p in fallback)
        {
            if (extremes.TryGetValue(p.ProductionId, out var e) && IsPlausibleExtremes(p.TargetNetWeight, e.MinWeight, e.MaxWeight))
                result.Add((p.MachineId, p.StartTime, new WeightPart(p.ProductionId, p.TargetNetWeight, p.MediaBags, p.MediaWeightSum)));
        }
        return result;
    }

    // Checked once per connection setup (ResetConnections clears it): the
    // SQLite dev DB has no lotto, and a login without rights on it sees none.
    private async Task<bool> HasLotTableAsync(IDbConnection connection, Dialect d)
    {
        if (_hasLotTable is { } known)
            return known;
        var exists = await connection.ExecuteScalarAsync<int>(d.LotTableExists) > 0;
        if (!exists)
            AppLogger.Log("Table lotto introuvable : poids calcules depuis media_totale (filtre).");
        _hasLotTable = exists;
        return exists;
    }

    // numero_linea is CHAR(4): send the id as varchar so SQL Server can seek
    // the index instead of converting every row to nvarchar.
    private static DbString Line(string machineId) => new() { Value = machineId, IsAnsi = true, Length = 10 };

    private sealed record Dialect(
        Func<string, string, string> Seconds,
        Func<string, string, string> MinusSeconds,
        Func<string, string> Hour,
        Func<string, string> Day,
        string Float,
        string Now,
        string NowMinus6Hours,
        Func<int, string> Top,
        Func<int, string> Limit,
        string LotTableExists)
    {
        private static readonly Dialect SqlServer = new(
            (a, b) => $"DATEDIFF(SECOND, {a}, {b})",
            (x, s) => $"DATEADD(SECOND, -({s}), {x})",
            x => $"DATEADD(HOUR, DATEDIFF(HOUR, 0, {x}), 0)",
            x => $"DATEADD(DAY, DATEDIFF(DAY, 0, {x}), 0)",
            "FLOAT",
            "GETDATE()",
            "DATEADD(HOUR, -6, GETDATE())",
            n => $"TOP {n}",
            _ => "",
            "SELECT CASE WHEN OBJECT_ID('lotto') IS NULL THEN 0 ELSE 1 END");

        // Dev DB: timestamps stored as 'yyyy-MM-dd HH:mm:ss' text.
        private static readonly Dialect Sqlite = new(
            (a, b) => $"CAST(ROUND((julianday({b}) - julianday({a})) * 86400) AS INTEGER)",
            (x, s) => $"datetime(julianday({x}) - ({s}) / 86400.0)",
            x => $"strftime('%Y-%m-%d %H:00:00', {x})",
            x => $"strftime('%Y-%m-%d 00:00:00', {x})",
            "REAL",
            "datetime('now', 'localtime')",
            "datetime('now', 'localtime', '-6 hours')",
            _ => "",
            n => $"LIMIT {n}",
            "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table', 'view') AND name = 'lotto'");

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
