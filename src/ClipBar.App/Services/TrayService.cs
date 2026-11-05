using System.Windows;
using ClipBar.App.Platform;
using Hardcodet.Wpf.TaskbarNotification;

namespace ClipBar.App.Services;

public sealed class TrayService : IDisposable
{
    private const string DefaultTooltip = "ClipBar · 剪贴板历史";
    private const string PausedTooltip = "ClipBar · 剪贴板历史（已暂停）";

    private readonly TaskbarIcon _tray;
    private readonly Action _open;
    private readonly Action _settings;
    private readonly Action _clear;
    private readonly Action _exit;
    private readonly Func<bool> _isCapturePaused;
    private readonly Action _toggleCapture;
    private TrayMenuWindow? _menuWindow;
    private bool _disposed;

    public TrayService(
        Action open,
        Action settings,
        Action clear,
        Action exit,
        Func<bool> isCapturePaused,
        Action toggleCapture)
    {
        _open = open;
        _settings = settings;
        _clear = clear;
        _exit = exit;
        _isCapturePaused = isCapturePaused;
        _toggleCapture = toggleCapture;

        _tray = new TaskbarIcon
        {
            ToolTipText = DefaultTooltip,
            Icon = LoadIcon(),
        };

        _tray.TrayLeftMouseUp += (_, _) => open();
        _tray.TrayRightMouseUp += (_, _) => ShowMenu();
    }

    /// <summary>Reflects the capture pause in the tooltip; the menu label is rebuilt on open.</summary>
    public void UpdateCaptureState(bool paused)
        => _tray.ToolTipText = paused ? PausedTooltip : DefaultTooltip;

    /// <summary>Built on every open so the pause label reflects the current state (A2).</summary>
    private IReadOnlyList<TrayMenuEntry> BuildMenu() =>
    [
        TrayMenuEntry.Item("打开剪贴板", _open, bold: true),
        TrayMenuEntry.Separator(),
        TrayMenuEntry.Item(_isCapturePaused() ? "恢复捕获" : "暂停捕获", _toggleCapture),
        TrayMenuEntry.Item("设置", _settings),
        TrayMenuEntry.Item("清空历史", _clear),
        TrayMenuEntry.Separator(),
        TrayMenuEntry.Item("退出", _exit),
    ];

    public void ShowBalloon(string title, string message)
    {
        try
        {
            _tray.ShowBalloonTip(title, message, BalloonIcon.Info);
        }
        catch (Exception ex)
        {
            AppLog.Error("Show balloon", ex);
        }
    }

    /// <summary>Shows the Fluent tray menu anchored to the tray icon (falls back to the taskbar corner).</summary>
    private void ShowMenu()
    {
        _menuWindow?.Close();

        // GetCursorPos can fail (no interactive desktop yet). The old fallback was (0, 0),
        // which parked the menu in the top-left corner of the primary monitor; the tray lives
        // at the bottom right, so fall back to that work-area corner instead.
        var cursor = NativeMethods.GetCursorPos(out var point)
            ? point
            : DefaultAnchor();

        var window = TrayMenuWindow.Create(BuildMenu());
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menuWindow, window))
            {
                _menuWindow = null;
            }
        };
        _menuWindow = window;

        // Anchor the bottom edge to the cursor so the menu opens upward like the Win11 tray menu.
        window.ShowAt(cursor.X, cursor.Y);
    }

    /// <summary>Bottom-right corner of the primary monitor's work area, in physical pixels.</summary>
    private static NativeMethods.Point DefaultAnchor()
    {
        var info = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };

        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.Point(0, 0),
            NativeMethods.MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return new NativeMethods.Point(info.Work.Right - 8, info.Work.Bottom - 8);
        }

        // Last resort: the screen's working area via SystemParameters (DIPs → pixels).
        var area = SystemParameters.WorkArea;
        var scale = NativeMethods.MonitorScale(monitor);
        return new NativeMethods.Point(
            (int)(area.Right * scale) - 8,
            (int)(area.Bottom * scale) - 8);
    }

    private static System.Drawing.Icon? LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource?.Stream is null)
            {
                return null;
            }

            using var stream = resource.Stream;
            return new System.Drawing.Icon(stream);
        }
        catch (Exception ex)
        {
            AppLog.Error("Load tray icon", ex);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _menuWindow?.Close();
        _tray.Dispose();
    }
}
