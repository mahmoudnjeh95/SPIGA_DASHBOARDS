using System.Data;
using Dapper;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.Data;

// Up to version 2.0 the application wrote its own tables in the production
// database "stats" (planning of the stops, shared settings, sticky note).
// They now live in the application database (spiga_suivi). At start-up,
// once per table, the rows still in "stats" are copied there; LegacyImports
// records it, so a table is never copied twice. "stats" is only read: the old
// tables stay there, untouched, for IT to drop when every PC runs this
// version (a PC on an older version keeps writing there, unseen by others).
public sealed class LegacyDataImporter(IDbConnectionFactory live, IDbConnectionFactory appData)
{
    private sealed record Table(string Name, string[] Columns, bool KeepIds, string Where = "");

    private static readonly Table[] Tables =
    {
        new("PlanStopQualifications", new[] { "Id", "MachineId", "StopStart", "StopEnd", "ShiftDate", "ShiftId", "Team", "CauseCode",
            "Component", "Technician", "Comment", "IsManual", "CreatedAt", "ExpectedEnd" }, true),
        new("PlanMaintenancePlans", new[] { "Id", "MachineId", "Task", "Kind", "IntervalDays", "IntervalBags", "DurationMinutes",
            "LastDoneAt", "IsActive" }, true),
        // Events point at their plan: ids kept.
        new("PlanMaintenanceEvents", new[] { "Id", "PlanId", "MachineId", "Task", "PlannedStart", "DurationMinutes", "Status",
            "DoneAt", "Technician", "Comment", "Kind" }, true),
        new("PlanProductionOrders", new[] { "Id", "OrderNo", "MachineId", "Product", "TargetWeightGrams", "QuantityBags",
            "PlannedStart", "PlannedEnd", "Status", "Comment" }, true),
        new("AppSharedSettings", new[] { "Id", "Version", "Json", "UpdatedAt", "UpdatedBy" }, false),
    };

    private const string NotesImport = "Notes";

    private sealed class OldNote
    {
        public string Content { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    // What was copied this time, one line per table ("PlanProductionOrders : 12 lignes").
    public async Task<IReadOnlyList<string>> ImportAsync()
    {
        var report = new List<string>();
        using var target = appData.CreateConnection();
        target.Open();
        var done = (await target.QueryAsync<string>("SELECT Name FROM LegacyImports")).ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var source = live.CreateConnection();
        source.Open();
        foreach (var table in Tables.Where(t => !done.Contains(t.Name)))
            report.Add(await CopyAsync(source, target, table));
        if (!done.Contains(NotesImport))
            report.Add(await CopyNoteAsync(source, target));
        return report;
    }

    private async Task<string> CopyAsync(IDbConnection source, IDbConnection target, Table table)
    {
        var columns = await ColumnsAsync(source, live.Provider, table.Name);
        if (columns.Count == 0)
            return await MarkAsync(target, table.Name, 0, "absente de la base de production");
        if (await target.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table.Name}") > 0)
        {
            // Already in use here: copying could clash with its ids.
            AppLogger.Log($"Reprise {table.Name} : la table de la base applicative contient déjà des lignes, rien n'est copié.");
            return await MarkAsync(target, table.Name, 0, "déjà renseignée, non copiée");
        }

        var copied = table.Columns.Where(c => columns.Contains(c)).ToList();
        var rows = (await source.QueryAsync($"SELECT {string.Join(", ", copied)} FROM {table.Name}"))
            .Cast<IDictionary<string, object?>>().ToList();
        if (table.Name == "PlanProductionOrders")
            Renumber(rows);

        var sqlServer = appData.Provider == DatabaseProvider.SqlServer;
        using var transaction = target.BeginTransaction();
        if (sqlServer && table.KeepIds)
            await target.ExecuteAsync($"SET IDENTITY_INSERT dbo.{table.Name} ON", transaction: transaction);
        var insert = $"INSERT INTO {table.Name} ({string.Join(", ", copied)}) VALUES ({string.Join(", ", copied.Select(c => "@" + c))})";
        foreach (var row in rows)
        {
            var args = new DynamicParameters();
            foreach (var c in copied)
                args.Add(c, row[c]);
            await target.ExecuteAsync(insert, args, transaction);
        }
        if (sqlServer && table.KeepIds)
            await target.ExecuteAsync($"SET IDENTITY_INSERT dbo.{table.Name} OFF", transaction: transaction);
        await target.ExecuteAsync("INSERT INTO LegacyImports (Name, ImportedAt, CopiedRows) VALUES (@name, @now, @count)",
            new { name = table.Name, now = DateTime.Now, count = rows.Count }, transaction);
        transaction.Commit();
        AppLogger.Log($"Reprise {table.Name} : {rows.Count} ligne(s) copiée(s) de la base de production vers la base applicative.");
        return $"{table.Name} : {rows.Count} ligne(s)";
    }

    // Order numbers are unique now: a number given twice before keeps its
    // first order, the others get "-2", "-3"...
    private static void Renumber(List<IDictionary<string, object?>> rows)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.OrderBy(r => Convert.ToInt64(r["Id"])))
        {
            var number = row["OrderNo"]?.ToString() ?? "";
            var candidate = number;
            for (var i = 2; !used.Add(candidate); i++)
                candidate = $"{number}-{i}";
            if (candidate != number)
            {
                AppLogger.Log($"Reprise : ordre {number} en double, renuméroté {candidate}.");
                row["OrderNo"] = candidate;
            }
        }
    }

    // Only the note shown: the old table kept no author.
    private async Task<string> CopyNoteAsync(IDbConnection source, IDbConnection target)
    {
        var columns = await ColumnsAsync(source, live.Provider, "Notes");
        var note = columns.Contains("Content")
            ? await source.QueryFirstOrDefaultAsync<OldNote>(
                "SELECT Content, CreatedAt FROM Notes WHERE IsActive = 1 ORDER BY CreatedAt DESC")
            : null;
        using var transaction = target.BeginTransaction();
        if (note is { } n && await target.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Notes", transaction: transaction) == 0)
            await target.ExecuteAsync(
                @"INSERT INTO Notes (Content, CreatedBy, CreatedByName, Workstation, CreatedAt, IsActive)
                  VALUES (@content, 'reprise', 'Version précédente', '', @at, 1)",
                new { content = n.Content, at = n.CreatedAt }, transaction);
        await target.ExecuteAsync("INSERT INTO LegacyImports (Name, ImportedAt, CopiedRows) VALUES (@name, @now, @count)",
            new { name = NotesImport, now = DateTime.Now, count = note == null ? 0 : 1 }, transaction);
        transaction.Commit();
        return $"Notes : {(note == null ? 0 : 1)} ligne(s)";
    }

    private static async Task<string> MarkAsync(IDbConnection target, string name, int count, string why)
    {
        await target.ExecuteAsync("INSERT INTO LegacyImports (Name, ImportedAt, CopiedRows) VALUES (@name, @now, @count)",
            new { name, now = DateTime.Now, count });
        return $"{name} : {why}";
    }

    // The columns of a table of the production database (none = no table).
    private static async Task<HashSet<string>> ColumnsAsync(IDbConnection connection, DatabaseProvider provider, string table)
        => (await connection.QueryAsync<string>(provider == DatabaseProvider.SqlServer
                ? "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@table)"
                : "SELECT name FROM pragma_table_info(@table)",
            new { table = provider == DatabaseProvider.SqlServer ? "dbo." + table : table }))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
