using ClipBar.App.Services;

namespace ClipBar.App.Tests;

/// <summary>
/// G-01: the paste keystroke must never be injected into our own window or into "unknown" (zero),
/// and the panel must never capture its own hwnd as the paste target. These guards are pure so
/// the unsafe-target rule is pinned without touching a real clipboard.
/// </summary>
public class PasteTargetTests
{
    private static readonly IntPtr SelfHwnd = new(123);
    private const uint SelfPid = 7;

    /// <summary>hwnd 55 belongs to our own process (e.g. a hidden helper window); anything else is foreign.</summary>
    private static uint PidOf(IntPtr hwnd) => hwnd.ToInt64() == 55 ? SelfPid : 999;

    [Theory]
    [InlineData(0)]    // zero: the capture lost a focus race
    [InlineData(123)]  // exactly the panel hwnd
    [InlineData(55)]   // another window of our own process
    public void KeysAreRefusedForUnknownAndSelfTargets(long target)
    {
        Assert.False(PasteTarget.CanSendKeys(new IntPtr(target), SelfHwnd, SelfPid, PidOf));
    }

    [Fact]
    public void KeysAreAllowedForARealForeignWindow()
    {
        Assert.True(PasteTarget.CanSendKeys(new IntPtr(999), SelfHwnd, SelfPid, PidOf));
    }

    [Fact]
    public void DefaultSelfParametersOnlyGuardAgainstZero()
    {
        // Callers that do not pass self info (tests) keep the zero-guard semantics.
        Assert.False(PasteTarget.CanSendKeys(IntPtr.Zero, IntPtr.Zero, 0, PidOf));
        Assert.True(PasteTarget.CanSendKeys(new IntPtr(999), IntPtr.Zero, 0, PidOf));
    }

    [Theory]
    [InlineData(0)]   // zero foreground
    [InlineData(55)]  // our own process owns the foreground (tray path, fast re-show)
    public void CaptureRefusesZeroAndOwnProcessForegrounds(long foreground)
    {
        Assert.False(PasteTarget.WorthCapturing(new IntPtr(foreground), SelfPid, PidOf));
    }

    [Fact]
    public void CaptureAcceptsAForeignForeground()
    {
        Assert.True(PasteTarget.WorthCapturing(new IntPtr(999), SelfPid, PidOf));
    }
}
