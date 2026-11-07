using System.Windows;
using System.Windows.Interop;

namespace ClipBar.App.Platform;

/// <summary>
/// Keeps a non-activating popup clickable.
/// <para>
/// <c>WS_EX_NOACTIVATE</c> alone stops the window from taking focus, but the click that would
/// have activated it can be swallowed: the compact list stayed on screen and its rows ignored
/// every mouse click. SnipDo's bars answer <c>WM_MOUSEACTIVATE</c> with <c>MA_NOACTIVATE</c> for
/// the same reason (see docs/PLAN-NATIVE-FLOATING-CLIPBOARD.md); this helper does it for a WPF
/// window.
/// </para>
/// </summary>
internal static class NonActivatingWindow
{
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    /// <summary>Installs the message hook. Safe to call once per window.</summary>
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
        };
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmMouseActivate)
        {
            return IntPtr.Zero;
        }

        // Let the click through without activating, so the row under the pointer receives it.
        handled = true;
        return new IntPtr(MaNoActivate);
    }
}
