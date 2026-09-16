using System.IO;

namespace SpigaDashboards.App.Data;

public static class DatabasePathResolver
{
    private const string DbFileName = "spiga_dashboards.db";

    public static string ResolveSqlitePath()
    {
        var besideExe = Path.Combine(AppContext.BaseDirectory, DbFileName);
        if (File.Exists(besideExe))
            return besideExe;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "database", DbFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {DbFileName}. Run database/generate_seed_data.py from the repo root, " +
            "or place a copy next to the application executable.");
    }
}
