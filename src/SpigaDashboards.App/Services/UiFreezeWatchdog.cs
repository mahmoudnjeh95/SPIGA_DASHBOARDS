using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace SpigaDashboards.App.Services;

// Logs every time the UI thread stops answering for longer than Threshold
// ("Interface bloquée 2,4 s"): what makes the screen freeze (nothing can be
// clicked) is then in connection.log, with when it happened. A background
// thread posts a no-op to the dispatcher every Period and measures how long
// it waits to run.
public static class UiFreezeWatchdog
{
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(700);
    private static int _started;

    public static void Start(Dispatcher dispatcher)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;
        var thread = new Thread(() => Run(dispatcher)) { IsBackground = true, Name = "UiFreezeWatchdog", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    private static void Run(Dispatcher dispatcher)
    {
        var answered = new ManualResetEventSlim();
        while (!dispatcher.HasShutdownStarted)
        {
            answered.Reset();
            var clock = Stopwatch.StartNew();
            // Input priority: what a click waits for.
            dispatcher.BeginInvoke(DispatcherPriority.Input, () => answered.Set());
            if (!answered.Wait(Threshold))
            {
                answered.Wait();
                if (dispatcher.HasShutdownStarted)
                    return;
                AppLogger.Log($"Interface bloquée {clock.Elapsed.TotalSeconds:0.0} s (le poste ne répondait plus aux clics).");
            }
            Thread.Sleep(Period);
        }
    }
}
