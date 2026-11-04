using System.IO;

namespace ClipBar.App.Services;

/// <summary>
/// Process-wide single-instance gate: one owner plus a wake-up signal for later launches.
/// </summary>
/// <remarks>
/// Extracted from <c>App.OnStartup</c> so the race in plan §5.3 T-41 is testable in-process
/// instead of needing five real windows on a live desktop. A second launch never starts a
/// second app: it signals the owner's event and exits.
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _showEventName;
    private readonly bool _owned;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex, string showEventName, bool owned)
    {
        _mutex = mutex;
        _showEventName = showEventName;
        _owned = owned;
    }

    /// <summary>
    /// Takes ownership of <paramref name="mutexName"/>. Returns <c>null</c> when another
    /// instance already owns it, after waking that instance through <paramref name="showEventName"/>.
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(string mutexName, string showEventName)
    {
        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            // Another instance is live: wake it and let the caller exit. Releasing is not ours
            // to do — the kernel object belongs to the owner.
            SignalExisting(showEventName);
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(mutex, showEventName, owned: true);
    }

    /// <summary>
    /// Creates the event the owner listens on. Created before any window exists, so a second
    /// launch during startup is not lost.
    /// </summary>
    public EventWaitHandle CreateShowEvent()
        => new(false, EventResetMode.AutoReset, _showEventName);

    private static void SignalExisting(string showEventName)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(showEventName);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Owner has not created the event yet; it will show itself on startup.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_owned)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owning thread (shutdown from a background thread): the handle close
                // below still releases the kernel object.
            }
        }

        _mutex.Dispose();
    }
}
