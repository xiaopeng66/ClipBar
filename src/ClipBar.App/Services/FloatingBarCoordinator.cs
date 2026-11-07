using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClipBar.App.Platform;
using ClipBar.Core.Clipboard;
using ClipBar.Core.Models;

namespace ClipBar.App.Services;

/// <summary>
/// Drives the native floating clipboard bar: raw-input gestures in, one floating bar plus the
/// compact list out. Mirrors SnipDo's flow (hold or drag-select → small bar → hover the clipboard
/// button → list) using ClipBar's own windows, so it works without SnipDo installed.
/// <para>
/// Everything expensive happens off the input path: the raw-input callback only records a
/// timestamp and a point, the decision runs on a dispatcher timer, and the UIA query runs in
/// <see cref="SelectionProbe"/> with its own timeout.
/// </para>
/// </summary>
public sealed class FloatingBarCoordinator : IDisposable
{
    private readonly IClipRepository _repository;
    private readonly PasteService _pasteService;
    private readonly Func<AppSettings> _settings;
    private readonly Func<IntPtr> _mainWindowHandle;
    private readonly Func<bool> _isMainWindowVisible;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _gestureTimer;
    private readonly DispatcherTimer _autoHideTimer;
    private readonly SelectionProbe _probe = new();
    private readonly bool _selfElevated = Elevation.IsSelfElevated();

    private RawMouseInputService? _rawInput;
    private FloatingGestureStateMachine _machine = new(600, 15);
    private FloatingActionBarWindow? _bar;
    private CompactPanelWindow? _list;
    private OutsideClickHook? _outsideClick;
    private IntPtr _target;
    private bool _barShownAtHold;
    private long _pointerLeftAtMs;
    private bool _disposed;

    public FloatingBarCoordinator(
        IClipRepository repository,
        PasteService pasteService,
        Func<AppSettings> settings,
        Func<IntPtr> mainWindowHandle,
        Func<bool> isMainWindowVisible)
    {
        _repository = repository;
        _pasteService = pasteService;
        _settings = settings;
        _mainWindowHandle = mainWindowHandle;
        _isMainWindowVisible = isMainWindowVisible;

        _gestureTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };
        _gestureTimer.Tick += (_, _) => OnGestureTick();

        _autoHideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _autoHideTimer.Tick += (_, _) => OnAutoHideTick();
    }

    /// <summary>Starts listening. Returns false when Raw Input is unavailable.</summary>
    public bool Start()
    {
        var hwnd = _mainWindowHandle();
        if (hwnd == IntPtr.Zero)
        {
            AppLog.Write("Floating bar: main window handle not ready");
            return false;
        }

        ApplySettings();
        _rawInput = RawMouseInputService.TryAttach(hwnd);
        if (_rawInput is null)
        {
            return false;
        }

        _rawInput.LeftButtonDown += OnLeftButtonDown;
        _rawInput.LeftButtonUp += OnLeftButtonUp;
        _rawInput.DragMoved += OnDragMoved;
        _gestureTimer.Start();
        AppLog.Write("Floating bar: listening");
        return true;
    }

    /// <summary>Re-reads the gesture timings after the settings window saved.</summary>
    public void ApplySettings()
    {
        var settings = _settings();
        _machine = new FloatingGestureStateMachine(settings.HoldDurationMs, settings.DragThresholdPx);
        if (_bar is not null)
        {
            _bar.HoverExpandMs = settings.HoverExpandMs;
        }
    }

    private void OnLeftButtonDown(ScreenPoint point)
    {
        if (_disposed)
        {
            return;
        }

        // The panel being open means the user is already working in ClipBar; a gesture there is
        // not a request for another popup.
        if (_isMainWindowVisible())
        {
            return;
        }

        // A press inside the floating pair belongs to those windows (clicking a row to paste).
        // Treating it as a new gesture used to close the list before the click could land.
        if (IsInside(_bar, point.X, point.Y) || IsInside(_list, point.X, point.Y))
        {
            return;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (!IsUsableTarget(foreground))
        {
            _machine.Reset();
            return;
        }

        _target = foreground;
        _machine.Down(point.X, point.Y, _clock.ElapsedMilliseconds);
        CloseBar();
    }

    private void OnDragMoved(ScreenPoint point)
    {
        if (_disposed)
        {
            return;
        }

        _machine.Move(point.X, point.Y, _clock.ElapsedMilliseconds);
    }

    private void OnLeftButtonUp(ScreenPoint point)
    {
        if (_disposed)
        {
            return;
        }

        var action = _machine.Up(_clock.ElapsedMilliseconds, out var anchorX, out var anchorY);
        if (action != FloatingGestureStateMachineActionProbe)
        {
            return;
        }

        if (!_settings().InvokeOnDrag)
        {
            return;
        }

        var target = _target;
        var x = point.X;
        var y = point.Y;
        _ = ProbeAndShowAsync(target, x, y);
    }

    /// <summary>Alias so the machine's action name stays readable at the call site.</summary>
    private const GestureAction FloatingGestureStateMachineActionProbe = GestureAction.ProbeSelection;

    private void OnGestureTick()
    {
        if (_disposed)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out var point))
        {
            return;
        }

        var action = _machine.Tick(point.X, point.Y, _clock.ElapsedMilliseconds);
        if (action != GestureAction.ShowBarFromHold)
        {
            return;
        }

        // SnipDo's hold path opens the small bar regardless of what the pointer is over, as long
        // as the target app is reachable. The bar then offers the clipboard list.
        ShowBar(new ScreenPoint(point.X, point.Y), fromHold: true);
    }

    private async Task ProbeAndShowAsync(IntPtr target, int x, int y)
    {
        SelectionProbeResult result;
        try
        {
            result = await _probe
                .TryGetSelectionAsync(target, allowClipboardFallback: true)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Selection probe", ex);
            return;
        }

        if (!result.HasText || _disposed)
        {
            return;
        }

        ShowBar(new ScreenPoint(x, y), fromHold: false);
    }

    private void ShowBar(ScreenPoint point, bool fromHold)
    {
        if (_disposed || !IsUsableTarget(_target))
        {
            return;
        }

        CloseBar();

        var settings = _settings();
        if (!settings.NativeFloatingEnabled)
        {
            return;
        }

        CompactPanelWindow.RememberTarget(_target);

        var bar = new FloatingActionBarWindow
        {
            HoverExpandMs = settings.HoverExpandMs,
        };
        bar.ClipboardRequested += OnClipboardRequested;
        bar.Closed += (_, _) =>
        {
            if (ReferenceEquals(_bar, bar))
            {
                _bar = null;
                CloseList();
                _outsideClick?.Dispose();
                _outsideClick = null;
            }
        };

        _bar = bar;
        _barShownAtHold = fromHold;
        _pointerLeftAtMs = 0;
        bar.ShowAt(point);
        InstallOutsideClickHook();
        _autoHideTimer.Start();
        AppLog.Write($"Floating bar shown ({(fromHold ? "hold" : "drag")})");
    }

    private void OnClipboardRequested(FloatingActionBarWindow bar)
    {
        if (_disposed || _list is { IsVisible: true })
        {
            // A second request while the list is open closes it (click acts as a toggle).
            CloseList();
            bar.IsExpanded = false;
            return;
        }

        var list = new CompactPanelWindow(_repository, _pasteService, _settings);
        list.Closed += (_, _) =>
        {
            if (ReferenceEquals(_list, list))
            {
                _list = null;
                if (_bar is not null)
                {
                    _bar.IsExpanded = false;
                }
            }
        };

        _list = list;
        bar.IsExpanded = true;
        _ = list.ShowBelowAsync(bar, _target);
        AppLog.Write("Floating bar: compact list opened");
    }

    private void InstallOutsideClickHook()
    {
        _outsideClick?.Dispose();
        if (_bar is null)
        {
            return;
        }

        _outsideClick = OutsideClickHook.StartForPopup(
            () => !_disposed && (_bar is not null || _list is not null),
            (x, y) => IsInside(_bar, x, y) || IsInside(_list, x, y),
            () =>
            {
                AppLog.Write("Floating bar closed by outside click");
                CloseBar();
            });
    }

    private static bool IsInside(Window? window, int x, int y)
    {
        if (window is null)
        {
            return false;
        }

        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
    }

    private void OnAutoHideTick()
    {
        if (_disposed)
        {
            return;
        }

        if (_bar is null && _list is null)
        {
            _autoHideTimer.Stop();
            return;
        }

        var overBar = _bar?.IsPointerOver == true;
        var overList = _list?.IsPointerOver == true;
        if (overBar || overList)
        {
            _pointerLeftAtMs = 0;
            return;
        }

        if (_pointerLeftAtMs == 0)
        {
            _pointerLeftAtMs = _clock.ElapsedMilliseconds;
            return;
        }

        var limit = Math.Max(300, _settings().FloatingAutoHideMs);
        if (_clock.ElapsedMilliseconds - _pointerLeftAtMs >= limit)
        {
            AppLog.Write("Floating bar closed after the pointer left");
            CloseBar();
        }
    }

    /// <summary>Why a target is refused: self, elevated (when we are not), fullscreen, excluded.</summary>
    private bool IsUsableTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == Environment.ProcessId)
        {
            return false;
        }

        if (!_selfElevated && Elevation.IsWindowElevated(hwnd))
        {
            return false;
        }

        if (IsForegroundFullScreen(hwnd))
        {
            return false;
        }

        var name = NativeMethods.GetProcessName(hwnd);
        if (!string.IsNullOrWhiteSpace(name))
        {
            var file = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
            foreach (var excluded in _settings().ExcludedApps)
            {
                if (string.Equals(excluded, file, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(excluded, name, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsForegroundFullScreen(IntPtr hwnd)
    {
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var info = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var monitorWidth = info.Monitor.Right - info.Monitor.Left;
        var monitorHeight = info.Monitor.Bottom - info.Monitor.Top;
        return width >= monitorWidth && height >= monitorHeight;
    }

    private void CloseList()
    {
        if (_list is { } list)
        {
            _list = null;
            try
            {
                list.Close();
            }
            catch (Exception ex)
            {
                AppLog.Error("Close floating list", ex);
            }
        }
    }

    private void CloseBar()
    {
        CloseList();
        _autoHideTimer.Stop();
        _pointerLeftAtMs = 0;

        if (_bar is { } bar)
        {
            _bar = null;
            try
            {
                bar.Close();
            }
            catch (Exception ex)
            {
                AppLog.Error("Close floating bar", ex);
            }
        }

        _outsideClick?.Dispose();
        _outsideClick = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gestureTimer.Stop();
        _autoHideTimer.Stop();

        if (_rawInput is not null)
        {
            _rawInput.LeftButtonDown -= OnLeftButtonDown;
            _rawInput.LeftButtonUp -= OnLeftButtonUp;
            _rawInput.DragMoved -= OnDragMoved;
            _rawInput.Dispose();
            _rawInput = null;
        }

        CloseBar();
    }
}
