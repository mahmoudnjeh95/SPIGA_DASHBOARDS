using System.Diagnostics;
using System.IO;

namespace SpigaDashboards.App.Services;

// Connection log lines always accumulate to a file. "Console" is a separate,
// independent process that tails that file - closing its window only closes
// the viewer, never the app. (A console AllocConsole'd into this process
// would instead take the whole app down when its window is closed, since
// Windows terminates every process attached to a console when it's closed.)
public static class AppLogger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpigaDashboards", "logs", "connection.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

            var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
            using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.Write(line);
        }
        catch (IOException)
        {
            // Best-effort logging only; never let a logging failure break the app.
        }
    }

    public static void OpenConsole()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        if (!File.Exists(LogPath))
            File.Create(LogPath).Dispose();

        var psCommand =
            "$host.UI.RawUI.WindowTitle = 'SPIGA Dashboards - Journal de connexion'; " +
            $"Get-Content -Path '{LogPath}' -Encoding UTF8 -Wait -Tail 100";

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -NoExit -Command \"{psCommand}\"",
            UseShellExecute = true,
        });
    }
}
