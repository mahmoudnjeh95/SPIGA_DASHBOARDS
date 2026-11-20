using System.Runtime.CompilerServices;

namespace Spiga.Data;

// "await OffUiThread.Switch();" first thing in a data method: the rest of
// it (connection, query, reading the rows, the arithmetic on them) runs on
// the thread pool, never on the WPF / Blazor UI thread, which stays free to
// draw and answer clicks. The caller's own await still comes back to its
// thread. SQLite in particular has no real async I/O: without this, its
// queries -- and its waits on a locked file -- froze the whole window.
public readonly struct OffUiThread : ICriticalNotifyCompletion
{
    public static OffUiThread Switch() => default;

    public OffUiThread GetAwaiter() => this;

    // Already on a pool thread without a context (web host, tests): no hop.
    public bool IsCompleted => Thread.CurrentThread.IsThreadPoolThread && SynchronizationContext.Current == null;

    public void GetResult() { }

    public void OnCompleted(Action continuation) => ThreadPool.QueueUserWorkItem(static c => c(), continuation, preferLocal: false);

    public void UnsafeOnCompleted(Action continuation) => ThreadPool.UnsafeQueueUserWorkItem(static c => c(), continuation, preferLocal: false);
}
