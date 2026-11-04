namespace ClipBar.App.Services;

/// <summary>
/// Pure guards for the paste keystroke and for capturing the paste target. The panel must never
/// send Ctrl+V to itself, and a zero or self-owned hwnd must downgrade to a plain copy instead of
/// injecting keys into whatever happens to be foreground. Measured root causes behind the
/// "clicking a clip pasted nothing" report: the tray path and a fast Esc→show cycle both put a
/// shell or our own window in the foreground at capture time.
/// </summary>
public static class PasteTarget
{
    /// <summary>Whether Ctrl+V may be sent to <paramref name="targetHwnd"/>.</summary>
    public static bool CanSendKeys(
        IntPtr targetHwnd,
        IntPtr selfHwnd,
        uint selfProcessId,
        Func<IntPtr, uint> processIdOf)
        => targetHwnd != IntPtr.Zero
            && targetHwnd != selfHwnd
            && processIdOf(targetHwnd) != selfProcessId;

    /// <summary>
    /// Whether a foreground window is worth capturing as the paste target: our own windows and
    /// zero are not (zero cannot be activated; our own hwnd would self-paste).
    /// </summary>
    public static bool WorthCapturing(
        IntPtr foregroundHwnd,
        uint selfProcessId,
        Func<IntPtr, uint> processIdOf)
        => foregroundHwnd != IntPtr.Zero
            && processIdOf(foregroundHwnd) != selfProcessId;
}
