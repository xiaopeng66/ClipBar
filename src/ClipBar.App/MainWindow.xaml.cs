using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipBar.App.Platform;
using ClipBar.App.Services;
using ClipBar.App.ViewModels;
using ClipBar.Core.Clipboard;
using ClipBar.Core.Models;
using Wpf.Ui.Controls;

namespace ClipBar.App;

public partial class MainWindow : FluentWindow
{
    /// <summary>
    /// How image clips are previewed: "Large" fills the card's content width, "Medium" caps the
    /// preview to a compact box, "Small" draws it in the 40 DIP icon slot. A dependency property
    /// rather than a plain field so the per-item bindings re-run when 设置 applies a new value.
    /// </summary>
    public static readonly DependencyProperty ImagePreviewModeProperty =
        DependencyProperty.Register(
            nameof(ImagePreviewMode),
            typeof(string),
            typeof(MainWindow),
            new PropertyMetadata("Large"));

    /// <summary>Device pixels per DIP on this window's monitor; thumbnails size against it.</summary>
    public static readonly DependencyProperty DpiScaleProperty =
        DependencyProperty.Register(
            nameof(DpiScale),
            typeof(double),
            typeof(MainWindow),
            new PropertyMetadata(1.0));

    private readonly PasteService _pasteService;
    private readonly Func<AppSettings> _settings;
    private readonly Action<double, double, double, double>? _noteGeometry;
    private readonly ExtensionActionRunner _runner;
    private IntPtr _previousForeground;
    private bool _allowClose;
    private bool _isClosed;
    /// <summary>
    /// True once ShowPanel has finished its show/activate sequence. Deactivated events that
    /// arrive earlier are part of that sequence and must not hide the panel; after this point
    /// any deactivation is a real focus loss. Activated alone is not a reliable signal: when the
    /// window is re-shown while WPF still considers it active, Activated is never raised, and the
    /// old flag stayed false — which silently disabled click-outside dismissal.
    /// </summary>
    private bool _showCompleted;
    private int _suppressAutoHideDepth;
    /// <summary>
    /// The panel lost activation while a suppression scope (menu, dialog, keep-open paste) was
    /// holding it open. When the last scope ends the panel must still be active, otherwise the
    /// user has moved on and it hides — without this, an outside click that closes a menu left
    /// the panel behind.
    /// </summary>
    private bool _deactivatedWhileSuppressed;
    private bool _dismissCheckPending;

    // Safety net for dismissal: Deactivated is the fast path, but Windows/WPF can miss the
    // activation change after the keep-open paste hands focus back (measured: the panel was
    // foreground, the user clicked another app, no Deactivated arrived). While the panel is
    // visible, require our process to keep the foreground; two consecutive foreign samples
    // at 100 ms hide it (~200 ms), which still rides out transient steals. Suppression
    // scopes (menu, dialog, paste) suspend the check.
    private readonly DispatcherTimer _focusWatchdog = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(100),
    };

    private int _foreignFocusTicks;

    public MainWindow(
        IClipRepository repository,
        Func<AppSettings> settings,
        PasteService pasteService,
        ExtensionService extensions,
        Action settingsRequested,
        Action<double, double, double, double>? noteGeometry = null)
    {
        InitializeComponent();
        _settings = settings;
        _pasteService = pasteService;
        _runner = new ExtensionActionRunner(() => _previousForeground);
        ViewModel = new MainViewModel(
            repository,
            settings,
            PasteItem,
            settingsRequested,
            extensions,
            _runner,
            PromptForAppOptions,
            ReportExtensionError,
            TargetProcessName,
            ReportRefreshFailure);
        DataContext = ViewModel;
        var initial = settings();
        Width = initial.PanelWidth;
        Height = initial.PanelHeight;
        _noteGeometry = noteGeometry;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _foreignFocusTicks = 0;
                _focusWatchdog.Start();
                return;
            }

            _focusWatchdog.Stop();
            // Defer the geometry write off the hide path: a settings flush (fsync) here used to
            // stretch the click-away dismissal by a visible beat. Idle priority runs a moment
            // later, off the critical path.
            Dispatcher.BeginInvoke(new Action(SaveGeometry), DispatcherPriority.ApplicationIdle);
        };

        _focusWatchdog.Tick += (_, _) => CheckFocusWhileVisible();
        ImagePreviewMode = initial.ImagePreview;
        ViewModel.SetImagePreviewMode(ImagePreviewMode);
        // Warm the first frame: the panel must open with its history already bound, not as an
        // empty shell that fills in a beat later. The single-flight task created here is shared
        // by every later ShowPanel call (hotkey, tray, --show), so nothing re-queries.
        _ = ViewModel.EnsureInitialLoadedAsync();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += (_, _) => _isClosed = true;
    }

    public MainViewModel ViewModel { get; }

    /// <summary>True once the window has closed; a queued show must not touch it after that.</summary>
    public bool IsClosed => _isClosed;

    public double DpiScale
    {
        get => (double)GetValue(DpiScaleProperty);
        set => SetValue(DpiScaleProperty, value);
    }

    public string ImagePreviewMode
    {
        get => (string)GetValue(ImagePreviewModeProperty);
        set => SetValue(ImagePreviewModeProperty, value);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        DpiScale = newDpi.DpiScaleX > 0 ? newDpi.DpiScaleX : 1.0;
    }

    public void ShowPanel(string reason = "unknown")
    {
        // Fire-and-forget: the initial load is a single-flight Task (created in the constructor),
        // so the second and every later call await the same completed task and stay on the fast
        // path. The cursor is captured NOW, before any await: preloading must not make the panel
        // open where the pointer has since moved to.
        _ = ShowPanelCore(reason);
    }

    private async Task ShowPanelCore(string reason)
    {
        NativeMethods.GetCursorPos(out var cursor);
        await ViewModel.EnsureInitialLoadedAsync().ConfigureAwait(true);
        if (_isClosed)
        {
            return;
        }

        if (!IsVisible)
        {
            var foreground = NativeMethods.GetForegroundWindow();
            // Never accept our own window as the paste target: the tray path and a fast
            // Esc→show cycle both put a shell or our own hwnd in the foreground (G-01). When the
            // capture is refused, keep the previous session's target — it is the app the user was
            // typing in, and the unsafe-target guard in PasteAsync still blocks self-pasting.
            if (PasteTarget.WorthCapturing(foreground, SelfProcessId, PidOf))
            {
                _previousForeground = foreground;
            }

            AppLog.Write(
                $"Panel open ({reason}): target={NativeMethods.GetProcessName(_previousForeground) ?? "unknown"}, "
                + $"hwnd=0x{_previousForeground.ToInt64():X}, foregroundNow={NativeMethods.GetProcessName(foreground) ?? "unknown"}");
            _showCompleted = false;
            _deactivatedWhileSuppressed = false;
            // Place before the window is shown: showing first and moving afterwards can paint
            // one frame at the previous monitor/position, which reads as the panel ignoring the
            // position setting.
            RestorePlacement(cursor.X, cursor.Y);
            Show();
        }

        // Guarded: this runs on every panel show, so a failed query must be reported rather than
        // silently leaving the stale list on screen.
        ViewModel.RefreshInBackground();
        Topmost = true;
        ForceForeground();
        _showCompleted = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>
    /// Restores the saved geometry (Remember), centres on the cursor's monitor
    /// (MonitorCenter) or opens next to the pointer (CursorAnchor). A saved position is clamped
    /// to the target monitor's work area, so an unplugged monitor or a resolution change never
    /// strands the panel off-screen.
    /// </summary>
    private void RestorePlacement(int cursorX, int cursorY)
    {
        var saved = _settings();
        var mode = AppSettings.CanonicalPanelPositionMode(saved.PanelPositionMode);
        if (mode == "Remember" && saved.HasPanelPosition)
        {
            // MonitorFromPoint expects PHYSICAL screen pixels. v1.1.1+ stores physical values;
            // older files stored DIPs, so convert with the window's current scale before choosing
            // a monitor (F-03). Mixing the two units picked the wrong monitor near screen edges
            // on mixed-DPI setups.
            var scaleNow = DpiScale > 0 ? DpiScale : 1.0;
            var point = saved.PanelPositionInPixels
                ? new NativeMethods.Point(saved.PanelLeft, saved.PanelTop)
                : new NativeMethods.Point(
                    (int)Math.Round(saved.PanelLeft * scaleNow),
                    (int)Math.Round(saved.PanelTop * scaleNow));
            var savedMonitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest);
            var savedInfo = new NativeMethods.MonitorInfo
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>(),
            };

            if (savedMonitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(savedMonitor, ref savedInfo))
            {
                var savedWork = savedInfo.Work;
                var savedScale = NativeMethods.MonitorScale(savedMonitor);
                var (restoredLeft, restoredTop) = PanelPlacement.ClampIn(
                    point.X / savedScale,
                    point.Y / savedScale,
                    ActualWidth > 0 ? ActualWidth : Width,
                    ActualHeight > 0 ? ActualHeight : Height,
                    savedWork.Left,
                    savedWork.Top,
                    savedWork.Right - savedWork.Left,
                    savedWork.Bottom - savedWork.Top,
                    savedScale);

                Left = restoredLeft;
                Top = restoredTop;
                return;
            }
        }

        // MonitorCenter and CursorAnchor both need the cursor's monitor; Remember without a
        // usable saved spot falls back here too.
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.Point(cursorX, cursorY),
            NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };

        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        // Monitor rects are physical pixels; WPF positions windows in DIPs of the target
        // monitor's DPI. The conversion lives in PanelPlacement so the mixed-DPI arithmetic is
        // testable.
        var work = info.Work;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var (left, top) = mode == "CursorAnchor"
            ? PanelPlacement.PlaceNearCursor(
                cursorX,
                cursorY,
                work.Left,
                work.Top,
                work.Right - work.Left,
                work.Bottom - work.Top,
                width,
                height,
                NativeMethods.MonitorScale(monitor))
            : PanelPlacement.CenterIn(
                work.Left,
                work.Top,
                work.Right - work.Left,
                work.Bottom - work.Top,
                width,
                height,
                NativeMethods.MonitorScale(monitor));

        Left = left;
        Top = top;
    }

    /// <summary>Persists the current size/position when the panel hides or closes (A5).</summary>
    private void SaveGeometry()
    {
        if (WindowState != WindowState.Normal || ActualWidth < 100 || ActualHeight < 100)
        {
            return;
        }

        // Position is persisted in physical pixels so restoring on a differently-scaled monitor
        // does not need to guess a scale; size stays in DIPs (it is the WPF Width/Height unit).
        var scale = DpiScale > 0 ? DpiScale : 1.0;
        _noteGeometry?.Invoke(Left * scale, Top * scale, ActualWidth, ActualHeight);
    }

    public void HidePanel() => Hide();

    /// <summary>Nested-safe suppression: a context menu and an options prompt can overlap.</summary>
    private bool AutoHideSuppressed => Volatile.Read(ref _suppressAutoHideDepth) > 0;

    /// <summary>
    /// Stops the panel from hiding itself on deactivation until the returned scope is disposed.
    /// A native ContextMenu is its own popup window, so opening one deactivates this window —
    /// without the guard the panel would vanish the moment the menu appeared.
    /// </summary>
    private IDisposable SuppressAutoHide()
    {
        Interlocked.Increment(ref _suppressAutoHideDepth);
        return new AutoHideScope(this);
    }

    private sealed class AutoHideScope(MainWindow owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (Interlocked.Decrement(ref owner._suppressAutoHideDepth) == 0
                && owner._deactivatedWhileSuppressed)
            {
                owner.ScheduleDismissAfterSuppression();
            }
        }
    }

    /// <summary>
    /// Runs one dispatcher turn after the last suppression ends. A click inside the panel
    /// activates it while the menu/dialog is still closing, so checking at Input priority sees
    /// the settled state instead of the transient "menu has focus, panel has not been activated
    /// yet" order. Still inactive means the user clicked elsewhere: dismiss the panel.
    /// </summary>
    private void ScheduleDismissAfterSuppression()
    {
        if (_dismissCheckPending)
        {
            return;
        }

        _dismissCheckPending = true;
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _dismissCheckPending = false;
                if (AutoHideSuppressed)
                {
                    // A new scope started between the queue and this callback (menu item →
                    // keep-open paste, option prompt): that scope's own disposal decides.
                    return;
                }

                var deactivated = _deactivatedWhileSuppressed;
                _deactivatedWhileSuppressed = false;
                // Judge by the foreground window: a closing owned menu makes WPF mark the owner
                // active even when the click landed on another app, so IsActive would lie here.
                // A zero foreground counts as foreign in this callback: it only runs after the
                // panel really lost activation, and "no window has the foreground" (desktop
                // click, transition) means the user is not interacting with the panel.
                var foreground = NativeMethods.GetForegroundWindow();
                var ours = foreground != IntPtr.Zero && PidOf(foreground) == SelfProcessId;
                if (PanelDismissPolicy.ShouldDismiss(deactivated, IsVisible, _allowClose, ours))
                {
                    Hide();
                }
            }),
            DispatcherPriority.Input);
    }

    /// <summary>Window inventory for the save-dialog flash reports (positions only, no content).</summary>
    private void LogVisibleWindows(string context)
    {
        try
        {
            var list = Application.Current.Windows
                .OfType<Window>()
                .Where(w => w.IsVisible)
                .Select(w => $"{w.GetType().Name}@{w.Left:0},{w.Top:0} topmost={w.Topmost}");
            AppLog.Write($"{context}: {string.Join(" | ", list)}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Log windows", ex);
        }
    }

    private void NotifyOwned(string message, SymbolRegular symbol, string heading)
    {
        using var scope = SuppressAutoHide();
        try
        {
            FluentMessageWindow.Notify(this, message, symbol, heading);
        }
        finally
        {
            ForceForeground();
        }
    }

    public void PrepareForExit()
    {
        _allowClose = true;
    }

    private async void PasteItem(ClipItem item) => await PasteItemCore(item, plainTextOnly: false);

    /// <summary>
    /// One paste flow. <paramref name="plainTextOnly"/> skips the HTML flavour (A1);
    /// with KeepPanelOpenAfterPaste the panel stays visible for consecutive pastes (A3) and is
    /// re-activated afterwards so the normal deactivate-to-hide rule keeps working.
    /// </summary>
    private async Task PasteItemCore(ClipItem item, bool plainTextOnly)
    {
        var target = _previousForeground;
        var keepOpen = _settings().KeepPanelOpenAfterPaste;
        AppLog.Write(
            $"Paste request: kind={item.Kind}, automatic={_settings().PasteOnClick}, plainText={plainTextOnly}, keepOpen={keepOpen}, "
            + $"target={NativeMethods.GetProcessName(target) ?? "unknown"}, hwnd=0x{target.ToInt64():X}");
        IDisposable? scope = null;
        if (keepOpen)
        {
            scope = SuppressAutoHide();
        }
        else
        {
            Hide();
        }

        // `async void` is the entry point WPF calls, so an exception here has nowhere to return
        // to: it reaches DispatcherUnhandledException, which logs and continues — but the panel is
        // already hidden by then, so the user sees nothing at all. F-42 was exactly that (a
        // damaged PNG throwing out of the decode); this guard is the backstop for whatever the
        // next one turns out to be.
        try
        {
            var selfHwnd = new WindowInteropHelper(this).Handle;
            // Tell the user what actually happened. Copying can fail outright (an image whose file was
            // trimmed away, an empty file list, a clipboard another process holds for good) and in that
            // case we must not send Ctrl+V — that would paste the *previous* clipboard contents.
            // The self/zero guard lives in PasteService *after* the copy, so refusing to send
            // keys never blocks the copy itself (G-01).
            var outcome = await _pasteService.PasteAsync(
                item, target, _settings().PasteOnClick, selfHwnd, SelfProcessId, plainTextOnly);
            var foreground = NativeMethods.GetForegroundWindow();
            AppLog.Write($"Paste result: {outcome}, foreground={NativeMethods.GetProcessName(foreground) ?? "unknown"}, hwnd=0x{foreground.ToInt64():X}");
            switch (outcome)
            {
                case PasteOutcome.CopyFailed:
                    FluentMessageWindow.Toast(
                        "无法写入剪贴板，可能是剪贴板被其它程序占用，或这条内容的源文件已被清理。请重试或重新复制一次。",
                        SymbolRegular.Warning24,
                        "复制失败");
                    break;
                case PasteOutcome.CopiedOnly when _settings().PasteOnClick:
                    // Content is on the clipboard; only the keystroke was withheld.
                    FluentMessageWindow.Toast(
                        "内容已复制到剪贴板，但无法切回原来的窗口，请手动按 Ctrl+V 粘贴。",
                        SymbolRegular.Info24,
                        "已复制");
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Paste item", ex);
            FluentMessageWindow.Toast(
                "粘贴时发生错误：" + ex.Message,
                SymbolRegular.Warning24,
                "粘贴失败");
        }
        finally
        {
            if (scope is not null)
            {
                // Keep-open: hand activation back to the panel so the next click lands on it and
                // the usual deactivate-to-hide rule still applies when the user moves on.
                // Activating immediately after the keystroke races the target app: measured with
                // a browser target, the browser took the foreground back after our Activate() and
                // the panel was left visible but inactive (so no Deactivated ever arrived and
                // click-outside did nothing). Let the target settle, then verify the hand-back.
                var panelHwnd = new WindowInteropHelper(this).Handle;
                await Task.Delay(150).ConfigureAwait(true);
                for (var attempt = 0; IsVisible && !_allowClose && attempt < 4; attempt++)
                {
                    ForceForeground();
                    await Task.Delay(80).ConfigureAwait(true);
                    if (NativeMethods.GetForegroundWindow() == panelHwnd)
                    {
                        break;
                    }
                }

                scope.Dispose();
            }
        }
    }

    /// <summary>右键图片条目：把历史里的 PNG 另存为文件（A4）。</summary>
    private void SaveImageAs(ClipItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ImagePath) || !System.IO.File.Exists(item.ImagePath))
        {
            FluentMessageWindow.Toast("这条图片的源文件已不在，无法保存。", SymbolRegular.Warning24, "保存失败");
            return;
        }

        var name = item.ImageWidth > 0 && item.ImageHeight > 0
            ? $"图片 {item.ImageWidth}×{item.ImageHeight}.png"
            : "图片.png";

        // Open the dialog only after the menu has fully closed and the panel is idle again:
        // doing it inside the menu click made the menu, the panel and the dialog flash over each
        // other at different screen positions.
        Dispatcher.BeginInvoke(
            new Action(() => ShowSaveImageDialog(item, name)),
            DispatcherPriority.ApplicationIdle);
    }

    private void ShowSaveImageDialog(ClipItem item, string fileName)
    {
        if (!IsVisible)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "图片另存为",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            FileName = fileName,
            AddExtension = true,
            OverwritePrompt = true,
        };

        // The save dialog deactivates the panel; without the suppression the panel hides behind
        // it and stays hidden after the dialog closes (same mechanism as the old D-C-01).
        using var scope = SuppressAutoHide();

        // A topmost panel over a shell dialog fights the dialog for the top of the z-order, which
        // shows up as windows flashing between monitors. Drop topmost for the dialog's lifetime.
        var wasTopmost = Topmost;
        Topmost = false;
        LogVisibleWindows("before save dialog");
        try
        {
            ForceForeground();
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            if (ImageStore.SaveAs(item.ImagePath!, dialog.FileName))
            {
                AppLog.Write("Image saved as " + dialog.FileName);
                FluentMessageWindow.Toast(
                    "图片已保存：" + System.IO.Path.GetFileName(dialog.FileName),
                    SymbolRegular.Checkmark24,
                    "已保存");
            }
            else
            {
                FluentMessageWindow.Toast(
                    "图片保存失败，目标位置可能不可写或同名文件被占用。",
                    SymbolRegular.Warning24,
                    "保存失败");
            }
        }
        finally
        {
            LogVisibleWindows("after save dialog");
            Topmost = wasTopmost;
            ForceForeground();
        }
    }

    /// <summary>Process that owned the clipboard when the panel opened, for app-scoped actions.</summary>
    private string? TargetProcessName()
    {
        var hwnd = _previousForeground != IntPtr.Zero ? _previousForeground : NativeMethods.GetForegroundWindow();
        return NativeMethods.GetProcessName(hwnd);
    }

    private static uint PidOf(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }

    /// <summary>This process id: used to refuse self-paste at capture and at send time (G-01).</summary>
    private static uint SelfProcessId => (uint)Environment.ProcessId;

    private IReadOnlyDictionary<string, string>? PromptForAppOptions(SnipDoAction action)
    {
        var window = new AppOptionWindow(action, action.Options)
        {
            Owner = this,
        };

        // Deactivation normally hides the panel; keep it visible while the prompt runs.
        using var scope = SuppressAutoHide();
        try
        {
            return window.ShowDialog() == true ? window.Result : null;
        }
        finally
        {
            ForceForeground();
        }
    }

    private void ReportExtensionError(string message)
    {
        AppLog.Write("Extension action: " + message);
        NotifyOwned(message, SymbolRegular.Warning24, "扩展动作失败");
    }

    /// <summary>
    /// A panel refresh failed. Logged and told to the user once per failure streak: the list on
    /// screen is then stale, and the usual cause (an unreadable or deleted history file) is
    /// something they can act on.
    /// </summary>
    private void ReportRefreshFailure(Exception exception)
    {
        AppLog.Error("Refresh history", exception);
        NotifyOwned(
            "读取剪贴板历史失败，列表可能不是最新的。如果问题持续，请重启 ClipBar。",
            SymbolRegular.Warning24,
            "读取历史失败");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GwlExStyle,
            new IntPtr(style | NativeMethods.WsExToolWindow));

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        DpiScale = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void Window_Activated(object? sender, EventArgs e) => _showCompleted = true;

    /// <summary>
    /// Hides the panel once it has been visible for two watchdog ticks without our process
    /// owning the foreground. Complements <see cref="Window_Deactivated"/> for the cases where
    /// WPF never delivers the deactivation (see the timer's construction comment).
    /// </summary>
    private void CheckFocusWhileVisible()
    {
        if (!IsVisible || _allowClose || AutoHideSuppressed)
        {
            _foreignFocusTicks = 0;
            return;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return;
        }

        if (PidOf(foreground) == SelfProcessId)
        {
            _foreignFocusTicks = 0;
            return;
        }

        // Three ticks at the old 200 ms cadence was ~600 ms — far too slow to read as "dismissed
        // on click-away". Two samples at 100 ms keep the transient-steal tolerance.
        _foreignFocusTicks++;
        if (_foreignFocusTicks >= 2)
        {
            _foreignFocusTicks = 0;
            AppLog.Write("Focus watchdog: panel hidden");
            Hide();
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_allowClose || !_showCompleted)
        {
            return;
        }

        if (AutoHideSuppressed)
        {
            // A menu/dialog/keep-open paste is holding the panel. Remember the deactivation so
            // the last scope can decide: still inactive at that point means click-outside.
            _deactivatedWhileSuppressed = true;
            return;
        }

        Hide();
    }

    private void ForceForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != currentThread
            && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);

        NativeMethods.BringWindowToTop(hwnd);
        NativeMethods.SetForegroundWindow(hwnd);

        if (attached)
        {
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }

        Activate();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.Enter:
                ViewModel.PasteSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete when ViewModel.SelectedItem is not null:
                ViewModel.DeleteCommand.Execute(ViewModel.SelectedItem);
                e.Handled = true;
                break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.P when Keyboard.Modifiers == ModifierKeys.Control && ViewModel.SelectedItem is not null:
                ViewModel.TogglePinCommand.Execute(ViewModel.SelectedItem);
                e.Handled = true;
                break;
            case Key.Apps when ViewModel.SelectedItem is not null:
                OpenClipMenuForSelectedItem();
                e.Handled = true;
                break;
            case Key.F10 when Keyboard.Modifiers == ModifierKeys.Shift && ViewModel.SelectedItem is not null:
                OpenClipMenuForSelectedItem();
                e.Handled = true;
                break;
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // The IME draws its composition glyphs over the box while TextBox.Text is still empty,
        // so waiting for TextChanged would leave the hint showing through the composing text.
        if (e.Key == Key.ImeProcessed)
        {
            SearchPlaceholder.Visibility = Visibility.Collapsed;
        }

        if (e.Key == Key.Down)
        {
            HistoryList.Focus();
            if (ViewModel.SelectedItem is null && HistoryList.Items.Count > 0)
            {
                HistoryList.SelectedIndex = 0;
            }

            e.Handled = true;
        }

        // Esc/Enter are handled by Window_PreviewKeyDown, which tunnels before this handler
        // ever runs — the old branches here were dead code (D-C-07).
    }

    /// <summary>
    /// Keeps the search hint in sync with what the box actually contains. This covers committed
    /// typing, pasting and programmatic clears; the IME composition case is handled in
    /// <see cref="SearchBox_PreviewKeyDown"/> because a composition does not touch
    /// <c>TextBox.Text</c> until the user commits it.
    /// </summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSearchPlaceholder();

    /// <summary>
    /// A cancelled composition leaves the text unchanged, and with it the hint hidden; restoring
    /// on focus loss keeps an empty box from looking blank.
    /// </summary>
    private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => UpdateSearchPlaceholder();

    private void UpdateSearchPlaceholder()
        => SearchPlaceholder.Visibility = SearchBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void HistoryList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<System.Windows.Controls.Button>(source) is not null)
        {
            // Pin/delete clicks are logged too: without this line a swallowed click looks like a
            // paste that never happened (the G-01 investigation needs the full click picture).
            AppLog.Write("History click: interactive control, no paste");
            return;
        }

        if (e.OriginalSource is not DependencyObject original)
        {
            return;
        }

        // Same Run-safe walk as the context menu: highlighted titles are ContentElements, which
        // ContainerFromElement cannot always bridge (a left click on the highlight used to die
        // the same way the right click did).
        if (FindAncestor<ListBoxItem>(original) is not ListBoxItem item || item.DataContext is not ClipItemViewModel clip)
        {
            AppLog.Write($"History click: source={original.GetType().Name}, container=none");
            return;
        }

        // The id makes a recycled-container mis-click diagnosable (G-12).
        AppLog.Write($"History click: clip={clip.Id}, kind={clip.Kind}");
        HistoryList.SelectedItem = item.DataContext;
        ViewModel.PasteSelectedCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// Right-click on the history: offer the extension actions for the row under the pointer.
    /// Left-click paste is untouched — this handler only ever opens a menu.
    /// </summary>
    private void HistoryList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        // Walk to the row through ElementTree: highlighted titles are Runs (ContentElements),
        // which ContainerFromElement cannot always bridge — right-clicking the highlighted text
        // used to silently produce no menu.
        if (FindAncestor<ListBoxItem>(source) is not ListBoxItem container
            || container.DataContext is not ClipItemViewModel item)
        {
            // Outside any row (group header, empty list): no menu. Keyboard invocation is handled
            // in Window_PreviewKeyDown for the selected row instead.
            return;
        }

        e.Handled = true;
        if (NativeMethods.GetCursorPos(out var cursor))
        {
            OpenClipContextMenu(item, cursor.X, cursor.Y);
            return;
        }

        // GetCursorPos can fail while the input queue is busy; fall back to the row's on-screen
        // position instead of swallowing the right-click without a trace.
        AppLog.Write("ContextMenu: GetCursorPos failed, falling back to the row rect");
        var atRow = container.PointToScreen(new Point(container.ActualWidth / 2, container.ActualHeight / 2));
        OpenClipContextMenu(item, (int)atRow.X, (int)atRow.Y);
    }

    /// <summary>
    /// Opens the extension menu for one specific clip, anchored at a physical-pixel screen point.
    /// The actions and the clip are captured now: by the time an entry is clicked the selection
    /// may have moved on, and the runner must act on the clip the user right-clicked. The menu is
    /// a TrayMenuWindow; activating it deactivates this panel, so auto-hide is suppressed for
    /// exactly the menu's lifetime — FluentWindow.Closed is a reliable release point, unlike the
    /// native ContextMenu's Closed (measured: never raised for programmatic closes).
    /// </summary>
    private TrayMenuWindow OpenClipContextMenu(ClipItemViewModel item, int anchorX, int anchorY)
    {
        var actions = ViewModel.GetApplicableActions(item);
        var model = item.Model;

        var entries = new List<TrayMenuEntry>();
        var builtIn = model.Kind switch
        {
            ClipKind.Text => TrayMenuEntry.Item(
                "粘贴为纯文本",
                // Menu rows are Actions: PasteItemCore logs its own failures, so the returned
                // task is deliberately fire-and-forget.
                () => { _ = PasteItemCore(model, plainTextOnly: true); },
                icon: MenuGlyph(SymbolRegular.TextDescription24)),
            ClipKind.Image => TrayMenuEntry.Item(
                "图片另存为…",
                () => SaveImageAs(model),
                icon: MenuGlyph(SymbolRegular.Save24)),
            _ => (TrayMenuEntry?)null,
        };

        if (builtIn is not null)
        {
            entries.Add(builtIn);
            if (actions.Count > 0)
            {
                entries.Add(TrayMenuEntry.Separator());
            }
        }

        foreach (var action in actions)
        {
            var entry = action;
            entries.Add(TrayMenuEntry.Item(
                entry.Title,
                () => ViewModel.RunExtensionCommand.Execute(new ExtensionActionRequest(item, entry)),
                icon: BuildMenuGlyph(entry)));
        }

        if (entries.Count == 0)
        {
            entries.Add(new TrayMenuEntry("暂无适用扩展动作", null));
        }

        var menu = TrayMenuWindow.Create(entries, width: 0);
        menu.Owner = this;
        HistoryList.SelectedItem = item;

        var lifetime = AttachMenuLifetime(menu);

        // Show on the next input frame: opening synchronously inside ContextMenuOpening races
        // the right-button-up, which can hand focus back to the owner and leave the menu visible
        // but inactive. A ShowAt failure here must still not leak the suppression (G-21).
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                try
                {
                    menu.ShowAt(anchorX, anchorY, aboveCursor: false);
                }
                catch
                {
                    lifetime.Dispose();
                    try
                    {
                        menu.Close();
                    }
                    catch (Exception closeFailure)
                    {
                        AppLog.Error("Close clip menu after failed show", closeFailure);
                    }
                }
            }),
            DispatcherPriority.Input);

        return menu;
    }

    /// <summary>Keyboard invocation (menu key / Shift+F10) anchors at the selected row's right edge.</summary>
    private void OpenClipMenuForSelectedItem()
    {
        var item = ViewModel.SelectedItem;
        if (item is null)
        {
            return;
        }

        NativeMethods.Point anchor;
        if (HistoryList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
        {
            var screen = container.PointToScreen(new Point(container.ActualWidth, 0));
            anchor = new NativeMethods.Point((int)screen.X, (int)screen.Y);
        }
        else if (!NativeMethods.GetCursorPos(out anchor))
        {
            return;
        }

        OpenClipContextMenu(item, anchor.X, anchor.Y);
    }

    /// <summary>
    /// Keeps the panel alive while an owned menu is open. When the menu closes, Esc or an item
    /// click hands activation back to the panel; a click outside leaves the panel inactive and
    /// the suppression scope's deferred check then dismisses it. Shared by the clip menu and the
    /// source-filter menu.
    /// </summary>
    private IDisposable AttachMenuLifetime(TrayMenuWindow menu)
    {
        var scope = SuppressAutoHide();
        menu.Closed += (_, _) =>
        {
            // Dispose first: the deferred dismiss runs at Input priority, after the activation
            // below has settled, so this is still one coherent decision.
            scope.Dispose();

            if (menu.ClosedByDeactivation)
            {
                // Measured: clicking the panel while the menu is open closes the menu without
                // leaving the panel in the foreground — Windows hands focus back to the app that
                // was active before the menu (a foreign window). The pointer position is the
                // reliable signal that the click landed inside the panel; take focus back so the
                // deferred check keeps it. A click elsewhere leaves both conditions false and the
                // deferred check dismisses the panel.
                if (CursorIsOverPanel() && IsVisible && !_allowClose)
                {
                    ForceForeground();
                }

                return;
            }

            // Esc or a menu item: return focus so the panel stays for the next action.
            if (IsVisible && !_allowClose)
            {
                ForceForeground();
            }
        };

        return scope;
    }

    /// <summary>True when the pointer is inside this window's screen rectangle.</summary>
    private bool CursorIsOverPanel()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)
            || !NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var rect))
        {
            return false;
        }

        return cursor.X >= rect.Left && cursor.X < rect.Right
            && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
    }

    /// <summary>Compact source filter: a funnel button with a menu instead of a wide dropdown.</summary>
    private void SourceFilter_Click(object sender, RoutedEventArgs e)
    {
        var entries = new List<TrayMenuEntry>
        {
            TrayMenuEntry.Item("全部来源", () => ViewModel.SelectedSource = "全部来源", bold: true),
        };
        foreach (var source in ViewModel.SourceOptions)
        {
            if (source == "全部来源")
            {
                continue;
            }

            var value = source;
            entries.Add(TrayMenuEntry.Item(value, () => ViewModel.SelectedSource = value));
        }

        var menu = TrayMenuWindow.Create(entries, width: 0);
        menu.Owner = this;
        var lifetime = AttachMenuLifetime(menu);

        // The footer button sits at the panel's bottom edge: anchor on its top-left corner and
        // open upwards, so the menu never fights the work-area bottom.
        var anchor = (FrameworkElement)sender;
        var topLeft = anchor.PointToScreen(new Point(0, 0));

        // Same next-input-frame display as the clip menu: the button's click must finish before
        // the menu activates, or the activation can be stolen back mid-click.
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                try
                {
                    menu.ShowAt((int)topLeft.X, (int)topLeft.Y, aboveCursor: true);
                }
                catch
                {
                    lifetime.Dispose();
                    try
                    {
                        menu.Close();
                    }
                    catch (Exception closeFailure)
                    {
                        AppLog.Error("Close source menu after failed show", closeFailure);
                    }
                }
            }),
            DispatcherPriority.Input);
    }

    /// <summary>A 16 DIP glyph: the package icon when it decoded, else a flash placeholder.</summary>
    private static UIElement BuildMenuGlyph(ExtensionActionViewModel entry)
        => entry.IconSource is { } source
            ? new System.Windows.Controls.Image
            {
                Source = source,
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 8, 0),
                SnapsToDevicePixels = true,
            }
            : MenuGlyph(SymbolRegular.Flash24);

    /// <summary>Built-in menu rows use the same 16 DIP symbol treatment as package icons.</summary>
    /// <remarks>
    /// The explicit size keeps the menu's pre-show measurement deterministic: a SymbolIcon's
    /// desired size before the window is loaded can fall back to zero, which would make the
    /// content-sized menu narrower than its rows.
    /// </remarks>
    private static UIElement MenuGlyph(SymbolRegular symbol) => new SymbolIcon
    {
        Symbol = symbol,
        FontSize = 14,
        Width = 16,
        Height = 16,
        Margin = new Thickness(0, 0, 8, 0),
        Foreground = ThemeBrushes.Resolve("TextFillColorSecondaryBrush", ThemeBrushes.SecondaryText),
    };

    /// <summary>
    /// Rounds the thumbnail's corners. A Border's CornerRadius cannot clip a child, so the
    /// clip geometry is rebuilt here whenever the image settles on a new size.
    /// </summary>
    private void Thumbnail_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Image image)
        {
            return;
        }

        var width = image.ActualWidth;
        var height = image.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            image.Clip = null;
            return;
        }

        var radius = Math.Min(8, Math.Min(width, height) / 2);
        image.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, width, height),
            radius,
            radius);
    }

    /// <summary>Drag the panel by any non-interactive area of its chrome.</summary>
    private void Panel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source && IsInteractive(source))
        {
            return;
        }

        try
        {
            DragMove();

            // A drag only moves the panel for this session; "follow the mouse" still centres on
            // the cursor's monitor the next time it opens, and "remember" persists the new spot
            // through SaveGeometry. The old session-wide _userMoved pin made one click on the
            // panel chrome silently disable follow mode until restart.
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>True when the click landed on something that must keep its own mouse handling.</summary>
    private static bool IsInteractive(DependencyObject source)
    {
        var current = source;
        while (current is not null && current is not Grid { Name: "RootPanel" })
        {
            switch (current)
            {
                case System.Windows.Controls.Primitives.ButtonBase:
                case System.Windows.Controls.Primitives.TextBoxBase:
                case System.Windows.Controls.Primitives.ScrollBar:
                case System.Windows.Controls.Primitives.Thumb:
                case ListBoxItem:
                case System.Windows.Controls.Primitives.Popup:
                // A group caption is chrome, not a drag handle: clicking it used to start
                // DragMove and swallow the click (D-C-04, measured 120,72 px drift).
                case GroupItem:
                    return true;
            }

            current = ElementTree.ParentOf(current);
        }

        return false;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenSettingsCommand.Execute(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = ElementTree.ParentOf(current);
        }

        return null;
    }
}
