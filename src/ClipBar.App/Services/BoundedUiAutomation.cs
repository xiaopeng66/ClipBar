namespace ClipBar.App.Services;

/// <summary>
/// Runs cross-process UI Automation without letting a stalled provider block the caller or spawn
/// an unbounded number of additional workers. A provider that ignores the timeout keeps the single
/// worker slot until it actually returns; later probes fail closed in the meantime.
/// </summary>
internal static class BoundedUiAutomation
{
    private static readonly SemaphoreSlim WorkerGate = new(1, 1);

    public static async Task<T> RunAsync<T>(
        Func<T> operation,
        TimeSpan timeout,
        T fallback,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await WorkerGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return fallback;
            }
        }
        catch (Exception)
        {
            return fallback;
        }

        Task<T> worker;
        try
        {
            worker = Task.Run(() =>
            {
                try
                {
                    return operation();
                }
                finally
                {
                    WorkerGate.Release();
                }
            });
        }
        catch (Exception)
        {
            WorkerGate.Release();
            return fallback;
        }

        try
        {
            return await worker.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
