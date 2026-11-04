namespace ClipBar.App.Services;

/// <summary>
/// Wakes the panel when another launch signals the named event.
/// </summary>
/// <remarks>
/// The handle is an <see cref="EventResetMode.AutoReset"/> wait handle, so <c>WaitOne</c> returns
/// <c>true</c> on every signal and never <c>false</c>; "the wait returned" therefore cannot mean
/// "stop". Shutdown is an explicit flag: <see cref="Dispose"/> raises it, re-signals the handle to
/// release the blocked wait, and the loop then exits without running <c>onSignal</c>. Without that
/// the old code's <c>Set()</c>-before-<c>Shutdown()</c> woke the listener on the way out and
/// queued a show on an already-closed window
/// (<c>InvalidOperationException: 关闭窗口后，无法设置可见性…</c>).
/// </remarks>
public sealed class ShowSignalListener : IDisposable
{
    private readonly EventWaitHandle _handle;
    private readonly Action _onSignal;
    private readonly Action<string, Exception> _log;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private bool _disposed;

    public ShowSignalListener(
        EventWaitHandle handle,
        Action onSignal,
        Action<string, Exception> log)
    {
        _handle = handle;
        _onSignal = onSignal;
        _log = log;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ClipBar.ShowListener",
        };
        _thread.Start();
    }

    /// <summary>True once <see cref="Dispose"/> has been called; queued callbacks check this.</summary>
    public bool IsStopping => _stopping;

    private void Run()
    {
        while (true)
        {
            try
            {
                if (!_handle.WaitOne())
                {
                    break;
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            // A signal that arrives after Dispose is a shutdown race, not a user request.
            if (_stopping)
            {
                break;
            }

            try
            {
                _onSignal();
            }
            catch (Exception ex)
            {
                _log("Show listener", ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;

        // Release the blocked WaitOne so the thread can observe the flag and leave.
        try
        {
            _handle.Set();
        }
        catch (ObjectDisposedException)
        {
        }

        _thread.Join(TimeSpan.FromSeconds(2));

        _handle.Dispose();
    }
}
