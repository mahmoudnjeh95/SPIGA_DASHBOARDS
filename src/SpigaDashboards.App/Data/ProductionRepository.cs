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

    // Shortest snapshot interval used for the max speed: under 20 s, one bag
    // more or less on the counter swings the rate too much to mean anything.
    private const int MinSpeedIntervalSeconds = 20;

    // Latest snapshots read for the line status: enough to reach the current
    // production's previous snapshot past duplicates and stray rows.
    private const int StatusRows = 60;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly AppSettings _settings;

    // Planned stops (maintenance slots, "Planifié" stops) left out of the TRS.
    private readonly PlanningRepository _planning;

    // Whether Stiavelli's lotto table is readable (null = not checked yet).
    // The SQLite dev DB has none: weights then come from media_totale.
    private bool? _hasLotTable;

    public ProductionRepository(IDbConnectionFactory connectionFactory, AppSettings settings)
    {
        _connectionFactory = connectionFactory;
        _settings = settings;
        _planning = new PlanningRepository(connectionFactory);
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
            CurrentProduction = ToProductionInfo(latest, _settings.FindTare(machineId, latest.TargetNetWeight ?? 0)),
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

    // tare = empty bag weight (Parametres), deducted from the recorded average.
    private static ProductionInfo ToProductionInfo(SnapshotRow row, double tare)
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
                ? row.AvgWeight - tare
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

    // Buckets for a set of machines (synthesis slides, history, exports). Day
    // buckets are lighter for long ranges but carry no poste (ShiftId 0):
    // callers filtering by poste / team need hour buckets.
    public Task<List<ProductionBucket>> GetLotsForMachinesAsync(
        IReadOnlyCollection<string> machineIds, DateTime from, DateTime to, BucketSize size = BucketSize.Hour)
    {
        if (machineIds.Count == 0)
            return Task.FromResult(new List<ProductionBucket>());
        // Compared through Machines (small table), so the produzione side
        // still seeks its index instead of converting numero_linea.
        return QueryBucketsAsync(
            "{0} IN (SELECT MachineId FROM Machines WHERE MachineId IN @machineIds)",
            size, from, to, new { machineIds = machineIds.ToList() }, includeWeights: true);
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
        public double PlannedStopMinutes { get; set; }
        public double MaxSpeed { get; set; }
        public long MediaBags { get; set; }
        public double MediaWeightSum { get; set; }
    }

    private static readonly string[] Counters = { "tot", "sotto", "ok", "sopra", "indet", "metal" };

    private const string ByProduction = "PARTITION BY r.numero_linea, r.ID_produzione ORDER BY r.tempo_fine, r.progressivo";

    // CTEs src .. rz: the snapshots of the lines matching lineFilter read in
    // [@readFrom, @readTo), cleaned of StatsViewer's glitch rows.
    private static string CleanSnapshotsSql(string lineFilter, Dialect d) => $@"src AS (
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
    SELECT r.*, MAX(r.tot) OVER ({ByProduction} ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS seen_max
    FROM rng r
),
rz AS (
    -- All-zero glitch rows of a production that already showed bags are
    -- dropped, so the next real snapshot is compared with the last real one.
    SELECT * FROM nz WHERE NOT (nz.tot = 0 AND nz.seen_max > 0)
)";

    // Seconds of an interval (ending at second endSecond of its day, lasting
    // duration seconds) inside a planned break. Intervals last at most
    // MaxIntervalSeconds, so each break is checked on the interval's own day
    // and the day before (an interval or a break across midnight).
    private string BreakOverlapSql(string endSecond, string duration)
    {
        static string Min(string a, string b) => $"CASE WHEN {a} < {b} THEN {a} ELSE {b} END";
        static string Max(string a, string b) => $"CASE WHEN {a} > {b} THEN {a} ELSE {b} END";

        var start = $"({endSecond} - {duration})";
        var terms = BreakSchedule.DayWindows(_settings.PlannedBreaks)
            .SelectMany(w => new[] { w, (Start: w.Start - BreakSchedule.SecondsPerDay, End: w.End - BreakSchedule.SecondsPerDay) })
            .Select(w =>
            {
                var overlap = $"{Min(endSecond, w.End.ToString())} - {Max(start, w.Start.ToString())}";
                return $"CASE WHEN {overlap} > 0 THEN {overlap} ELSE 0 END";
            })
            .ToList();
        return terms.Count == 0 ? "0" : "(" + string.Join("\n             + ", terms) + ")";
    }

    // Seconds of an interval (ending endRel seconds after @readFrom, lasting
    // duration seconds) inside a stop planned in the Planification window for
    // its line: maintenance slots and "Planifié" stops. The windows are merged
    // per line and cut out of the daily breaks first (those are in brk), so
    // no second is deducted twice.
    private string PlannedOverlapSql(
        IEnumerable<(string MachineId, DateTime Start, DateTime End)> windows, DateTime readFrom, DateTime readTo,
        string line, string endRel, string duration)
    {
        static string Min(string a, string b) => $"CASE WHEN {a} < {b} THEN {a} ELSE {b} END";
        static string Max(string a, string b) => $"CASE WHEN {a} > {b} THEN {a} ELSE {b} END";

        var breaks = BreakSchedule.DayWindows(_settings.PlannedBreaks);
        var start = $"({endRel} - {duration})";
        var cases = new List<string>();
        foreach (var g in windows.GroupBy(w => w.MachineId.Trim()))
        {
            // Codes go into the SQL text: Machines ids only (CC01, PL03, ...).
            if (g.Key.Length == 0 || !g.Key.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' '))
                continue;
            var merged = new List<(DateTime Start, DateTime End)>();
            foreach (var (_, s, e) in g.OrderBy(w => w.Start))
            {
                var (cs, ce) = (s < readFrom ? readFrom : s, e > readTo ? readTo : e);
                if (ce <= cs)
                    continue;
                if (merged.Count > 0 && cs <= merged[^1].End)
                    merged[^1] = (merged[^1].Start, ce > merged[^1].End ? ce : merged[^1].End);
                else
                    merged.Add((cs, ce));
            }
            var terms = merged
                .SelectMany(w => BreakSchedule.Outside(breaks, w.Start, w.End))
                .Select(w =>
                {
                    var (s, e) = ((long)(w.Start - readFrom).TotalSeconds, (long)(w.End - readFrom).TotalSeconds);
                    var overlap = $"{Min(endRel, e.ToString())} - {Max(start, s.ToString())}";
                    return $"CASE WHEN {overlap} > 0 THEN {overlap} ELSE 0 END";
                })
                .ToList();
            if (terms.Count > 0)
                cases.Add($"WHEN '{g.Key}' THEN (" + string.Join("\n               + ", terms) + ")");
        }
        return cases.Count == 0 ? "0" : $"CASE {line} " + string.Join("\n             ", cases) + " ELSE 0 END";
    }

    // Planned stops of the Planification window over the read range; none
    // (and a log line) if they can't be read, so the KPIs never fail on them.
    private async Task<List<(string MachineId, DateTime Start, DateTime End)>> LoadPlannedStopsAsync(DateTime from, DateTime to)
    {
        try
        {
            return await _planning.GetPlannedStopWindowsAsync(from, to);
        }
        catch (Exception ex)
        {
            AppLogger.Log("Arrets planifies (Planification) illisibles, non deduits du TRS : " + ex.Message);
            return new();
        }
    }

    // A snapshot interval longer than this with the counter frozen means
    // StatsViewer wasn't writing (every line at once when the server PC is
    // off): "no data" rather than a machine stop. The cadence is 1-2 min.
    private const int NoDataSeconds = 600;

    // Each line's timeline in [from, to) cut into segments of consecutive
    // snapshot intervals in the same state: producing (counter moved),
    // stopped (counter frozen), no data (frozen across a gap longer than
    // NoDataSeconds) or unplaced (moved across a gap too long to place). Same cleaned snapshots as the KPIs; an interval runs
    // from the line's previous snapshot to this one. Segments that start
    // before from or end after to are returned whole.
    public async Task<List<LineSegment>> GetLineSegmentsAsync(IReadOnlyCollection<string> machineIds, DateTime from, DateTime to)
    {
        if (from < _settings.DataStartDate)
            from = _settings.DataStartDate;
        if (machineIds.Count == 0 || to <= from)
            return new List<LineSegment>();

        var d = Dialect.For(_connectionFactory.Provider);
        const string lineFilter = "{0} IN (SELECT MachineId FROM Machines WHERE MachineId IN @machineIds)";
        var sql = $@"
WITH {CleanSnapshotsSql(lineFilter, d)},
iv AS (
    SELECT r.numero_linea, r.progressivo, r.tempo_inizio, r.tempo_fine AS t1, r.tot,
           LAG(r.tempo_fine) OVER (PARTITION BY r.numero_linea ORDER BY r.tempo_fine, r.progressivo) AS t0,
           LAG(r.tot) OVER ({ByProduction}) AS prev_tot
    FROM rz r
),
st AS (
    -- Moved: counter up since the production's previous snapshot, a machine
    -- reset (total under half the previous one), or a production started
    -- inside the interval that already shows bags.
    SELECT iv.numero_linea, iv.progressivo, iv.t0, iv.t1,
           CASE WHEN iv.tot > iv.prev_tot
                  OR (iv.tot > 0 AND 2 * iv.tot < iv.prev_tot)
                  OR (iv.prev_tot IS NULL AND iv.tot > 0 AND iv.tempo_inizio >= iv.t0)
                THEN CASE WHEN {d.Seconds("iv.t0", "iv.t1")} > {MaxIntervalSeconds}
                          THEN {(int)LineState.Unplaced} ELSE {(int)LineState.Producing} END
                WHEN {d.Seconds("iv.t0", "iv.t1")} > {NoDataSeconds} THEN {(int)LineState.NoData}
                ELSE {(int)LineState.Stopped} END AS state
    FROM iv
    WHERE iv.t0 IS NOT NULL AND iv.t1 > iv.t0
),
isl AS (
    SELECT st.*,
           ROW_NUMBER() OVER (PARTITION BY st.numero_linea ORDER BY st.t1, st.progressivo)
           - ROW_NUMBER() OVER (PARTITION BY st.numero_linea, st.state ORDER BY st.t1, st.progressivo) AS grp
    FROM st
)
SELECT isl.numero_linea AS MachineId, isl.state AS State, MIN(isl.t0) AS StartTime, MAX(isl.t1) AS EndTime
FROM isl
GROUP BY isl.numero_linea, isl.state, isl.grp
HAVING MAX(isl.t1) > @from AND MIN(isl.t0) < @to";

        var parameters = new DynamicParameters();
        parameters.Add("machineIds", machineIds.ToList());
        parameters.Add("from", from);
        parameters.Add("to", to);
        parameters.Add("readFrom", from - SnapshotMargin < _settings.DataStartDate ? _settings.DataStartDate : from - SnapshotMargin);
        parameters.Add("readTo", to + SnapshotMargin);

        using var connection = _connectionFactory.CreateConnection();
        var segments = (await connection.QueryAsync<LineSegment>(sql, parameters)).ToList();
        foreach (var s in segments)
            s.MachineId = s.MachineId.TrimEnd();
        return segments.OrderBy(s => s.MachineId).ThenBy(s => s.StartTime).ToList();
    }

    // Ranges over a day are read one calendar day at a time: SQL Server's
    // cost grows faster than the range (the month Cumul of 10 lines passed
    // the 60 s timeout on 29/10 while each day takes 2-4 s). Buckets never
    // cross midnight and each day reads its own SnapshotMargin, so the result
    // is the same.
    private async Task<List<ProductionBucket>> QueryBucketsAsync(
        string lineFilter, BucketSize size, DateTime from, DateTime to, object filterParams, bool includeWeights)
    {
        if (from < _settings.DataStartDate)
            from = _settings.DataStartDate;
        if (to - from <= TimeSpan.FromDays(1))
            return await QueryRangeAsync(lineFilter, size, from, to, filterParams, includeWeights);
        var buckets = new List<ProductionBucket>();
        for (var start = from; start < to;)
        {
            var end = start.Date.AddDays(1) < to ? start.Date.AddDays(1) : to;
            buckets.AddRange(await QueryRangeAsync(lineFilter, size, start, end, filterParams, includeWeights));
            start = end;
        }
        return buckets;
    }

    // lineFilter: SQL condition on the line code, {0} = the column/expression holding it.
    private async Task<List<ProductionBucket>> QueryRangeAsync(
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
        const string byProduction = ByProduction;

        var previous = string.Join(",\n", Counters.Select(c =>
            $"           LAG(r.{c}) OVER ({byProduction}) AS prev_{c}"));
        var deltas = string.Join(",\n", Counters.Select(c =>
            $"           g.known * CASE WHEN g.prev_tot IS NULL OR g.restart = 1 THEN g.{c}"
            + $" WHEN g.{c} > g.prev_{c} THEN g.{c} - g.prev_{c} ELSE 0 END AS d_{c}"));
        var saneDeltas = string.Join(",\n", Counters.Select(c => $"           sc.sane * sc.d_{c} AS d_{c}"));

        var readFrom = from - SnapshotMargin < dataStart ? dataStart : from - SnapshotMargin;
        var readTo = to + SnapshotMargin;
        var planned = PlannedOverlapSql(await LoadPlannedStopsAsync(readFrom, readTo), readFrom, readTo,
            "sc.numero_linea", "sc.e_rel", "sc.dur");

        var sql = $@"
WITH {CleanSnapshotsSql(lineFilter, d)},
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
           {d.SecondOfDay("g.tempo_fine")} AS e_sec,
           {(planned == "0" ? "0" : d.Seconds("@readFrom", "g.tempo_fine"))} AS e_rel,
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
           -- Interval speed (bags/min), for the max speed of the bucket.
           CASE WHEN sc.sane = 1 AND sc.dur >= {MinSpeedIntervalSeconds}
                THEN CAST(sc.d_tot AS {d.Float}) * 60 / sc.dur ELSE 0 END AS spd,
           -- Seconds of the interval inside a planned break.
           {BreakOverlapSql("sc.e_sec", "sc.dur")} AS brk,
           -- Seconds of the interval inside a stop planned in Planification.
           {planned} AS pln,
{saneDeltas}
    FROM sc
)
SELECT a.*, pr.ProductName, CAST(COALESCE(pr.NominalRatePerMinute, 0) AS {d.Float}) AS ArticleRate
FROM (
    -- One row per line, bucket, production AND target weight: rate and
    -- weight figures must use each bag's own target. The target can change
    -- inside a production (operator sets 400 g instead of 500 g without
    -- opening a new one, counters keep running): each interval goes to the
    -- target of the snapshot that ends it.
    SELECT x.numero_linea                       AS MachineId,
           {bucket}                             AS StartTime,
           RTRIM(x.ID_produzione)               AS ProductionId,
           RTRIM(MAX(x.codice_articolo))        AS ProductCode,
           CAST(COALESCE(MAX(x.target), 0) AS {d.Float}) AS TargetNetWeight,
           CAST(SUM(x.d_tot)   AS BIGINT)       AS TotalBags,
           CAST(SUM(x.d_sotto) AS BIGINT)       AS BagsUnderTolerance,
           CAST(SUM(x.d_ok)    AS BIGINT)       AS BagsInTolerance,
           CAST(SUM(x.d_sopra) AS BIGINT)       AS BagsOverTolerance,
           CAST(SUM(x.d_indet) AS BIGINT)       AS BagsIndeterminate,
           CAST(SUM(x.d_metal) AS BIGINT)       AS MetalRejects,
           CAST(SUM(x.dur) AS {d.Float}) / 60   AS ObservedMinutes,
           CAST(SUM(CASE WHEN x.d_tot > 0 THEN x.dur ELSE 0 END) AS {d.Float}) / 60 AS RunMinutes,
           -- Only stopped time is planned: a line that runs through a break
           -- or a maintenance slot keeps that time as run time.
           CAST(SUM(CASE WHEN x.d_tot > 0 THEN 0 WHEN x.brk + x.pln > x.dur THEN x.dur ELSE x.brk + x.pln END) AS {d.Float}) / 60 AS PlannedStopMinutes,
           CAST(MAX(x.spd) AS {d.Float})        AS MaxSpeed,
           -- Fallback weight data (used only when the line has no lot): bags
           -- whose running average is within 15 % of the target.
           CAST(SUM(CASE WHEN x.target > 0 AND x.media >= 0.85 * x.target AND x.media <= 1.15 * x.target
                         THEN x.d_tot ELSE 0 END) AS BIGINT) AS MediaBags,
           CAST(SUM(CASE WHEN x.target > 0 AND x.media >= 0.85 * x.target AND x.media <= 1.15 * x.target
                         THEN x.media * x.d_tot ELSE 0 END) AS {d.Float}) AS MediaWeightSum
    FROM x
    WHERE x.mid_t >= @from AND x.mid_t < @to
    GROUP BY x.numero_linea, {bucket}, x.ID_produzione, x.target
) a
LEFT JOIN Products pr ON pr.Code = a.ProductCode";

        var parameters = new DynamicParameters(filterParams);
        parameters.Add("from", from);
        parameters.Add("to", to);
        parameters.Add("readFrom", readFrom);
        parameters.Add("readTo", readTo);

        using var connection = _connectionFactory.CreateConnection();
        var parts = (await connection.QueryAsync<BucketPartRow>(sql, parameters)).ToList();
        foreach (var p in parts)
            // CHAR(4) numero_linea comes back padded if a code is ever shorter.
            p.MachineId = p.MachineId.TrimEnd();

        var weights = includeWeights
            ? await LoadWeightPartsAsync(connection, d, lineFilter, size, parameters, parts)
            : new List<(string MachineId, DateTime StartTime, WeightPart Part)>();

        var length = size == BucketSize.Hour ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        var partsByKey = parts.ToDictionary(p => (p.MachineId, p.StartTime, p.ProductionId, TargetNetWeight: p.TargetNetWeight));
        var weightsByKey = weights.ToLookup(w => (w.MachineId, w.StartTime, w.Part.ProductionId, TargetNetWeight: w.Part.Target), w => w.Part);
        var keys = partsByKey.Keys
            .Union(weightsByKey.Select(g => g.Key))
            .OrderBy(k => k.StartTime)
            .ThenBy(k => k.MachineId)
            .ThenBy(k => k.ProductionId)
            .ThenBy(k => k.TargetNetWeight);

        var buckets = new List<ProductionBucket>();
        foreach (var key in keys)
        {
            var weightParts = weightsByKey[key].ToList();
            // A lot can close in a bucket where its production's counters
            // didn't move: weights only, no counters.
            var p = partsByKey.GetValueOrDefault(key) ?? new BucketPartRow
            {
                MachineId = key.MachineId,
                StartTime = key.StartTime,
                ProductionId = key.ProductionId,
                ProductCode = ProductionIdParts.Parse(key.ProductionId).ArticleCode,
                TargetNetWeight = key.TargetNetWeight,
            };
            var b = new ProductionBucket
            {
                MachineId = key.MachineId,
                ProductionId = key.ProductionId,
                StartTime = key.StartTime,
                EndTime = key.StartTime + length,
                // Day buckets span all three postes: no poste.
                ShiftId = size == BucketSize.Hour ? KpiCalculator.CurrentShiftId(key.StartTime) : 0,
                ProductCode = p.ProductCode,
                // Products row missing or unnamed: show the article code without
                // its zero padding (00000000NOUVELLEPROD -> NOUVELLEPROD).
                ProductName = string.IsNullOrWhiteSpace(p.ProductName) ? ProductionIdParts.DisplayArticle(p.ProductCode) : p.ProductName,
                TargetNetWeight = p.TargetNetWeight,
                TotalBags = p.TotalBags,
                BagsUnderTolerance = p.BagsUnderTolerance,
                BagsInTolerance = p.BagsInTolerance,
                BagsOverTolerance = p.BagsOverTolerance,
                BagsIndeterminate = p.BagsIndeterminate,
                MetalRejects = p.MetalRejects,
                ObservedMinutes = p.ObservedMinutes,
                RunMinutes = p.RunMinutes,
                PlannedStopMinutes = p.PlannedStopMinutes,
                MaxSpeedBagsPerMinute = p.MaxSpeed,
            };

            // Performance inputs at the production's own nominal rate: a
            // per-article rate in Products wins, otherwise the machine/weight
            // table from Parametres. Unknown rate (0) = left out of both sides.
            var rate = p.ArticleRate > 0 ? p.ArticleRate : _settings.FindNominalRate(p.MachineId, p.TargetNetWeight);
            b.NominalRate = rate > 0 ? rate : 0;
            if (p.TotalBags > 0 && rate > 0)
            {
                b.IdealMinutes = p.TotalBags / rate;
                b.RatedRunMinutes = p.RunMinutes;
            }

            b.WeightParts.AddRange(weightParts);
            buckets.Add(b);
        }
        return buckets;
    }

    private sealed class LotRow
    {
        public string MachineId { get; set; } = "";
        public string ProductionId { get; set; } = "";
        public DateTime StartTime { get; set; }
        public double? Target { get; set; }
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
    // A lot's target is the peso_netto_impostato of its production's first
    // snapshot at or after the lot (the target can change inside a
    // production); lots of a production with no snapshot in the read range
    // are left out. Line = first 4 characters of ID_produzione (odd codes
    // like "PL 3" match no machine).
    // The empty bag weight set in Parametres for the line and target is
    // deducted from every average (Stiavelli weighs the full bag).
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
            // Target subquery seeks IX_produzione_linea_fine (line + time).
            var lots = await connection.QueryAsync<LotRow>($@"
SELECT t.MachineId, t.ProductionId, t.StartTime, t.Target,
       CAST(SUM(t.dimensione) AS BIGINT) AS Bags,
       CAST(SUM(t.media * t.dimensione) AS {d.Float}) AS WeightSum
FROM (
    SELECT {lineOfLot} AS MachineId, RTRIM(l.ID_produzione) AS ProductionId, {bucket} AS StartTime,
           l.dimensione, CAST(l.media_effettiva AS {d.Float}) AS media,
           (SELECT {d.Top(1)} CAST(p.peso_netto_impostato AS {d.Float}) FROM produzione p
            WHERE p.numero_linea = {lineOfLot} AND p.ID_produzione = l.ID_produzione
              AND p.tempo_fine >= l.ora_fine AND p.tempo_fine < @readTo
            ORDER BY p.tempo_fine {d.Limit(1)}) AS Target
    FROM lotto l
    WHERE l.ora_fine >= @from AND l.ora_fine < @to
      AND l.dimensione > 0 AND l.media_effettiva > 0
      AND {string.Format(lineFilter, lineOfLot)}
) t
GROUP BY t.MachineId, t.ProductionId, t.StartTime, t.Target", parameters);

            foreach (var lot in lots)
            {
                var machineId = lot.MachineId.TrimEnd();
                // No later snapshot read: the production's own target.
                if (!targets.TryGetValue(lot.ProductionId, out var productionTarget))
                    continue;
                var target = lot.Target is > 0 ? lot.Target.Value : productionTarget;
                linesWithLots.Add(machineId);
                var tare = _settings.FindTare(machineId, target);
                result.Add((machineId, lot.StartTime, new WeightPart(lot.ProductionId, target, lot.Bags, lot.WeightSum - tare * lot.Bags)));
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
            {
                var tare = _settings.FindTare(p.MachineId, p.TargetNetWeight);
                result.Add((p.MachineId, p.StartTime,
                    new WeightPart(p.ProductionId, p.TargetNetWeight, p.MediaBags, p.MediaWeightSum - tare * p.MediaBags)));
            }
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
        Func<string, string> SecondOfDay,
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
            x => $"DATEDIFF(SECOND, DATEADD(DAY, DATEDIFF(DAY, 0, {x}), 0), {x})",
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
            // Wall-clock text read as UTC: modulo a day gives its time of day.
            x => $"(CAST(strftime('%s', {x}) AS INTEGER) % 86400)",
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
