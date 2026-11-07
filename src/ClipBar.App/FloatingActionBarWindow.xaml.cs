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

    private readonly System.Windows.Threading.DispatcherTimer _hoverTimer;
    private bool _expanded;
    private bool _closing;

    public FloatingActionBarWindow()
    {
        InitializeComponent();
        _hoverTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (!_closing && ClipboardButton.IsMouseOver)
            {
                RequestClipboard();
            }
        };

        SourceInitialized += (_, _) => ApplyToolWindowStyle();
        NonActivatingWindow.Attach(this);
        Closed += (_, _) =>
        {
            _closing = true;
            _hoverTimer.Stop();
        };
    }

    /// <summary>Raised when the user wants the clipboard list (hover dwell or click).</summary>
    public event Action<FloatingActionBarWindow>? ClipboardRequested;

    /// <summary>Set by the coordinator while the compact list is visible.</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set => _expanded = value;
    }

    /// <summary>Hover dwell before the list expands; wired from settings.</summary>
    public int HoverExpandMs
    {
        get => (int)_hoverTimer.Interval.TotalMilliseconds;
        set => _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(value, 0, 2000));
    }

    /// <summary>True when the pointer is over the bar itself.</summary>
    public bool IsPointerOver => IsMouseOver;

    /// <summary>Shows the bar anchored at a physical screen point, clamped to the work area.</summary>
    public void ShowAt(ScreenPoint screenPoint)
    {
        var nativePoint = new NativeMethods.Point(screenPoint.X, screenPoint.Y);
        var monitor = NativeMethods.MonitorFromPoint(nativePoint, NativeMethods.MonitorDefaultToNearest);
        var scale = NativeMethods.MonitorScale(monitor);
        var work = WorkArea(monitor, scale);

        // Measure before the first frame so placement uses the real size (the panel did the same
        // for its menus: sizing after Show painted one wrong frame).
        if (Content is FrameworkElement root)
        {
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (root.DesiredSize.Width > 0)
            {
                Width = Math.Ceiling(root.DesiredSize.Width);
                Height = Math.Ceiling(root.DesiredSize.Height);
            }
        }

        var width = double.IsNaN(Width) ? 64 : Width;
        var height = double.IsNaN(Height) ? 40 : Height;
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

    private void ClipboardButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_closing && !_expanded)
        {
            _hoverTimer.Stop();
            _hoverTimer.Start();
        }
    }

    private void ClipboardButton_MouseLeave(object sender, MouseEventArgs e) => _hoverTimer.Stop();

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
        NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GwlExStyle,
            new IntPtr(style | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate));

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
