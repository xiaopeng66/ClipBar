using ClipBar.App.Services;

namespace ClipBar.App.Tests;

/// <summary>
/// Plan §5.3 T-41, the single-instance half. Five concurrent launches used to be a manual
/// test; the gate is extracted so the "exactly one owner, later launches only wake it" rule is
/// verifiable in-process, with no real windows and no second process.
/// </summary>
public class SingleInstanceGuardTests
{
    private const string ShowEventSuffix = ".Show";

    /// <summary>A per-test name pair so the real Local\ClipBar.* objects are never touched.</summary>
    private static (string MutexName, string EventName) NewNames()
    {
        var id = "Local\\ClipBar.Test." + Guid.NewGuid().ToString("N");
        return (id + ".SingleInstance", id + ShowEventSuffix);
    }

    [Fact]
    public void TheFirstAcquireOwnsAndALaterAcquireDoesNot()
    {
        var (mutexName, eventName) = NewNames();

        using var owner = SingleInstanceGuard.TryAcquire(mutexName, eventName);
        Assert.NotNull(owner);

        // A second launch must not become a second resident instance.
        Assert.Null(SingleInstanceGuard.TryAcquire(mutexName, eventName));
    }

    [Fact]
    public void ALaterAcquireWakesTheOwnerThroughTheShowEvent()
    {
        var (mutexName, eventName) = NewNames();

        using var owner = SingleInstanceGuard.TryAcquire(mutexName, eventName);
        Assert.NotNull(owner);
        using var showEvent = owner!.CreateShowEvent();

        // This is what --show and the SnipDo action rely on: the launch that loses the race
        // still makes the panel appear.
        Assert.Null(SingleInstanceGuard.TryAcquire(mutexName, eventName));
        Assert.True(showEvent.WaitOne(TimeSpan.FromSeconds(2)), "第二次启动没有唤醒已运行的实例");
    }

    [Fact]
    public void ALaterAcquireWithoutAShowEventDoesNotThrow()
    {
        var (mutexName, eventName) = NewNames();

        using var owner = SingleInstanceGuard.TryAcquire(mutexName, eventName);
        Assert.NotNull(owner);

        // Startup ordering: a second launch can land before the owner created the event. Losing
        // that signal is acceptable; crashing the second launch is not.
        Assert.Null(SingleInstanceGuard.TryAcquire(mutexName, eventName));
    }

    [Fact]
    public void ReleasingTheOwnerLetsTheNextLaunchBecomeTheOwner()
    {
        var (mutexName, eventName) = NewNames();

        var owner = SingleInstanceGuard.TryAcquire(mutexName, eventName);
        Assert.NotNull(owner);
        owner!.Dispose();

        // No zombie lock: after a clean exit the next launch starts normally.
        using var next = SingleInstanceGuard.TryAcquire(mutexName, eventName);
        Assert.NotNull(next);

        // Dispose is idempotent.
        owner.Dispose();
    }
}
