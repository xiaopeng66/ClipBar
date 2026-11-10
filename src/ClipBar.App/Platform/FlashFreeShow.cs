using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClipBar.App.Platform;

/// <summary>
/// Removes the "first frame" flicker from transient windows (menus, dialogs, toasts).
/// A window is shown while DWM-cloaked, so nothing is composited until WPF has laid out and
/// rendered the real content; the cloak is released right after the first content render (or,
/// as a fallback, on the next idle turn). This is the technique STranslate uses for its main
/// window and it is the only reliable way to hide the half-painted frame WPF-UI produces while
/// it applies the window surface and template.
/// </summary>
public static class FlashFreeShow
{
    /// <summary>
    /// Call immediately before <see cref="Window.Show"/> / <see cref="Window.ShowDialog"/>.
    /// The cloak is installed at <c>SourceInitialized</c> (the HWND does not exist before that)
    /// and released after the first rendered frame; <paramref name="afterFirstFrame"/> receives
    /// focus/activation work that must not run while the window is still invisible.
    /// </summary>
    public static void Prepare(Window window, Action? afterFirstFrame = null)
    {
        var hwnd = IntPtr.Zero;
        var released = false;

        void Release()
        {
            if (released)
            {
                return;
            }

            released = true;

            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowCloaked(hwnd, false);
            }

            afterFirstFrame?.Invoke();
        }

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            hwnd = new WindowInteropHelper(window).Handle;
            NativeMethods.SetWindowCloaked(hwnd, true);
        }

        // The handle can already exist: a window prepared ahead of time calls EnsureHandle so the
        // HWND creation cost stays off the input path. SourceInitialized has fired by then and
        // will not fire again, so cloak right here instead of never.
        var existing = new WindowInteropHelper(window).Handle;
        if (existing != IntPtr.Zero)
        {
            hwnd = existing;
            NativeMethods.SetWindowCloaked(hwnd, true);
        }
        else
        {
            window.SourceInitialized += OnSourceInitialized;
        }

        void OnContentRendered(object? sender, EventArgs e)
        {
            window.ContentRendered -= OnContentRendered;

            // ContentRendered means WPF finished its own render pass, but DWM may not have
            // presented that frame yet. Uncloaking immediately occasionally exposed the
            // window's unpainted surface for one frame (measured: right-click menus flashed
            // light then dark). Wait for two composited frames before revealing the window.
            var frames = 0;
            EventHandler? onRendering = null;
            onRendering = (_, _) =>
            {
                if (++frames < 2)
                {
                    return;
                }

                System.Windows.Media.CompositionTarget.Rendering -= onRendering;
                Release();
            };
            System.Windows.Media.CompositionTarget.Rendering += onRendering;

            // If the window never renders (hidden/closed), the idle fallback still releases.
        }

        window.ContentRendered += OnContentRendered;

        // Fallback for instances that already rendered once (a re-shown settings window):
        // ContentRendered will not fire again, and the idle turn still runs after layout.
        _ = window.Dispatcher.BeginInvoke(new Action(Release), DispatcherPriority.ApplicationIdle);
    }
}
