using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClipBar.App.Platform;
using ClipBar.Core.Clipboard;
using ClipBar.Core.Models;

namespace ClipBar.App.Services;

/// <summary>
/// Drives the native floating clipboard bar: raw-input gestures show a small bar, and hovering
/// or clicking its clipboard button opens the compact list. Both windows belong to ClipBar.
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

    /// <summary>
    /// Set by the app: true when the pointer is over the main panel's own search box. The panel is
    /// this process's WPF window, so the UIA gate is not the right judge for it.
    /// </summary>
    private readonly Func<bool>? _isPointerOverOwnTextInput;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _gestureTimer;
    private readonly DispatcherTimer _autoHideTimer;
    private readonly SelectionProbe _probe = new();
    private readonly TextTargetGate _textGate = new();
    private readonly bool _selfElevated = Elevation.IsSelfElevated();

    private RawMouseInputService? _rawInput;
    private FloatingGestureStateMachine _machine = new(600, 15);
    private FloatingActionBarWindow? _bar;
    private CompactPanelWindow? _list;

    /// <summary>
    /// The list built while the bar was on screen but before the pointer reached it. Constructing
    /// the window and its first rows here keeps that cost off the hover path.
    /// </summary>
    private CompactPanelWindow? _preparedList;
    private OutsideClickHook? _outsideClick;
    private IntPtr _target;

    /// <summary>Where the current bar was anchored, so the warm render can pick its monitor.</summary>
    private ScreenPoint? _lastAnchor;

    private long _pointerLeftAtMs;

    /// <summary>
    /// True while the press in flight started on an editable surface. Resolved asynchronously
    /// right after the press; the hold and drag paths read it before producing a popup.
    /// </summary>
    private bool _pressIsTextTarget;

    /// <summary>True once the gate answered for the current press (either verdict).</summary>
    private bool _pressGateResolved;

    /// <summary>Press timestamp, used to start the list warm-up while the user is still holding.</summary>
    private long _pressAtMs;

    /// <summary>True once the list warm-up was started for the current press.</summary>
    private bool _prepareKicked;

    /// <summary>Incremented per press so a slow gate result cannot land on a newer gesture.</summary>
    private int _pressEpoch;

    /// <summary>
    /// True while the bar/list belong to a gesture that started in ClipBar's own search box.
    /// A row click then inserts text instead of sending Ctrl+V to our own process.
    /// </summary>
    private bool _targetIsOwnTextInput;

    /// <summary>Set by the app: inserts a clip into the panel's search box.</summary>
    private readonly Func<ClipItem, bool>? _insertIntoOwnTextInput;

    /// <summary>Set by the app: stops the main panel auto-hiding while the floating pair is up.</summary>
    private readonly Func<IDisposable>? _suppressPanelHide;

    /// <summary>Active suppression for the panel, disposed when the pair closes.</summary>
    private IDisposable? _panelHold;

    /// <summary>
    /// Rows fetched while the bar is on screen, so the hover expand is a render instead of a
    /// query. Cancelled with the bar.
    /// </summary>
    private Task<IReadOnlyList<ClipItem>>? _prefetch;

    /// <summary>
    /// A bar built at startup, waiting for the first gesture. It holds no data, so keeping it is
    /// safe: the first ShowBar only parks and shows it instead of constructing one from cold.
    /// </summary>
    private FloatingActionBarWindow? _preparedBar;

    private bool _disposed;

    public FloatingBarCoordinator(
        IClipRepository repository,
        PasteService pasteService,
        Func<AppSettings> settings,
        Func<IntPtr> mainWindowHandle,
        Func<bool>? isPointerOverOwnTextInput = null,
        Func<ClipItem, bool>? insertIntoOwnTextInput = null,
        Func<IDisposable>? suppressPanelHide = null)
    {
        _repository = repository;
        _pasteService = pasteService;
        _settings = settings;
        _mainWindowHandle = mainWindowHandle;
        _isPointerOverOwnTextInput = isPointerOverOwnTextInput;
        _insertIntoOwnTextInput = insertIntoOwnTextInput;
        _suppressPanelHide = suppressPanelHide;

        // One frame at 60 Hz: the hold fires on the first tick after its threshold, so the tick
        // interval used to add up to 40 ms to every long-press. Runs only while a button is held
        // (see OnLeftButtonDown), so the tighter interval costs nothing at idle.
        _gestureTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _gestureTimer.Tick += (_, _) => OnGestureTick();

        // ~one frame at 60 Hz. The hover dwell is short, so a coarse poll was the dominant delay
        // in "hovering the logo takes a moment to open the list". The tick only reads
        // GetCursorPos and two window rects, so frame rate is affordable.
        _autoHideTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16),
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
        _rawInput.PointerMoved += OnPointerMoved;
        AppLog.Write("Floating bar: listening");

        // Loading the UIA client costs ~600 ms the first time it is used in a process. Paying it
        // at startup keeps the first press-and-hold from stalling on it: the gate runs on the
        // press path, and a cold verdict would otherwise arrive after the hold had fired.
        _ = TextTargetGate.WarmUpAsync();

        // Same idea for everything else the first gesture needs. Measured before this existed:
        // the first bar of a session appeared 60 ms later after the press than every later one.
        _ = PrewarmAsync();
        return true;
    }

    /// <summary>
    /// Pays the first gesture's one-time costs at startup: the database's first query, the bar's
    /// handle/template/layout, and one full build of the compact list. Delayed and at background
    /// priority so the app's own startup is not held up by it.
    /// <para>
    /// The rows fetched here are thrown away and the list is closed again: a list kept from startup
    /// would show a stale history. What is kept is the finished bar, which carries no data.
    /// </para>
    /// </summary>
    private async Task PrewarmAsync()
    {
        try
        {
            // The trivially reachable case first: startup itself must not get slower.
            await Task.Delay(800).ConfigureAwait(true);

            // Let the tray and the panel finish whatever startup work they have queued.
            await Dispatcher.Yield(DispatcherPriority.Background);

            if (_disposed || !_settings().NativeFloatingEnabled)
            {
                return;
            }

            // 1. The database's first query: opening the file and preparing the statements.
            var rows = await PrefetchRowsAsync().ConfigureAwait(true);
            if (_disposed || _bar is not null || _list is not null)
            {
                return;
            }

            // 2. A finished bar for the first gesture to take over.
            if (_preparedBar is null)
            {
                var bar = new FloatingActionBarWindow();
                bar.Prewarm();
                if (_disposed || _bar is not null)
                {
                    // A gesture beat us to it and made its own bar.
                    bar.Close();
                    return;
                }

                _preparedBar = bar;
            }

            // 3. One full build of the list, closed again: parsing its XAML, compiling the row
            //    template, building the rows and laying the window out are the costs, not the
            //    instance.
            var anchor = PointerAnchor();
            var list = CreateListWindow();
            await list.PrepareAsync(IntPtr.Zero, rows).ConfigureAwait(true);
            list.WarmRender(anchor);
            list.Close();

            AppLog.Write("Floating bar: prewarmed");
        }
        catch (Exception ex)
        {
            AppLog.Error("Prewarm floating bar", ex);
        }
    }

    /// <summary>
    /// Where to warm-render: the pointer, i.e. the monitor the user is working on, so the warm-up
    /// resolves that monitor's DPI instead of the primary's.
    /// </summary>
    private static ScreenPoint PointerAnchor()
    {
        if (NativeMethods.GetCursorPos(out var point))
        {
            return new ScreenPoint(point.X, point.Y);
        }

        return new ScreenPoint(0, 0);
    }

    /// <summary>Re-reads the gesture timings after the settings window saved.</summary>
    public void ApplySettings()
    {
        var settings = _settings();
        _machine = new FloatingGestureStateMachine(settings.HoldDurationMs, settings.DragThresholdPx);
    }

    private void OnLeftButtonDown(ScreenPoint point)
    {
        if (_disposed)
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
        var inOwnTextInput = _isPointerOverOwnTextInput?.Invoke() == true;
        if (!inOwnTextInput && !IsUsableTarget(foreground))
        {
            _machine.Reset();
            return;
        }

        // Pasted into by direct insertion, not Ctrl+V: the target is this process, and a
        // keystroke aimed at ourselves would be refused by PasteService's self-paste guard.
        _targetIsOwnTextInput = inOwnTextInput;
        _target = foreground;
        CloseBar();

        // Two-stage gate. The pointer's shape is read right here and is cheap: a standard
        // non-text cursor ends the gesture with no cross-process call at all. Only an I-beam or
        // an unrecognised custom cursor escalates to UI Automation, which runs in the background
        // while the user is still holding the button down.
        var cursor = CursorShape.Read();
        _pressIsTextTarget = false;
        _pressGateResolved = false;
        _pressAtMs = _clock.ElapsedMilliseconds;
        _prepareKicked = false;
        _machine.Down(point.X, point.Y, _clock.ElapsedMilliseconds);

        // The hold is detected by polling the pointer, so the timer runs exactly while a press is
        // in flight and stops again on release.
        _gestureTimer.Start();

        if (cursor == CursorShapeKind.KnownNonText)
        {
            _pressGateResolved = true;
            _machine.Reset();
            return;
        }

        var epoch = ++_pressEpoch;
        var x = point.X;
        var y = point.Y;
        _ = ResolveTextTargetAsync(cursor, x, y, epoch, inOwnTextInput);
    }

    /// <summary>
    /// Resolves whether the current press started on an editable surface. Our own panel answers
    /// for itself (the UIA gate cannot classify a WPF window from a screen point reliably); every
    /// other app goes through the gate. A result that arrives after the press ended or after a
    /// newer press began is discarded.
    /// </summary>
    private async Task ResolveTextTargetAsync(CursorShapeKind cursor, int x, int y, int epoch, bool ownTextInput)
    {
        bool isTextTarget;
        if (ownTextInput)
        {
            isTextTarget = true;
        }
        else
        {
            try
            {
                isTextTarget = await _textGate.IsTextTargetAsync(cursor, x, y).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Text target gate", ex);
                isTextTarget = false;
            }
        }

        if (_disposed || epoch != _pressEpoch)
        {
            // A new press superseded this one; its own check owns the verdict.
            return;
        }

        _pressIsTextTarget = isTextTarget;
        _pressGateResolved = true;
        if (!isTextTarget)
        {
            // Drop the in-flight gesture now: no hold timer, no drag probe.
            _machine.Reset();
        }
    }

    /// <summary>
    /// A pointer move with no button held. The only thing that needs it is the hover-expand: the
    /// 16 ms poll used to be the largest remaining delay before the list appeared, and this event
    /// arrives with the packet itself.
    /// </summary>
    private void OnPointerMoved(ScreenPoint point)
    {
        if (_disposed || _bar is not { IsExpanded: false } bar)
        {
            return;
        }

        if (bar.IsPointerOverButton)
        {
            OnClipboardRequested(bar);
        }
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

        _gestureTimer.Stop();
        var action = _machine.Up(_clock.ElapsedMilliseconds, out var anchorX, out var anchorY);
        var resolved = _pressGateResolved;
        var textTarget = _pressIsTextTarget;
        _pressGateResolved = false;

        if (action != FloatingGestureStateMachineActionProbe)
        {
            // The press ended without a drag-select. Only a press that produced no bar at all
            // leaves a useless warm-up behind: a hold that fired has a bar on screen and is
            // about to need exactly that list on hover.
            if (_bar is null)
            {
                CloseList();
            }

            return;
        }

        // The machine consumed the gesture; a press that ended before its gate verdict landed is
        // dropped rather than guessed. A drag over a non-editable surface never had selection
        // semantics anyway.
        if (!_settings().InvokeOnDrag || !resolved || !textTarget)
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

    /// <summary>
    /// How long a press must last before the list warm-up starts. Below the 600 ms hold so the
    /// list is ready before the bar appears, above a normal click so a click builds nothing.
    /// </summary>
    private const long PrepareLeadMs = 220;

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

        // Wait for the gate's verdict before advancing the machine: Tick consumes the hold state
        // and returns ShowBarFromHold exactly once, so ticking early would burn the gesture on a
        // press that then turns out not to be over a text field. The verdict normally lands in a
        // few milliseconds, well inside the hold duration.
        if (!_pressGateResolved)
        {
            return;
        }

        if (!_pressIsTextTarget)
        {
            // Not an editable surface: the machine was already reset when the verdict arrived.
            return;
        }

        // Start building the list once the press looks like a hold. Doing it here — while the
        // pointer is down and still — keeps the work off the moment the bar appears, which is
        // exactly when the user starts moving towards it. A plain click never reaches this
        // threshold, so no list is built for it.
        if (!_prepareKicked && _clock.ElapsedMilliseconds - _pressAtMs >= PrepareLeadMs)
        {
            _prepareKicked = true;
            _prefetch ??= PrefetchRowsAsync();
            _ = PrepareListAsync();
        }

        var action = _machine.Tick(point.X, point.Y, _clock.ElapsedMilliseconds);
        if (action != GestureAction.ShowBarFromHold)
        {
            return;
        }


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
        // A gesture that started in ClipBar's own search box targets this process; the usability
        // guard rejects our own windows, but that check only exists to stop pasting into ourselves
        // by accident. This path is deliberate, and the row click inserts text directly.
        if (_disposed || (!_targetIsOwnTextInput && !IsUsableTarget(_target)))
        {
            return;
        }

        // keepWarmUp: the list was built during the hold for this very gesture; closing it here
        // would throw that work away and build it again.
        CloseBar(keepWarmUp: true);

        var settings = _settings();
        if (!settings.NativeFloatingEnabled)
        {
            return;
        }

        if (!_targetIsOwnTextInput)
        {
            CompactPanelWindow.RememberTarget(_target);
        }

        // Take the bar built at startup if there is one: its handle, template and layout are
        // already done, so the press only pays for parking and showing it.
        var bar = _preparedBar ?? new FloatingActionBarWindow();
        _preparedBar = null;
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
        _lastAnchor = point;
        _prefetch ??= PrefetchRowsAsync();
        _pointerLeftAtMs = 0;

        // The panel's click-outside hook treats these popups as "outside". Hold the panel open
        // for the pair's lifetime; otherwise a gesture that started in its own search box would
        // hide the panel before the chosen row could be inserted.
        if (_targetIsOwnTextInput)
        {
            _panelHold?.Dispose();
            _panelHold = _suppressPanelHide?.Invoke();
        }

        bar.ShowAt(point);
        InstallOutsideClickHook();
        _autoHideTimer.Start();

        // The press-to-visible span is the number worth watching: the first gesture of a session
        // pays for both windows' XAML/template/JIT work and for the first query, and this line is
        // how much of that landed on the user's hold.
        AppLog.Write(
            $"Floating bar shown ({(fromHold ? "hold" : "drag")}) at +{_clock.ElapsedMilliseconds - _pressAtMs} ms after press");

        // Build the list window now if the hold was too short to have started it. The hover expand
        // is then a position + Show — that is what makes it feel instant instead of a window
        // construction on the hover path.
        if (!_prepareKicked)
        {
            _prepareKicked = true;
            _ = PrepareListAsync();
        }
        else if (_preparedList is { } prepared)
        {
            // Already prepared during the hold: render it once now, while the pointer is travelling
            // to the bar. Doing this during the hold delayed the button-up and killed the gesture
            // (measured), so it is deferred to here. The point matters: it puts the window on the
            // monitor that will show it, so WPF resolves that monitor's DPI.
            prepared.WarmRender(point);
        }
    }

    /// <summary>
    /// Warms the compact list off the hover path: resolves the prefetch, constructs the window and
    /// lets it build its first rows. Failures are logged and ignored — a hover without a prepared
    /// window still works, it just pays the construction cost.
    /// </summary>
    private async Task PrepareListAsync()
    {
        try
        {
            IReadOnlyList<ClipItem>? rows = null;
            if (_prefetch is { } prefetch)
            {
                rows = await prefetch.ConfigureAwait(true);
            }

            // No bar requirement: the warm-up starts during the hold, before the bar exists.
            // Only a list that is already on screen makes the work pointless.
            if (_disposed || _list is not null)
            {
                return;
            }

            var list = CreateListWindow();
            _preparedList = list;
            await list.PrepareAsync(_target, rows).ConfigureAwait(true);

            // The bar may already be on screen by now; warm the render so the hover is instant.
            // Uses the bar's anchor, i.e. the point the list will appear near.
            if (_bar is not null && _lastAnchor is { } anchor)
            {
                list.WarmRender(anchor);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Prepare floating list", ex);
        }
    }

    private CompactPanelWindow CreateListWindow()
    {
        var list = new CompactPanelWindow(_repository, _pasteService, _settings)
        {
            // Owned from creation: the coordinator decides when it closes, including the window
            // that is only prepared and never shown.
            ManagedByCoordinator = true,
        };
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

            if (ReferenceEquals(_preparedList, list))
            {
                _preparedList = null;
            }
        };
        return list;
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

        // Reuse the window prepared when the bar appeared; a hover that beat the preparation
        // falls back to a fresh window, which is the old (slower) path but still correct.
        var list = _preparedList ?? CreateListWindow();
        _preparedList = null;
        _list = list;
        bar.IsExpanded = true;
        _ = ShowListAsync(list, bar);
    }

    /// <summary>Warms the row list while the pointer travels to the bar.</summary>
    private async Task<IReadOnlyList<ClipItem>> PrefetchRowsAsync()
    {
        var limit = Math.Clamp(_settings().MaxItems, 10, 5000);
        return await _repository
            .QueryAsync(ClipFilter.All, search: null, limit: limit)
            .ConfigureAwait(true);
    }

    private async Task ShowListAsync(CompactPanelWindow list, FloatingActionBarWindow bar)
    {
        try
        {
            var ownTarget = _targetIsOwnTextInput ? _insertIntoOwnTextInput : null;
            var started = _clock.ElapsedMilliseconds;
            await list
                .ShowBelowAsync(bar, _target, preloaded: null, ownTarget)
                .ConfigureAwait(true);
            var elapsed = _clock.ElapsedMilliseconds - started;
            AppLog.Write($"Floating bar: compact list shown in {elapsed} ms (visible={list.IsVisible}, {list.ActualWidth:0}x{list.ActualHeight:0})");
        }
        catch (Exception ex)
        {
            AppLog.Error("Show floating list", ex);
            CloseList();
        }
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
        }

        // Hover-to-expand is polled rather than driven by WPF MouseEnter: these windows can be
        // shown under a stationary pointer (no enter event) and a non-activating window does not
        // reliably route mouse messages. Polling the cursor is deterministic.
        //
        // No dwell: the pointer is already on the button by the time this runs, and any delay
        // reads as lag. The 16 ms tick is the only latency left.
        // Fallback for a pointer that does not move (raw input sends nothing then): the same check
        // the PointerMoved handler does. OnClipboardRequested is a no-op once the list is open.
        if (_bar is { IsExpanded: false } bar && bar.IsPointerOverButton)
        {
            OnClipboardRequested(bar);
        }

        if (overBar || overList)
        {
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
        // The desktop (Progman, or a WorkerW when a wallpaper engine runs) spans every monitor, so
        // the size test below reads it as a full-screen app and the gesture never fired anywhere on
        // the desktop — the rename case included. The desktop is the opposite of full-screen media.
        var className = NativeMethods.ClassNameOf(hwnd);
        if (className is "Progman" or "WorkerW")
        {
            return false;
        }

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

    private void CloseList(bool keepPrepared = false)
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

        // The prepared-but-never-shown window would otherwise leak with every bar.
        if (!keepPrepared && _preparedList is { } prepared)
        {
            _preparedList = null;
            try
            {
                prepared.Close();
            }
            catch (Exception ex)
            {
                AppLog.Error("Close prepared floating list", ex);
            }
        }
    }

    private void CloseBar(bool keepWarmUp = false)
    {
        if (!keepWarmUp)
        {
            _prepareKicked = false;
            _prefetch = null;
        }

        CloseList(keepPrepared: keepWarmUp);
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

        _panelHold?.Dispose();
        _panelHold = null;
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
            _rawInput.PointerMoved -= OnPointerMoved;
            _rawInput.Dispose();
            _rawInput = null;
        }

        CloseBar();

        // The startup bar that no gesture ever claimed.
        if (_preparedBar is { } prepared)
        {
            _preparedBar = null;
            try
            {
                prepared.Close();
            }
            catch (Exception ex)
            {
                AppLog.Error("Close prepared floating bar", ex);
            }
        }
    }
}
