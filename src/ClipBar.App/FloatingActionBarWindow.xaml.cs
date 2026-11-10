using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ClipBar.App.Platform;
using ClipBar.App.Services;

namespace ClipBar.App;

/// <summary>
/// The small bar the native gesture shows first: one clipboard button, shown next to the pointer
/// without taking focus. Hovering the button (or clicking it) expands the compact clipboard list,
/// The bar is built from ClipBar's own windows and does not depend on external integrations.
/// </summary>
public partial class FloatingActionBarWindow : Window
{
    private const double ScreenMargin = 6;

    private bool _expanded;
    private bool _closing;

    public FloatingActionBarWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyToolWindowStyle();
        Closed += (_, _) => _closing = true;
    }

    /// <summary>Raised when the user wants the clipboard list (hover dwell or click).</summary>
    public event Action<FloatingActionBarWindow>? ClipboardRequested;

    /// <summary>Set by the coordinator while the compact list is visible.</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set => _expanded = value;
    }

    /// <summary>True when the pointer is over the bar itself.</summary>
    public bool IsPointerOver => IsMouseOver;

    /// <summary>
    /// Builds the handle and runs one layout pass without showing the window.
    /// <para>
    /// The first gesture of a session used to pay for all of this on the press — XAML parse,
    /// template compilation, window-manager object, measure. Called at startup, the window is then
    /// ready and the first ShowBar only has to park it. Nothing is rendered here: showing a
    /// throwaway would put the logo on screen for a frame.
    /// </para>
    /// </summary>
    public void Prewarm()
    {
        new WindowInteropHelper(this).EnsureHandle();
        if (Content is FrameworkElement content)
        {
            content.Measure(new Size(30, 30));
            content.Arrange(new Rect(0, 0, 30, 30));
            content.UpdateLayout();
        }
    }

    /// <summary>Shows the bar anchored at a physical screen point, clamped to the work area.</summary>
    public void ShowAt(ScreenPoint screenPoint)
    {
        var nativePoint = new NativeMethods.Point(screenPoint.X, screenPoint.Y);
        var monitor = NativeMethods.MonitorFromPoint(nativePoint, NativeMethods.MonitorDefaultToNearest);
        var scale = NativeMethods.MonitorScale(monitor);
        var work = WorkArea(monitor);

        // Fixed DIP size: measuring the content produced a bar that shrank or grew with the
        // window DPI (27x25 DIP at 200%), which left a much smaller target. A constant size keeps
        // the hit area and the look identical on every monitor.
        var dipWidth = double.IsNaN(Width) ? 30 : Width;
        var dipHeight = double.IsNaN(Height) ? 30 : Height;
        var width = (int)Math.Round(dipWidth * scale);
        var height = (int)Math.Round(dipHeight * scale);
        var margin = (int)Math.Round(ScreenMargin * scale);
        var gapX = (int)Math.Round(8 * scale);
        var gapY = (int)Math.Round(18 * scale);

        var left = Math.Max(work.Left + margin, Math.Min(screenPoint.X + gapX, work.Right - width - margin));
        var top = screenPoint.Y + gapY + height > work.Bottom - margin
            ? Math.Max(work.Top + margin, screenPoint.Y - height - gapX)
            : screenPoint.Y + gapY;

        // Positioned in physical pixels, never through Left/Top. WPF converts those to device
        // pixels with the DPI of the monitor the window is currently on — for a window that has
        // never been shown that is the primary monitor. On a mixed-DPI desktop (a 125% primary
        // with a 200% secondary, measured here) the bar therefore appeared at the primary's scale
        // instead of the target's: "the logo is not next to the cursor" in some apps.
        //
        // Size is deliberately NOT set here: handing WPF a pixel size and then moving across a
        // DPI boundary made it scale that size again (60 px became 48 DIP at 125%, then 96 px at
        // 200%). The DIP Width/Height from XAML stay authoritative; only the position is forced.
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            left,
            top,
            0,
            0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);

        Topmost = true;
        ShowActivated = false;
        FlashFreeShow.Prepare(this);
        Show();
    }

    private void RequestClipboard()
    {
        if (_closing)
        {
            return;
        }

        ClipboardRequested?.Invoke(this);
    }

    /// <summary>True when the pointer is over the clipboard button (polled by the coordinator).</summary>
    public bool IsPointerOverButton
    {
        get
        {
            if (!IsVisible || _closing)
            {
                return false;
            }

            if (!NativeMethods.GetCursorPos(out var cursor))
            {
                return false;
            }

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                return false;
            }

            // The button fills the window, so the window rect is the hit area. It is 30 DIP while
            // the mark is 22 DIP, which leaves the pointer a margin that still counts as "on the
            // button" — the halo, not empty space.
            return cursor.X >= rect.Left && cursor.X < rect.Right
                && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
        }
    }

    private void ClipboardButton_Click(object sender, RoutedEventArgs e)
    {
        // Same rule as the list rows: pressing the logo, dragging off it and releasing is not a
        // click, so it must not open the list.
        if (!PointerInside.Element(ClipboardButton))
        {
            return;
        }

        RequestClipboard();
    }

    /// <summary>The monitor's work area in physical pixels, matching GetCursorPos and SetWindowPos.</summary>
    private static NativeMethods.Rect WorkArea(IntPtr monitor)
    {
        var info = new NativeMethods.MonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };

        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            var fallback = SystemParameters.WorkArea;
            var scale = NativeMethods.MonitorScale(monitor);
            return new NativeMethods.Rect
            {
                Left = (int)Math.Round(fallback.Left * scale),
                Top = (int)Math.Round(fallback.Top * scale),
                Right = (int)Math.Round(fallback.Right * scale),
                Bottom = (int)Math.Round(fallback.Bottom * scale),
            };
        }

        return info.Work;
    }

    private void ApplyToolWindowStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        // WS_EX_NOACTIVATE (plus MA_NOACTIVATE) made the bar swallow its own clicks: the
        // hover path worked but pressing the button did nothing. ShowActivated=false already
        // keeps the caret in the target until the user actually clicks the bar.
        NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GwlExStyle,
            new IntPtr(style | NativeMethods.WsExToolWindow));

        if (!AllowsTransparency)
        {
            NativeMethods.SetNonClientRendering(hwnd, enabled: false);
            NativeMethods.SetRoundCorners(hwnd);
            FrameBorder.Pin(this);
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // Deliberately does nothing. The bar is shown non-activating, so WPF still reports
        // deactivations it never had: showing the warm-rendered list closed the bar two
        // milliseconds after it appeared (measured), which then aborted the list's warm-up.
        // The coordinator owns the pair's lifetime — an outside click closes it immediately and
        // the auto-hide timer closes it when the pointer leaves both windows.
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
