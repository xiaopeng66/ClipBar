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
/// which mirrors SnipDo's floating bar → clipboard list flow without depending on SnipDo.
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

    /// <summary>Hover dwell before the list expands; the coordinator polls the pointer.</summary>
    public int HoverExpandMs { get; set; } = 200;

    /// <summary>True when the pointer is over the bar itself.</summary>
    public bool IsPointerOver => IsMouseOver;

    /// <summary>Shows the bar anchored at a physical screen point, clamped to the work area.</summary>
    public void ShowAt(ScreenPoint screenPoint)
    {
        var nativePoint = new NativeMethods.Point(screenPoint.X, screenPoint.Y);
        var monitor = NativeMethods.MonitorFromPoint(nativePoint, NativeMethods.MonitorDefaultToNearest);
        var scale = NativeMethods.MonitorScale(monitor);
        var work = WorkArea(monitor, scale);

        // Fixed DIP size: measuring the content produced a bar that shrank or grew with the
        // window DPI (27x25 DIP at 200%), which left a much smaller target than SnipDo's 40 DIP
        // bar. A constant size keeps the hit area and the look identical on every monitor.
        var width = double.IsNaN(Width) ? 40 : Width;
        var height = double.IsNaN(Height) ? 36 : Height;
        var anchorX = screenPoint.X / scale;
        var anchorY = screenPoint.Y / scale;

        var left = Math.Max(work.Left + ScreenMargin, Math.Min(anchorX + 8, work.Right - width - ScreenMargin));
        var top = anchorY + 18 + height > work.Bottom - ScreenMargin
            ? Math.Max(work.Top + ScreenMargin, anchorY - height - 8)
            : anchorY + 18;

        Left = left;
        Top = Math.Max(work.Top + ScreenMargin, top);
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

            // The button is the window minus the 4 DIP padding on each side; at the 46x42 DIP
            // size that is the whole interior, so the window rect is a good enough hit area.
            return cursor.X >= rect.Left && cursor.X < rect.Right
                && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
        }
    }

    private void ClipboardButton_Click(object sender, RoutedEventArgs e) => RequestClipboard();

    private static Rect WorkArea(IntPtr monitor, double scale)
    {
        var info = new NativeMethods.MonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };

        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            var fallback = SystemParameters.WorkArea;
            return new Rect(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
        }

        var work = info.Work;
        return new Rect(
            work.Left / scale,
            work.Top / scale,
            (work.Right - work.Left) / scale,
            (work.Bottom - work.Top) / scale);
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

        NativeMethods.SetNonClientRendering(hwnd, enabled: false);
        NativeMethods.SetRoundCorners(hwnd);
        FrameBorder.Pin(this);
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // The bar is non-activating by design, but WPF can still deliver a transient
        // deactivation while the compact list opens. The coordinator owns the pair's lifetime
        // (pointer leaves both → close), so never close just the bar while the list is up.
        if (_expanded)
        {
            return;
        }

        AppLog.Write($"Floating bar deactivated (pointer over={IsMouseOver})");
        if (!_closing && !IsMouseOver)
        {
            Close();
        }
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
