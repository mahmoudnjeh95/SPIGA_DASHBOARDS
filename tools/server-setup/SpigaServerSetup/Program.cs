using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace SpigaServerSetup;

/// <summary>
/// Server step of the SPIGA Dashboards setup .exe. The bundle runs it only when
/// "Serveur" is chosen. It checks each server-side collector (scheduled task(s)
/// + deployed script) and, if anything is missing or wrong, asks the user before
/// creating / repairing it with the matching install_*.ps1, which ship next to
/// this exe. It always exits 0: a refused or failed collector must never roll
/// back the app install.
///
/// Collectors checked:
///   - PC01 bridge           ("SPIGA - PC01 Collector")        -> install_pc01_collector.ps1
///   - Ricciarelli RIC1/RIC2 (two "SPIGA - Ricciarelli ..." )  -> install_ricciarelli_collector.ps1
/// RIC2 (.54) exits 0 even while its port is closed, so an offline weigher is
/// not reported as a fault.
///
///   SpigaServerSetup.exe          check, ask, fix (elevates itself if needed)
///   SpigaServerSetup.exe -check   report only, no prompt, no change
///                                 (exit 0 = all healthy, 1 = action needed)
/// </summary>
internal static class Program
{
    private const string Caption = "SPIGA Dashboards - Serveur";
    private const string LogDir = @"C:\ProgramData\SpigaPC01Collector";

    // A tick runs every minute; older than this means the task is stalled.
    private static readonly TimeSpan MaxSilence = TimeSpan.FromMinutes(5);

    private const int TaskStateDisabled = 1;
    private const int TaskStateRunning = 4;
    private const int ResultRunning = 0x41301;
    private const int ResultNotYetRun = 0x41303;

    private static string _logPath;

    private sealed class Collector
    {
        public string Label;         // in prompts, e.g. "pont PC01"
        public string DataName;      // the lines it feeds, e.g. "PC01 (Pâte courte)"
        public string DeployDir;
        public string CollectorFile;
        public string InstallerFile;
        public string[] TaskNames;
    }

    private static readonly Collector[] Collectors =
    {
        new Collector
        {
            Label = "pont PC01",
            DataName = "PC01 (Pâte courte)",
            DeployDir = @"C:\ProgramData\SpigaPC01Collector",
            CollectorFile = "pc01_collector.ps1",
            InstallerFile = "install_pc01_collector.ps1",
            TaskNames = new[] { "SPIGA - PC01 Collector" },
        },
        new Collector
        {
            Label = "collecteurs Ricciarelli",
            DataName = "RIC1 / RIC2 (Pâte)",
            DeployDir = @"C:\ProgramData\SpigaRicciarelliCollector",
            CollectorFile = "ricciarelli_collector.ps1",
            InstallerFile = "install_ricciarelli_collector.ps1",
            TaskNames = new[] { "SPIGA - Ricciarelli RIC1 Collector", "SPIGA - Ricciarelli RIC2 Collector" },
        },
    };

    [STAThread]
    private static int Main(string[] args)
    {
        bool checkOnly = args.Any(a => a.Equals("-check", StringComparison.OrdinalIgnoreCase)
                                    || a.Equals("/check", StringComparison.OrdinalIgnoreCase));
        string srcDir = AppDomain.CurrentDomain.BaseDirectory;

        try
        {
            var healths = Collectors.Select(c => Check(c, srcDir)).ToList();

            if (checkOnly)
            {
                foreach (var h in healths) Console.WriteLine(h.Report());
                return healths.All(h => h.Healthy) ? 0 : 1;
            }

            if (!IsElevated())
                return RelaunchElevated(args);

            Directory.CreateDirectory(LogDir);
            _logPath = Path.Combine(LogDir, "server-setup.log");
            Log("---- " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " v" + Version());
            foreach (var h in healths) Log(h.Report());

            Application.EnableVisualStyles();

            var unhealthy = healths.Where(h => !h.Healthy).ToList();
            if (unhealthy.Count == 0)
            {
                Info("Les collecteurs serveur sont installés et fonctionnent.\n\n" +
                     string.Concat(healths.Select(h => "• " + Cap(h.Collector.Label) + " : OK\n")) +
                     "\nAucune modification nécessaire.");
                return 0;
            }

            foreach (var h in unhealthy)
            {
                if (!h.InstallerAvailable)
                {
                    Error("Le « " + h.Collector.Label + " » doit être installé, mais les fichiers d'installation sont " +
                          "introuvables à côté de SpigaServerSetup.exe :\n" + srcDir + "\n\nAucune tâche n'a été créée.");
                    continue;
                }

                if (!Ask(h.Question()))
                {
                    Log("user declined " + h.Collector.Label);
                    Info("Le « " + h.Collector.Label + " » n'a pas été modifié.\n\n" +
                         "Sans lui, les données de " + h.Collector.DataName + " n'arrivent pas dans SPIGA Dashboards. " +
                         "Relancer l'installation et choisir « Serveur » pour le créer plus tard.");
                    continue;
                }

                var (exitCode, output) = RunInstaller(Path.Combine(srcDir, h.Collector.InstallerFile));
                Log(h.Collector.InstallerFile + " exit " + exitCode + "\n" + output);

                // Start-ScheduledTask was just called: give the first tick a moment.
                System.Threading.Thread.Sleep(3000);
                var after = Check(h.Collector, srcDir);
                Log("after: " + after.Report());

                if (exitCode == 0 && after.Healthy)
                    Info("Le « " + h.Collector.Label + " » est installé.\n\n" +
                         "La ou les tâches tournent chaque minute (et au démarrage du PC) " +
                         "et envoient les données de " + h.Collector.DataName + " dans SPIGA Dashboards.");
                else
                    Error("L'installation du « " + h.Collector.Label + " » a échoué (code " + exitCode + ").\n\n" +
                          Tail(output, 12) + "\n\nJournal : " + _logPath);
            }
        }
        catch (Exception ex)
        {
            Log("ERROR " + ex);
            if (checkOnly) { Console.WriteLine("ERROR " + ex.Message); return 1; }
            Error("Vérification des collecteurs serveur impossible :\n" + ex.Message);
        }
        return 0;
    }

    // ---- check ------------------------------------------------------------

    private sealed class TaskHealth
    {
        public string Name;
        public bool Exists, Enabled;
        public int State, LastResult;
        public DateTime LastRun;

        public bool RanRecently => LastRun > DateTime.Now - MaxSilence || State == TaskStateRunning;
        public bool ResultOk => LastResult == 0 || LastResult == ResultRunning || LastResult == ResultNotYetRun;
        public bool Ok => Exists && Enabled && RanRecently && ResultOk;
    }

    private sealed class Health
    {
        public Collector Collector;
        public bool InstallerAvailable, DeployedScriptExists, ScriptMatches;
        public List<TaskHealth> Tasks = new List<TaskHealth>();

        public bool Healthy => ScriptMatches && Tasks.Count > 0 && Tasks.All(t => t.Ok);

        public IEnumerable<string> Problems()
        {
            foreach (var t in Tasks)
            {
                if (!t.Exists) { yield return "la tâche « " + t.Name + " » n'existe pas sur ce PC"; continue; }
                if (!t.Enabled) yield return "la tâche « " + t.Name + " » est désactivée";
                else if (!t.RanRecently)
                    yield return t.LastRun.Year < 2000
                        ? "la tâche « " + t.Name + " » n'a jamais tourné"
                        : "la tâche « " + t.Name + " » n'a pas tourné depuis le " + t.LastRun.ToString("dd/MM/yyyy HH:mm");
                if (!t.ResultOk) yield return "la tâche « " + t.Name + " » s'est terminée en erreur (0x" + t.LastResult.ToString("X") + ")";
            }
            if (!DeployedScriptExists) yield return "le script du collecteur est absent de " + Collector.DeployDir;
            else if (!ScriptMatches) yield return "le script installé est différent de celui de cette version";
        }

        public string Question()
        {
            bool anyExist = Tasks.Any(t => t.Exists);
            string intro = anyExist
                ? "Le « " + Collector.Label + " » a un problème :\n"
                : "Le « " + Collector.Label + " » n'est pas installé sur ce serveur :\n";
            string action = anyExist
                ? "Réinstaller la ou les tâches et les redémarrer maintenant ?"
                : "Créer la ou les tâches et les démarrer maintenant ?";
            return intro + string.Concat(Problems().Select(p => "  • " + p + "\n")) + "\n" +
                   "Sans lui, les données de " + Collector.DataName + " n'arrivent pas dans SPIGA Dashboards.\n" +
                   "La tâche lit le pupitre chaque minute et au démarrage du PC.\n\n" + action;
        }

        public string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[" + Collector.Label + "]");
            foreach (var t in Tasks)
            {
                sb.Append("  task " + t.Name + ": exists=" + t.Exists);
                if (t.Exists)
                    sb.Append(" enabled=" + t.Enabled + " state=" + t.State +
                              " lastRun=" + t.LastRun.ToString("yyyy-MM-dd HH:mm:ss") +
                              " result=0x" + t.LastResult.ToString("X"));
                sb.AppendLine();
            }
            sb.AppendLine("  deployed script: " + (DeployedScriptExists ? (ScriptMatches ? "matches" : "DIFFERS") : "missing"));
            sb.AppendLine("  bundled files:   " + (InstallerAvailable ? "present" : "MISSING"));
            sb.Append("  healthy:         " + Healthy);
            foreach (var p in Problems()) sb.Append("\n    - " + p);
            return sb.ToString();
        }
    }

    private static Health Check(Collector c, string srcDir)
    {
        var h = new Health { Collector = c };
        string bundledScript = Path.Combine(srcDir, c.CollectorFile);
        string deployedScript = Path.Combine(c.DeployDir, c.CollectorFile);

        h.InstallerAvailable = File.Exists(bundledScript) && File.Exists(Path.Combine(srcDir, c.InstallerFile));
        h.DeployedScriptExists = File.Exists(deployedScript);
        h.ScriptMatches = h.DeployedScriptExists && h.InstallerAvailable && Sha256(bundledScript) == Sha256(deployedScript);

        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
        try
        {
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            foreach (var name in c.TaskNames)
            {
                var th = new TaskHealth { Name = name };
                dynamic task = null;
                try { task = folder.GetTask(name); }
                catch (COMException) { }        // 0x80070002: no such task
                catch (FileNotFoundException) { }
                if (task != null)
                {
                    th.Exists = true;
                    th.Enabled = task.Enabled;
                    th.State = task.State;
                    th.LastRun = task.LastRunTime;
                    th.LastResult = task.LastTaskResult;
                    if (th.State == TaskStateDisabled) th.Enabled = false;
                    Marshal.FinalReleaseComObject(task);
                }
                h.Tasks.Add(th);
            }
            Marshal.FinalReleaseComObject(folder);
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
        return h;
    }

    // Content hash ignoring line endings and a BOM: git checks the script out
    // with CRLF on one PC and LF on another, and that alone is no reason to
    // reinstall a running collector.
    private static string Sha256(string path)
    {
        string text = File.ReadAllText(path).TrimStart('\uFEFF').Replace("\r\n", "\n");
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    // ---- fix --------------------------------------------------------------

    private static (int, string) RunInstaller(string installerPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + installerPath + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath),
        };
        using var p = Process.Start(psi);
        var stderr = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(120_000)) { try { p.Kill(); } catch { } return (-1, stdout + "\n(délai dépassé)"); }
        string err = stderr.Result;
        return (p.ExitCode, string.IsNullOrWhiteSpace(err) ? stdout : stdout + "\n" + err);
    }

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int RelaunchElevated(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                                           string.Join(" ", args.Select(a => "\"" + a + "\"")))
            { UseShellExecute = true, Verb = "runas" };
            using var p = Process.Start(psi);
            p.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC refused: nothing changed.
        }
        return 0;
    }

    // ---- ui / log ---------------------------------------------------------

    private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

    // The setup window stays on screen: an invisible top-most owner keeps
    // these prompts in front of it instead of hidden behind.
    private static DialogResult Show(string text, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        using var owner = new Form { TopMost = true, ShowInTaskbar = false, Opacity = 0,
                                     FormBorderStyle = FormBorderStyle.None, Size = new System.Drawing.Size(1, 1),
                                     StartPosition = FormStartPosition.CenterScreen };
        owner.Show();
        owner.Activate();
        return MessageBox.Show(owner, text, Caption, buttons, icon);
    }

    private static bool Ask(string text) => Show(text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    private static void Info(string text) => Show(text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    private static void Error(string text) => Show(text, MessageBoxButtons.OK, MessageBoxIcon.Error);

    private static void Log(string line)
    {
        if (_logPath == null) return;
        try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { }
    }

    private static string Tail(string text, int lines) =>
        string.Join("\n", (text ?? "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Reverse().Take(lines).Reverse());

    private static string Version() =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";
}
