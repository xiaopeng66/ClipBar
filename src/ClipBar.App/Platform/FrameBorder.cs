using System.Windows;
using System.Windows.Interop;

namespace ClipBar.App.Platform;

/// <summary>
/// Pins the Win11 DWM border to one fixed colour for every window that draws its own surface:
/// the tray menu, dialogs and the settings window. The default border colour follows the
/// active/inactive state and the system theme, which on a dark app surface reads as the
/// "white edge" around a menu or dialog. DWMWA_COLOR_NONE is not the alternative — it made
/// the frame transparent, so the desktop (or the light taskbar under the tray menu) shone
/// through as the same light band; that was the panel's original defect. The panel has pinned
/// <see cref="Color"/> at SourceInitialized/Activated/Deactivated since (MainWindow), and the
/// re-application matters: an activation transition repaints the frame with the default colour.
/// </summary>
public static class FrameBorder
{
    /// <summary>Dark hairline for the dark surface (the panel's original pinned colour).</summary>
    public const uint DarkColor = 0x00202020;

    /// <summary>Light hairline for the light surface: visible against white without shouting.</summary>
    public const uint LightColor = 0x00D6D6D6;

    /// <summary>
    /// The pinned colour for the currently applied app theme. The old single dark colour read
    /// as a black border around the light-theme windows (the user's 浅色模式黑边).
    /// </summary>
    public static uint CurrentColor => Services.ThemeService.AppliedTheme == Wpf.Ui.Appearance.ApplicationTheme.Light
        ? LightColor
        : DarkColor;

    /// <summary>Re-pins an already shown window after a theme switch.</summary>
    public static void Repin(IntPtr hwnd) => NativeMethods.SetBorderColor(hwnd, CurrentColor);

    /// <summary>
    /// Pins the border colour now and after every event that can make DWM repaint the frame
    /// (source initialisation, first render, activation, deactivation).
    /// </summary>
    public static void Pin(Window window)
        => Pin(window, static (hwnd, _) => NativeMethods.SetBorderColor(hwnd, CurrentColor));

    /// <summary>
    /// The wiring is testable because DWMWA_BORDER_COLOR is set-only: DwmGetWindowAttribute
    /// rejects a read-back with E_INVALIDARG, so the only observable is the call itself.
    /// </summary>
    internal static void Pin(Window window, Action<IntPtr, uint> apply)
    {
        void Apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                apply(hwnd, CurrentColor);
            }
        }

        window.Activated += (_, _) => Apply();
        window.Deactivated += (_, _) => Apply();
        window.SourceInitialized += (_, _) => Apply();
        window.ContentRendered += (_, _) => Apply();

        // A re-shown window (the settings window is cached) already has its HWND.
        if (window.IsLoaded)
        {
            Apply();
        }
    }
}
