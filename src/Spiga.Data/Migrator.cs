using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using Spiga.Core.Security;
using Spiga.Core.Waste;

namespace Spiga.Data;

// Brings the application database to the current version at start-up:
// runs the embedded scripts Migrations/<provider>/NNNN_*.sql not yet in
// SchemaVersion, each in its own transaction, then seeds the Administrateur
// role and the waste reasons when missing. Safe to run on every start.
public sealed partial class Migrator(AppDb db)
{
    public const int CurrentVersion = 2;

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();

    public async Task<int> MigrateAsync()
    {
        using var connection = await db.OpenAsync();
        var create = db.Provider == AppDbProvider.SqlServer
            ? "IF OBJECT_ID('dbo.SchemaVersion') IS NULL CREATE TABLE dbo.SchemaVersion (Version INT NOT NULL PRIMARY KEY, AppliedAt DATETIME2(0) NOT NULL);"
            : "CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL PRIMARY KEY, AppliedAt TEXT NOT NULL);";
        await connection.ExecuteAsync(create);
        var applied = (await connection.QueryAsync<int>("SELECT Version FROM SchemaVersion")).ToHashSet();

        var folder = db.Provider == AppDbProvider.SqlServer ? "SqlServer" : "Sqlite";
        var assembly = typeof(Migrator).Assembly;
        var scripts = assembly.GetManifestResourceNames()
            .Where(n => n.Contains($".Migrations.{folder}.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => (Name: n, Version: VersionOf(n)))
            .OrderBy(s => s.Version)
            .ToList();

        var ran = 0;
        foreach (var (name, version) in scripts.Where(s => !applied.Contains(s.Version)))
        {
            var sql = Read(assembly, name);
            using var transaction = connection.BeginTransaction();
            foreach (var batch in GoSeparator().Split(sql).Where(b => !string.IsNullOrWhiteSpace(b)))
                await connection.ExecuteAsync(batch, transaction: transaction);
            await connection.ExecuteAsync("INSERT INTO SchemaVersion (Version, AppliedAt) VALUES (@version, @at)",
                new { version, at = DateTime.Now }, transaction);
            transaction.Commit();
            ran++;
        }

        await SeedAsync();
        return ran;
    }

    private async Task SeedAsync()
    {
        // Only the built-in Administrateur role. Every other role is defined
        // in the application (from scratch or from the optional templates).
        var roles = new RoleStore(db);
        if (await roles.GetByCodeAsync(RoleTemplates.AdminCode) == null)
            await roles.InsertAsync(RoleTemplates.Administrator());

        using var connection = await db.OpenAsync();
        if (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM WasteReasons") == 0)
        {
            var reasons = new (WasteKind Kind, string Label)[]
            {
                (WasteKind.Emballage, "Réglage machine"),
                (WasteKind.Emballage, "Changement de format"),
                (WasteKind.Emballage, "Changement de bobine"),
                (WasteKind.Emballage, "Défaut de soudure"),
                (WasteKind.Emballage, "Film défectueux"),
                (WasteKind.Emballage, "Impression / marquage"),
                (WasteKind.Emballage, "Autre"),
                (WasteKind.Produit, "Démarrage de ligne"),
                (WasteKind.Produit, "Arrêt de ligne"),
                (WasteKind.Produit, "Hors spécification"),
                (WasteKind.Produit, "Produit cassé / brisures"),
                (WasteKind.Produit, "Vidange / nettoyage"),
                (WasteKind.Produit, "Autre"),
            };
            var order = 0;
            foreach (var (kind, label) in reasons)
                await connection.ExecuteAsync("INSERT INTO WasteReasons (Kind, Label, IsActive, SortOrder) VALUES (@kind, @label, 1, @order)",
                    new { kind = kind.ToString(), label, order = order++ });
        }
    }

    private static int VersionOf(string resourceName)
    {
        var file = resourceName.Split('.')[^2];
        return int.Parse(file[..file.IndexOf('_')]);
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
