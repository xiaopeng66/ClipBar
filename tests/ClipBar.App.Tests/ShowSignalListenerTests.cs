using System.Collections.Concurrent;
using ClipBar.App.Services;

namespace ClipBar.App.Tests;

/// <summary>
/// Plan §5.3 T-41, the shutdown half: the show-event listener must stop when the app exits. The
/// handle is AutoReset, so <c>WaitOne</c> never returns false and the only way out is the explicit
/// exit flag — without it, teardown woke the listener and it queued a show on a closed window.
/// </summary>
public class ShowSignalListenerTests
{
    private static EventWaitHandle NewHandle()
    {
        // A private, process-local handle: never touch the real Local\ClipBar.Show.
        var name = @"Local\ClipBar.Test." + Guid.NewGuid().ToString("N");
        return new EventWaitHandle(false, EventResetMode.AutoReset, name);
    }

    private static void WaitFor(Func<bool> condition, int milliseconds = 3000)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail($"{milliseconds} ms 内条件未成立");
    }

    [Fact]
    public void ASignalRunsTheCallback()
    {
        using var handle = NewHandle();
        var calls = new ConcurrentQueue<string>();
        using var listener = new ShowSignalListener(handle, () => calls.Enqueue("show"), (_, _) => { });

        handle.Set();
        WaitFor(() => calls.Count == 1);
    }

    [Fact]
    public void EverySignalRunsTheCallbackOnce()
    {
        using var handle = NewHandle();
        var count = 0;
        using var listener = new ShowSignalListener(
            handle,
            () => Interlocked.Increment(ref count),
            (_, _) => { });

        for (var i = 0; i < 5; i++)
        {
            handle.Set();

            // AutoReset consumes one signal per wait, so each Set must produce exactly one call.
            WaitFor(() => Volatile.Read(ref count) == i + 1);
        }

        Thread.Sleep(50);
        Assert.Equal(5, Volatile.Read(ref count));
    }

    [Fact]
    public void DisposingTheListenerDoesNotRunTheCallback()
    {
        var handle = NewHandle();
        var calls = 0;
        var listener = new ShowSignalListener(
            handle,
            () => Interlocked.Increment(ref calls),
            (_, _) => { });

        // Dispose while the thread is blocked in WaitOne: the release signal must not be
        // mistaken for a user request.
        listener.Dispose();

        Thread.Sleep(50);
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.True(listener.IsStopping);

        // Dispose is idempotent and leaves the handle closed exactly once.
        listener.Dispose();
    }

    [Fact]
    public void ACallbackThatThrowsDoesNotKillTheListener()
    {
        using var handle = NewHandle();
        var failures = new ConcurrentQueue<string>();
        var calls = 0;
        using var listener = new ShowSignalListener(
            handle,
            () =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("boom");
            },
            (context, _) => failures.Enqueue(context));

        handle.Set();
        WaitFor(() => calls == 1 && failures.Count == 1);

        // The loop must survive: a second signal still runs.
        handle.Set();
        WaitFor(() => calls == 2);
    }
}
