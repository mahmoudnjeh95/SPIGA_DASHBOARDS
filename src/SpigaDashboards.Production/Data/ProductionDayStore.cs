using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.Data;

// Finished days kept in the application database (ProductionDayCache), for
// every PC: a month or a year opened again no longer runs the slow bucket
// query for each day (2-4 s a day on SQLEXPRESS, 1 s on a laptop's SQLite).
// Per machine and day, what that query returned (before the nominal rates
// and empty bag weights of Paramètres, applied on reading). A stored day is
// used while its fingerprint still matches, checked with two light queries
// on the day: its snapshots (count, last row, total bags; StatsViewer
// catching up late changes them), its lots (uploaded late), the planned
// stops of Planification, the planned breaks and the product table. Any
// trouble (table missing, no rights, server down): the day is read from the
// production database as before.
public partial class ProductionRepository
{
    // Bump when the bucket query or its constants change: every stored day
    // is then read again once.
    private const int StoredDayVersion = 1;

    // The application database has no ProductionDayCache (older version) or
    // refused it: not tried again until ResetConnections.
    private volatile bool _storeUnavailable;

    private string? _source;
    private (DateTime ReadAt, string Hash)? _products;

    private sealed class StoredSlice
    {
        public List<BucketPartRow> Parts { get; set; } = new();
        public List<LotRow> Lots { get; set; } = new();
    }

    private sealed class DayMark
    {
        public string MachineId { get; set; } = "";
        public long N { get; set; }
        public long Last { get; set; }
        public long Total { get; set; }
    }

    private sealed class StoredRow
    {
        public string MachineId { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }

    // Forgets what was checked once (another database, a reconnect).
    private void ResetDayStore()
    {
        _storeUnavailable = false;
        _source = null;
        _products = null;
    }

    // The raw buckets of a closed day for these machines, from the store
    // where still valid, from the production database for the others (then
    // stored); null when the store can't be used.
    private async Task<RawBuckets?> StoredDayAsync(IReadOnlyList<string> machines, BucketSize size, DateTime day, bool includeWeights)
    {
        var dataStart = _settings.DataStartDate;
        if (_storeUnavailable || day < dataStart || machines.Count == 0)
            return null;
        var ids = machines.Select(m => m.Trim()).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var from = day;
        var to = day.AddDays(1);
        var readFrom = from - SnapshotMargin < dataStart ? dataStart : from - SnapshotMargin;
        var readTo = to + SnapshotMargin;
        var key = new
        {
            source = Source,
            day = int.Parse(day.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            size = (int)size,
            weights = includeWeights ? 1 : 0,
        };

        Dictionary<string, string> fingerprints;
        try
        {
            fingerprints = await FingerprintsAsync(ids, size, from, to, readFrom, readTo, includeWeights);
        }
        catch (Exception ex)
        {
            AppLogger.Log("Jour enregistré non vérifié, relu en entier : " + ex.Message);
            return null;
        }

        List<StoredRow> rows;
        try
        {
            using var app = _appData.CreateConnection();
            rows = (await app.QueryAsync<StoredRow>(@"
SELECT MachineId, Fingerprint, Data FROM ProductionDayCache
WHERE Source = @source AND Day = @day AND Size = @size AND Weights = @weights AND MachineId IN @ids",
                new { key.source, key.day, key.size, key.weights, ids })).ToList();
        }
        catch (Exception ex)
        {
            _storeUnavailable = true;
            AppLogger.Log("Jours enregistrés indisponibles (base de l'application), lus dans la base de production : " + ex.Message);
            return null;
        }

        var raw = new RawBuckets { Weights = includeWeights };
        var hits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!fingerprints.TryGetValue(row.MachineId, out var expected) || !string.Equals(row.Fingerprint.Trim(), expected, StringComparison.Ordinal))
                continue;
            try
            {
                var slice = Unpack(row.Data);
                raw.Parts.AddRange(slice.Parts);
                raw.Lots.AddRange(slice.Lots);
                hits.Add(row.MachineId);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // Unreadable: read again below and replaced.
            }
        }

        var missing = ids.Where(id => !hits.Contains(id)).ToList();
        if (missing.Count == 0)
            return raw;
        var fresh = await QueryRawAsync(missing, size, from, to, includeWeights);
        raw.Parts.AddRange(fresh.Parts);
        raw.Lots.AddRange(fresh.Lots);

        try
        {
            var now = DateTime.Now;
            var saved = missing.Select(id => new
            {
                key.source,
                machineId = id,
                key.day,
                key.size,
                key.weights,
                fingerprint = fingerprints[id],
                data = Pack(new StoredSlice
                {
                    Parts = fresh.Parts.Where(p => string.Equals(p.MachineId, id, StringComparison.OrdinalIgnoreCase)).ToList(),
                    Lots = fresh.Lots.Where(l => string.Equals(l.MachineId, id, StringComparison.OrdinalIgnoreCase)).ToList(),
                }),
                savedAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second),
            }).ToList();
            using var app = _appData.CreateConnection();
            app.Open();
            using var transaction = app.BeginTransaction();
            await app.ExecuteAsync(@"
DELETE FROM ProductionDayCache
WHERE Source = @source AND Day = @day AND Size = @size AND Weights = @weights AND MachineId = @machineId", saved, transaction);
            await app.ExecuteAsync(@"
INSERT INTO ProductionDayCache (Source, MachineId, Day, Size, Weights, Fingerprint, Data, SavedAt)
VALUES (@source, @machineId, @day, @size, @weights, @fingerprint, @data, @savedAt)", saved, transaction);
            transaction.Commit();
        }
        catch (Exception ex)
        {
            // Another PC saving the same day at the same time, or no rights:
            // the day is shown all the same, and stored next time.
            AppLogger.Log("Jour de production non enregistré : " + ex.Message);
        }
        return raw;
    }

    // What each machine's day is made of, hashed.
    private async Task<Dictionary<string, string>> FingerprintsAsync(
        List<string> ids, BucketSize size, DateTime from, DateTime to, DateTime readFrom, DateTime readTo, bool includeWeights)
    {
        var d = Dialect.For(_connectionFactory.Provider);
        var (lineFilter, filterParams) = LineFilter(ids);
        var parameters = new DynamicParameters(filterParams);
        parameters.Add("from", from);
        parameters.Add("to", to);
        parameters.Add("readFrom", readFrom);
        parameters.Add("readTo", readTo);

        using var connection = _connectionFactory.CreateConnection();
        var snapshots = (await connection.QueryAsync<DayMark>($@"
SELECT p.numero_linea AS MachineId, CAST(COUNT(*) AS BIGINT) AS N, CAST(MAX(p.progressivo) AS BIGINT) AS Last,
       CAST(SUM(CAST(COALESCE(p.totale_confezioni, 0) AS BIGINT)) AS BIGINT) AS Total
FROM produzione p
WHERE {string.Format(lineFilter, "p.numero_linea")} AND p.tempo_fine >= @readFrom AND p.tempo_fine < @readTo
GROUP BY p.numero_linea", parameters))
            .ToDictionary(m => m.MachineId.Trim(), StringComparer.OrdinalIgnoreCase);

        var lots = new Dictionary<string, DayMark>(StringComparer.OrdinalIgnoreCase);
        if (includeWeights && await HasLotTableAsync(connection, d))
            foreach (var m in await connection.QueryAsync<DayMark>(@"
SELECT SUBSTRING(l.ID_produzione, 1, 4) AS MachineId, CAST(COUNT(*) AS BIGINT) AS N, CAST(SUM(l.dimensione) AS BIGINT) AS Total
FROM lotto l
WHERE l.ora_fine >= @from AND l.ora_fine < @to AND l.dimensione > 0 AND l.media_effettiva > 0
GROUP BY SUBSTRING(l.ID_produzione, 1, 4)", parameters))
                lots[m.MachineId.Trim()] = m;

        var products = await ProductsHashAsync(connection, d);
        var planned = await LoadPlannedStopsAsync(readFrom, readTo);
        var breaks = string.Join(",", BreakSchedule.DayWindows(_settings.PlannedBreaks).Select(w => $"{w.Start}-{w.End}"));
        var common = string.Join("|", StoredDayVersion, size, includeWeights, _settings.DataStartDate.ToString("O", CultureInfo.InvariantCulture),
            breaks, products);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var s = snapshots.GetValueOrDefault(id);
            var l = lots.GetValueOrDefault(id);
            var windows = string.Join(",", planned
                .Where(w => string.Equals(w.MachineId.Trim(), id, StringComparison.OrdinalIgnoreCase))
                .Select(w => $"{w.Start:yyyyMMddHHmmss}-{w.End:yyyyMMddHHmmss}")
                .Order(StringComparer.Ordinal));
            var text = string.Join("|", common, id.ToUpperInvariant(), s?.N ?? 0, s?.Last ?? 0, s?.Total ?? 0, l?.N ?? 0, l?.Total ?? 0, windows);
            result[id] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
        }
        return result;
    }

    // The product table (names, rates per article) the query joins: read
    // again at most every minute.
    private async Task<string> ProductsHashAsync(System.Data.IDbConnection connection, Dialect d)
    {
        if (_products is { } known && DateTime.UtcNow - known.ReadAt < TimeSpan.FromMinutes(1))
            return known.Hash;
        var rows = await connection.QueryAsync<(string Code, string? Name, double Rate)>(
            $"SELECT Code, ProductName AS Name, CAST(COALESCE(NominalRatePerMinute, 0) AS {d.Float}) AS Rate FROM Products");
        var text = string.Join("\n", rows
            .Select(r => r.Code + "\t" + r.Name + "\t" + r.Rate.ToString("R", CultureInfo.InvariantCulture))
            .Order(StringComparer.Ordinal));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        _products = (DateTime.UtcNow, hash);
        return hash;
    }

    // Which production database: its server and name (not the login), or
    // its file.
    private string Source => _source ??= SourceOf(_connectionFactory);

    private static string SourceOf(IDbConnectionFactory factory)
    {
        string id;
        using (var connection = factory.CreateConnection())
        {
            var text = connection.ConnectionString;
            try
            {
                if (factory.Provider == DatabaseProvider.SqlServer)
                {
                    var server = new SqlConnectionStringBuilder(text);
                    id = server.DataSource.Trim() + "/" + server.InitialCatalog.Trim();
                }
                else
                    id = Path.GetFullPath(new SqliteConnectionStringBuilder(text).DataSource);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                id = text;
            }
        }
        var bytes = Encoding.UTF8.GetBytes(factory.Provider + "|" + id.ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(bytes))[..16];
    }

    private static byte[] Pack(StoredSlice slice)
    {
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.Optimal))
            JsonSerializer.Serialize(zip, slice);
        return output.ToArray();
    }

    private static StoredSlice Unpack(byte[] data)
    {
        using var zip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<StoredSlice>(zip) ?? new StoredSlice();
    }
}
