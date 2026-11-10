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
    private readonly EventWaitHandle _showEventLifetime;
    private readonly EventWaitHandle? _compactEventLifetime;
    private readonly bool _owned;
    private bool _disposed;

    private SingleInstanceGuard(
        Mutex mutex,
        EventWaitHandle showEventLifetime,
        EventWaitHandle? compactEventLifetime,
        bool owned)
    {
        _mutex = mutex;
        _showEventLifetime = showEventLifetime;
        _compactEventLifetime = compactEventLifetime;
        _owned = owned;
    }

    /// <summary>
    /// Takes ownership of <paramref name="mutexName"/>. Returns <c>null</c> when another
    /// instance already owns it, after waking that instance through one of the named events.
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(
        string mutexName,
        string showEventName,
        string? compactEventName = null,
        bool requestCompact = false)
    {
        // Publish both named events before the mutex becomes visible. A second launch can then
        // signal during startup even before the owner opens its dispatcher listeners.
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, showEventName);
        EventWaitHandle? compactEvent = null;
        if (!string.IsNullOrWhiteSpace(compactEventName))
        {
            try
            {
                compactEvent = new EventWaitHandle(false, EventResetMode.AutoReset, compactEventName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                AppLog.Error("Create compact event", ex);
            }
        }

        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, mutexName, out createdNew);
        }
        catch
        {
            compactEvent?.Dispose();
            showEvent.Dispose();
            throw;
        }

        if (!createdNew)
        {
            // Another instance is live: preserve the requested picker instead of always opening
            // the full panel. Releasing is not ours to do — the kernel object belongs to the owner.
            var signal = requestCompact && compactEvent is not null ? compactEvent : showEvent;
            try
            {
                signal.Set();
            }
            catch (UnauthorizedAccessException)
            {
            }

            compactEvent?.Dispose();
            showEvent.Dispose();
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(mutex, showEvent, compactEvent, owned: true);
    }

    /// <summary>
    /// Creates the event the owner listens on. Created before any window exists, so a second
    /// launch during startup is not lost.
    /// </summary>
    public EventWaitHandle CreateShowEvent()
        => _showEventLifetime;

    public EventWaitHandle? CreateCompactEvent()
        => _compactEventLifetime;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _compactEventLifetime?.Dispose();
        _showEventLifetime.Dispose();

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
