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
    private bool _hasActivatedSinceShow;
    private int _suppressAutoHideDepth;
    private bool _userMoved;

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
            if (!IsVisible)
            {
                SaveGeometry();
            }
        };
        ImagePreviewMode = initial.ImagePreview;
        ViewModel.SetImagePreviewMode(ImagePreviewMode);
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
            _hasActivatedSinceShow = false;
            Show();
            RestorePlacement();
        }

        // Guarded: this runs on every panel show, so a failed query must be reported rather than
        // silently leaving the stale list on screen.
        ViewModel.RefreshInBackground();
        Topmost = true;
        ForceForeground();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>
    /// Restores the saved geometry (A5) when there is one, otherwise centres on the cursor's
    /// monitor. A saved position is clamped to the target monitor's work area, so an unplugged
    /// monitor or a resolution change never strands the panel off-screen.
    /// </summary>
    private void RestorePlacement()
    {
        if (_userMoved)
        {
            return;
        }

        var saved = _settings();
        // Position mode is independent of size memory: "Remember" restores the saved spot,
        // anything else centres on the monitor under the pointer every time.
        if (!string.Equals(saved.PanelPositionMode, "Remember", StringComparison.OrdinalIgnoreCase)
            || !saved.HasPanelPosition)
        {
            PlaceOnCursorMonitor();
            return;
        }

        // MonitorFromPoint expects PHYSICAL screen pixels. v1.1.1+ stores physical values;
        // older files stored DIPs, so convert with the window's current scale before choosing a
        // monitor (F-03). Mixing the two units picked the wrong monitor near screen edges on
        // mixed-DPI setups.
        var scaleNow = DpiScale > 0 ? DpiScale : 1.0;
        var point = saved.PanelPositionInPixels
            ? new NativeMethods.Point(saved.PanelLeft, saved.PanelTop)
            : new NativeMethods.Point(
                (int)Math.Round(saved.PanelLeft * scaleNow),
                (int)Math.Round(saved.PanelTop * scaleNow));
        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };

        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            PlaceOnCursorMonitor();
            return;
        }

        var work = info.Work;
        var targetScale = NativeMethods.MonitorScale(monitor);
        var (left, top) = PanelPlacement.ClampIn(
            point.X / targetScale,
            point.Y / targetScale,
            ActualWidth > 0 ? ActualWidth : Width,
            ActualHeight > 0 ? ActualHeight : Height,
            work.Left,
            work.Top,
            work.Right - work.Left,
            work.Bottom - work.Top,
            targetScale);

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

    /// <summary>Centres the panel on the monitor under the cursor, clamped to that monitor's work area.</summary>
    private void PlaceOnCursorMonitor()
    {
        // Once the user drags the panel, keep their choice for the rest of the session.
        if (_userMoved || !NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };

        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        // Monitor rects are physical pixels; WPF positions windows in DIPs of the window's own
        // DPI. The conversion lives in PanelPlacement so the mixed-DPI arithmetic is testable.
        var work = info.Work;
        var (left, top) = PanelPlacement.CenterIn(
            work.Left,
            work.Top,
            work.Right - work.Left,
            work.Bottom - work.Top,
            ActualWidth > 0 ? ActualWidth : Width,
            ActualHeight > 0 ? ActualHeight : Height,
            DpiScale,
            NativeMethods.MonitorScale(monitor));

        Left = left;
        Top = top;
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
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._suppressAutoHideDepth);
            }
        }
    }

    /// <summary>
    /// Panel-owned modal dialogs deactivate the panel while they are up; without suppression the
    /// panel hid itself behind the dialog and stayed hidden after the user cancelled (D-C-01).
    /// Focus returns to the panel once the dialog closes.
    /// </summary>
    private bool ConfirmOwned(string message, string primary, string secondary)
    {
        using var scope = SuppressAutoHide();
        try
        {
            return FluentMessageWindow.Confirm(this, message, primary, secondary);
        }
        finally
        {
            ForceForeground();
        }
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
                scope.Dispose();
                ForceForeground();
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

    private void Window_Activated(object? sender, EventArgs e)
    {
        _hasActivatedSinceShow = true;
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_allowClose && !AutoHideSuppressed && _hasActivatedSinceShow)
        {
            Hide();
        }
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

        var container = ItemsControl.ContainerFromElement(HistoryList, original);
        if (container is not ListBoxItem item || item.DataContext is not ClipItemViewModel clip)
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
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(HistoryList, source) is not ListBoxItem container
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
        }
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

        // A ShowAt failure (e.g. the window cannot open) must not leak the suppression: the
        // panel would never auto-hide again this session (G-21).
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

            throw;
        }

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
    /// Keeps the panel from auto-hiding while an owned menu is open, and hides it afterwards only
    /// when a foreign window really took the foreground (G-06). Shared by the clip menu and the
    /// source-filter menu.
    /// </summary>
    private IDisposable AttachMenuLifetime(TrayMenuWindow menu)
    {
        var scope = SuppressAutoHide();
        menu.Closed += (_, _) =>
        {
            scope.Dispose();

            Dispatcher.InvokeAsync(async () =>
            {
                // 120 ms covers the normal Esc/click hand-back to the owner. The decision is then
                // process-based, not hwnd-based: if any window of OURS owns the foreground (panel,
                // a dialog, the next menu) the panel must stay. Hiding whenever the foreground was
                // not the panel itself made a second click after right-click make the panel vanish.
                await Task.Delay(120).ConfigureAwait(true);
                if (!IsVisible || _allowClose || AutoHideSuppressed || IsActive)
                {
                    return;
                }

                // A click inside the panel dismisses the menu but must NOT take the panel with it.
                // Windows can deliver that click to the popup only, leaving a foreign window in the
                // foreground — so decide by where the pointer is, not only by the foreground hwnd.
                if (CursorIsOverPanel())
                {
                    ForceForeground();
                    return;
                }

                var foreground = NativeMethods.GetForegroundWindow();
                if (foreground == IntPtr.Zero)
                {
                    return;
                }

                if (PidOf(foreground) == SelfProcessId)
                {
                    return;
                }

                Hide();
            });
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

        var anchor = (FrameworkElement)sender;
        var bottomLeft = anchor.PointToScreen(new Point(0, anchor.ActualHeight));
        try
        {
            menu.ShowAt((int)bottomLeft.X, (int)bottomLeft.Y, aboveCursor: false);
        }
        catch
        {
            lifetime.Dispose();
            throw;
        }
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
    private static UIElement MenuGlyph(SymbolRegular symbol) => new SymbolIcon
    {
        Symbol = symbol,
        FontSize = 14,
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

            // Only a completed drag counts as a user placement. Setting this before DragMove
            // made an aborted drag (the InvalidOperationException below) disable re-centring
            // for the rest of the session, so the panel kept reappearing wherever it happened
            // to be.
            _userMoved = true;
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

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
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

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmOwned(
                "清空所有未固定的剪贴板历史？固定项会保留。",
                primary: "清空",
                secondary: "取消"))
        {
            ViewModel.ClearCommand.Execute(null);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
