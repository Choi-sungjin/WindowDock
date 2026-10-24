namespace WindowLayoutManager.App;

public enum SecondInstanceOutcome
{
    /// <summary>Duplicate --start-in-tray (logon autostart): exit silently, do nothing.</summary>
    SilentAutostartDuplicate,
    /// <summary>Normal launch: the running instance was asked to show its main window.</summary>
    ActivatedExisting,
    /// <summary>Normal launch but the running instance could not be reached (e.g. other session).</summary>
    ActivationUnavailable
}

/// <summary>
/// Local IPC for "a second normal launch should show the already running app".
/// A per-session named auto-reset event (Local\...). The running instance waits on it with a
/// thread-pool registered wait (never blocks the UI thread) and marshals to its own UI thread;
/// a second launch only opens the existing event and sets it. No user window is touched.
/// The existing single-instance mutex stays the duplicate guard.
/// </summary>
public static class SingleInstanceActivation
{
    public const string ProductionEventName = @"Local\WindowLayoutManager-Activate";

    /// <summary>Isolated name for QA (scope: letters, digits, '-').</summary>
    public static string EventNameForScope(string scope)
    {
        if (string.IsNullOrEmpty(scope) || scope.Length > 64 || !scope.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ArgumentException("invalid activation scope", nameof(scope));
        return ProductionEventName + "-QA-" + scope;
    }

    /// <summary>The decision a second process makes once the mutex says an instance exists.</summary>
    public static SecondInstanceOutcome HandleSecondInstance(bool startInTray, string eventName, TimeSpan wait)
    {
        if (startInTray) return SecondInstanceOutcome.SilentAutostartDuplicate;
        return TrySignalExisting(eventName, wait)
            ? SecondInstanceOutcome.ActivatedExisting
            : SecondInstanceOutcome.ActivationUnavailable;
    }

    /// <summary>
    /// Opens the existing event and sets it. Retries briefly to cover the window where the first
    /// instance holds the mutex but has not created the event yet. Never creates the event.
    /// </summary>
    public static bool TrySignalExisting(string eventName, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(eventName, out var h))
                {
                    using (h) return h.Set();
                }
            }
            catch
            {
                // Access denied / invalid name: treat as unavailable (reported truthfully by caller).
                return false;
            }
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(100);
        }
    }
}

/// <summary>Owned by the first instance for its lifetime; Dispose stops listening.</summary>
public sealed class ActivationListener : IDisposable
{
    private readonly EventWaitHandle _event;
    private readonly RegisteredWaitHandle _registration;
    private volatile bool _disposed;
    private int _signalCount;

    public int SignalCount => Volatile.Read(ref _signalCount);

    /// <param name="onSignal">Runs on a thread-pool thread; must only marshal (e.g. BeginInvoke).</param>
    public ActivationListener(string eventName, Action onSignal)
    {
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, eventName, out _);
        _registration = ThreadPool.RegisterWaitForSingleObject(_event, (_, timedOut) =>
        {
            if (timedOut || _disposed) return;
            Interlocked.Increment(ref _signalCount);
            try { onSignal(); } catch { }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _registration.Unregister(null); } catch { }
        try { _event.Dispose(); } catch { }
    }
}
