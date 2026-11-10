namespace ClipBar.App.Services;

/// <summary>Serializes complete clipboard-write/target-activation/paste transactions.</summary>
internal sealed class PasteTransactionGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        await _semaphore.WaitAsync().ConfigureAwait(true);
        try
        {
            return await operation().ConfigureAwait(true);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
