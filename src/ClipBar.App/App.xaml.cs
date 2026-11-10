using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClipBar.App.Platform;
using ClipBar.App.Services;
using ClipBar.Core.Clipboard;
using ClipBar.Core.Models;
using ClipBar.Core.Settings;

namespace ClipBar.App;

public partial class App : Application
{
    private const string MutexName = @"Local\ClipBar.SingleInstance";
    private const string ShowEventName = @"Local\ClipBar.Show";

    /// <summary>
    /// Named event a script can signal to open the compact list, so an external launcher does
    /// not have to spawn a second process. Also reachable as <c>--compact</c>.
    /// </summary>
    private const string CompactEventName = @"Local\ClipBar.Compact";

    private SingleInstanceGuard? _singleInstance;
    private EventWaitHandle? _showEvent;
    private ShowSignalListener? _showListener;

    /// <summary>
    /// Separate signal for the compact list, so a launcher can show the small picker without
    /// the full panel appearing.
    /// </summary>
    private EventWaitHandle? _compactEvent;
    private ShowSignalListener? _compactListener;
    private CompactPanelWindow? _compactWindow;
    private JsonSettingsStore _settingsStore = null!;
    private AppSettings _settings = null!;
    private SqliteClipRepository _repository = null!;
    private ClipboardMonitor _monitor = null!;
    private HotkeyService _hotkey = null!;
    private PasteService _pasteService = null!;
    private TrayService _tray = null!;
    private StartupService _startup = null!;
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;

    /// <summary>
    /// The native floating clipboard bar (Raw Input gestures). Null while disabled, and also
    /// null when Raw Input is unavailable on this session.
    /// </summary>
    private FloatingBarCoordinator? _floatingBar;

    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, e) =>
        {
            AppLog.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Keep the tray-resident process responsive after long idle periods.
        NativeMethods.DisablePowerThrottling();

        // Data location must be settled before anything reads AppPaths (log, settings, DB):
        // --data-dir, then a portable.marker next to the exe, then %LOCALAPPDATA%\ClipBar.
        AppPaths.Initialize(ReadOption(e.Args, "--data-dir"), AppContext.BaseDirectory);
        if (!AppPaths.IsDefaultLocation)
        {
            AppLog.Write("Data directory: " + AppPaths.DataDirectory);
        }

        // A second launch only wakes the running instance; --show / --compact are what make
        // the intent explicit at the call site.
        var showOnStart = e.Args.Any(arg => string.Equals(arg, "--show", StringComparison.OrdinalIgnoreCase));
        var compactOnStart = e.Args.Any(arg => string.Equals(arg, "--compact", StringComparison.OrdinalIgnoreCase));
        // Diagnostic-only: keep the panel visible despite outside clicks/deactivation so an
        // isolated UI automation run can observe it. Never persisted and never enabled by default.
        var holdOpenOnStart = e.Args.Any(arg => string.Equals(arg, "--hold-open", StringComparison.OrdinalIgnoreCase));

        _singleInstance = SingleInstanceGuard.TryAcquire(
            MutexName + AppPaths.InstanceSuffix,
            ShowEventName + AppPaths.InstanceSuffix,
            CompactEventName + AppPaths.InstanceSuffix,
            compactOnStart);
        if (_singleInstance is null)
        {
            Shutdown();
            return;
        }

        // Create the show event and start listening before any window exists. A second launch
        // during the (async) repository init used to be lost entirely, so the user pressed the
        // hotkey, saw nothing happen, and pressed it again.
        StartShowListener();

        try
        {
            AppPaths.EnsureCreated();
            _settingsStore = new JsonSettingsStore(AppPaths.SettingsPath);
            // Normalise on load, not just on save: an older settings.json carries values the
            // current build no longer emits (e.g. ImagePreview "Thumbnail"), and the panel would
            // otherwise receive the raw string.
            _settings = _settingsStore.Load().Normalize();
            ThemeService.Apply(_settings.Theme);

            _repository = new SqliteClipRepository(AppPaths.DatabasePath);
            await _repository.InitializeAsync();

            // A crash between writing a PNG and inserting its row leaves an unreferenced file
            // that nothing else collects; clear those out now that the history is known.
            try
            {
                var referenced = await _repository.GetImagePathsAsync();
                var swept = ImageStore.SweepOrphans(AppPaths.ImagesDirectory, referenced);
                // Undo-deleted images wait in images/trash for a short window (A6); clear the ones
                // nobody restored, e.g. after a crash during that window.
                ImageStore.SweepTrash(AppPaths.ImagesDirectory, TimeSpan.FromHours(1));
                if (swept > 0)
                {
                    AppLog.Write($"已清理 {swept} 个无引用的图片文件");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Sweep orphan images", ex);
            }

            _monitor = new ClipboardMonitor(_repository, () => _settings, OnClipCaptured);
            _pasteService = new PasteService(_monitor);
            _startup = new StartupService();

            _mainWindow = new MainWindow(
                _repository,
                () => _settings,
                _pasteService,
                ShowSettings,
                NotePanelGeometry,
                holdOpenOnStart);

            _tray = new TrayService(
                () => ShowPanel("tray"),
                ShowSettings,
                ClearAllHistory,
                ExitApp,
                () => _monitor.IsPaused,
                ToggleCapturePaused);

            StartFloatingBar();

            // G-02: a show signal can arrive while the repository init is still awaiting, before
            // the window exists; it used to vanish silently. Consume it now instead.
            if (_pendingShow)
            {
                _pendingShow = false;
                ShowPanel("deferred-signal");
            }

            if (_pendingCompact)
            {
                _pendingCompact = false;
                ShowCompact("deferred-signal");
            }

            // Surface a settings file we could not read; otherwise the user silently loses
            // every preference on the next save and never learns why.
            if (_settingsStore.LastLoadWarning is { } warning)
            {
                AppLog.Write(warning);
                _tray.ShowBalloon("设置未能加载", warning);
            }

            // A --data-dir / portable.marker directory we could not use fell back to the default;
            // tell the user instead of silently writing somewhere else.
            if (AppPaths.InitializationWarning is { } dataWarning)
            {
                AppLog.Write(dataWarning);
                _tray.ShowBalloon("数据目录不可用", dataWarning);
            }

            _hotkey = new HotkeyService();
            _hotkey.Pressed += () => ShowPanel("hotkey");
            RegisterConfiguredHotkey();


            // Reconcile the stored flag with the registry (a cleanup tool can remove the Run
            // entry), but only write when it actually differs: this used to rewrite
            // settings.json on every launch, so an external edit or a read-only profile made
            // startup either clobber or throw.
            // The rename orphaned the old Run value; if the user wanted autostart, rewrite it
            // under the new name with the current exe path instead of silently turning it off.
            if (_settings.StartWithWindows && !_startup.IsEnabled)
            {
                _startup.SetEnabled(true, StartupArguments);
            }

            var actualStartup = _startup.IsEnabled;
            if (_settings.StartWithWindows != actualStartup)
            {
                _settings.StartWithWindows = actualStartup;
                TrySaveSettings();
            }

            AppLog.Write("ClipBar started");

            if (compactOnStart)
            {
                _ = Dispatcher.BeginInvoke(
                    new Action(() => ShowCompact("startup")),
                    DispatcherPriority.ApplicationIdle);
            }

            if (showOnStart)
            {
                // The panel must be visible before the first foreground hand-off, so wait for
                // the window to finish layout instead of calling ShowPanel synchronously.
                _ = Dispatcher.BeginInvoke(new Action(() => ShowPanel("startup")), DispatcherPriority.ApplicationIdle);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup", ex);
            FluentMessageWindow.Notify(
                null,
                "ClipBar 启动失败：\n" + ex.Message,
                Wpf.Ui.Controls.SymbolRegular.ErrorCircle24,
                "启动失败");
            Shutdown();
        }
    }

    /// <summary>Persists settings, logging (not crashing) when the file cannot be written.</summary>
    private bool TrySaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Save settings", ex);
            _tray?.ShowBalloon("设置未能保存", "无法写入设置文件：" + ex.Message);
            return false;
        }
    }

    private void RegisterConfiguredHotkey()
    {
        if (_hotkey.Register(_settings.Hotkey, out var error))
        {
            return;
        }

        AppLog.Write("Hotkey register failed: " + error);
        const string fallback = "Ctrl+Alt+V";
        if (!string.Equals(_settings.Hotkey, fallback, StringComparison.OrdinalIgnoreCase)
            && _hotkey.Register(fallback, out _))
        {
            _settings.Hotkey = fallback;
            TrySaveSettings();
            _tray?.ShowBalloon("快捷键已被占用", $"已改用 {fallback}");
            return;
        }

        _tray?.ShowBalloon("快捷键注册失败", error + " 可以从托盘菜单打开面板。");
    }

    private void StartShowListener()
    {
        _showEvent = _singleInstance?.CreateShowEvent();
        _compactEvent = _singleInstance?.CreateCompactEvent();

        // The listener owns the exit flag: an AutoReset handle never makes WaitOne return false,
        // so the old loop could only leave by hitting the ObjectDisposedException after shutdown
        // — meanwhile ExitApp's Set() woke it and queued a show on a closed window.
        if (_showEvent is not null)
        {
            _showListener = new ShowSignalListener(
                _showEvent,
                () => Dispatcher.BeginInvoke(new Action(() => ShowPanel("signal"))),
                AppLog.Error);
        }

        if (_compactEvent is not null)
        {
            _compactListener = new ShowSignalListener(
                _compactEvent,
                () => Dispatcher.BeginInvoke(new Action(() => ShowCompact("signal"))),
                AppLog.Error);
        }
    }

    /// <summary>
    /// Shows the compact clipboard list: recent rows only, click to paste. The full panel stays
    /// closed. Reached from <c>--compact</c> and the named event; the floating bar's
    /// hover-expand builds the same window directly in the coordinator.
    /// </summary>
    private void ShowCompact(string reason)
    {
        if (_repository is null || _pasteService is null)
        {
            _pendingCompact = true;
            AppLog.Write($"Show compact ({reason}) deferred: services not ready");
            return;
        }

        if (_compactWindow is { IsVisible: true } open)
        {
            open.Activate();
            return;
        }

        try
        {
            _settingsWindow?.Hide();
            _mainWindow?.Hide();
            var window = new CompactPanelWindow(_repository, _pasteService, () => _settings);
            window.Closed += (_, _) => CompactWindowLifetime.ClearIfCurrent(ref _compactWindow, window);
            _compactWindow = window;
            _ = window.ShowAtCursorAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Show compact", ex);
        }
    }

    private bool _pendingShow;
    private bool _pendingCompact;

    /// <summary>Open-path tagged logging + deferred shows when the window is not ready yet (G-01/G-02).</summary>
    private void ShowPanel(string reason)
    {
        if (_mainWindow is not { } window || window.IsClosed)
        {
            _pendingShow = true;
            AppLog.Write($"Show panel ({reason}) deferred: window not ready");
            return;
        }

        if (_settingsWindow is { IsVisible: true } settings && settings.HasUnappliedEdits)
        {
            // Hiding parks the form with its edits; without a note the user believes the window
            // is still open and the edits are lost on the next open (G-11).
            AppLog.Write("Panel shown while the settings window has unapplied edits; window hidden");
        }
        _settingsWindow?.Hide();
        window.ShowPanel(reason);
    }

    /// <summary>Starts the native floating bar once the panel window (and its HWND) exists.</summary>
    private void StartFloatingBar()
    {
        if (!_settings.NativeFloatingEnabled || _floatingBar is not null || _mainWindow is not { } panel)
        {
            return;
        }

        try
        {
            // The panel is created lazily and its HWND only exists once WPF has initialized it.
            // Raw Input has to be registered against that HWND, so force it now instead of
            // silently staying off until the first manual open.
            var helper = new System.Windows.Interop.WindowInteropHelper(panel);
            if (helper.Handle == IntPtr.Zero)
            {
                helper.EnsureHandle();
            }

            var coordinator = new FloatingBarCoordinator(
                _repository,
                _pasteService,
                () => _settings,
                () => helper.Handle,
                panel.IsPointerOverSearchBox,
                panel.InsertIntoSearchBox,
                panel.SuppressAutoHideForFloating);
            if (coordinator.Start())
            {
                _floatingBar = coordinator;
            }
            else
            {
                coordinator.Dispose();
                AppLog.Write("Native floating bar unavailable; staying off");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Start floating bar", ex);
        }
    }

    private void StopFloatingBar()
    {
        _floatingBar?.Dispose();
        _floatingBar = null;
    }

    /// <summary>Persists the panel's size/position (A5) — called from MainWindow on hide.</summary>
    private void NotePanelGeometry(double left, double top, double width, double height)
    {
        var next = _settings.Clone();
        next.HasPanelPosition = true;
        next.PanelPositionInPixels = true;
        next.PanelLeft = (int)Math.Round(left);
        next.PanelTop = (int)Math.Round(top);
        next.PanelWidth = (int)Math.Round(width);
        next.PanelHeight = (int)Math.Round(height);
        next.Normalize();

        if (next.ContentEquals(_settings))
        {
            return;
        }

        _settings = next;
        TrySaveSettings();
    }

    /// <summary>Value of <paramref name="name"/> when followed by one argument.</summary>
    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>Tray menu 暂停捕获/恢复捕获 (A2): session-scoped, not persisted.</summary>
    private void ToggleCapturePaused()
    {
        _monitor.IsPaused = !_monitor.IsPaused;
        _tray.UpdateCaptureState(_monitor.IsPaused);
        _tray.ShowBalloon(
            _monitor.IsPaused ? "已暂停捕获" : "已恢复捕获",
            _monitor.IsPaused ? "期间复制的内容不会被记录。" : "新的复制内容会继续记录。");
        AppLog.Write(_monitor.IsPaused ? "Capture paused from tray" : "Capture resumed from tray");
    }

    private void OnClipCaptured(ClipItem item)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // Refresh even while the panel is hidden: the cache must be warm so the next open
            // shows the captured clip immediately instead of catching up on screen.
            if (_mainWindow is { IsClosed: false } panel)
            {
                // Guarded for the same reason as ShowPanel: a capture-triggered refresh that
                // fails must be reported, not silently skipped.
                panel.ViewModel.RefreshInBackground();
            }
        }));
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        if (_settingsWindow is { } parked)
        {
            // A hidden window is parked, not dead: it may hold unapplied edits (opening the
            // panel hides it without closing). Re-show that instance so the user finds their
            // edits. Closing a dirty parked window here used to pop the confirm dialog while
            // the caller went on creating a replacement — an orphaned editor plus a duplicate
            // window. BuildCandidate reads the live settings, so the parked form can neither
            // fake a dirty state nor overwrite geometry that changed while it was hidden.
            _mainWindow?.CenterOverPanel(parked);
            // Reveal the settings window first, then hide the panel underneath: the settings
            // window covers the panel area, so hiding afterwards can never expose the desktop
            // (hiding first left a one-frame gap while the new window was still cloaked).
            FlashFreeShow.Prepare(parked, () =>
            {
                parked.Activate();
                _mainWindow?.HidePanel();
            });
            parked.Show();
            return;
        }

        var created = new SettingsWindow(
            () => _settings,
            SaveSettings,
            ClearAllHistory,
            OpenDataFolder);

        // Only the current window may clear the field: a hidden predecessor closed here used to
        // null out the reference to its replacement (G-11).
        created.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, created))
            {
                _settingsWindow = null;
            }
        };
        _settingsWindow = created;
        // Hide the panel in the same frame the settings window becomes visible: hiding it when
        // settings merely activates leaves a desktop gap while the new window is still cloaked.
        _mainWindow?.CenterOverPanel(created);
        FlashFreeShow.Prepare(created, () =>
        {
            created.Activate();
            _mainWindow?.HidePanel();
        });
        created.Show();
    }

    private (bool Success, string Message) SaveSettings(AppSettings candidate)
    {
        var result = new SettingsSaveCoordinator().Save(
            candidate,
            registerHotkey: hotkey =>
            {
                var registered = _hotkey.Register(hotkey, out var error);
                return (registered, error);
            },
            makeLive: settings => _settings = settings,
            applyTheme: settings => ThemeService.Apply(settings.Theme),
            applyStartupEntry: settings =>
            {
                var enabled = _startup.SetEnabled(settings.StartWithWindows, StartupArguments);
                if (!enabled)
                {
                    // The registry write can fail (policy/ACL); the next launch reconciles, but say
                    // so instead of letting settings.json and the registry silently disagree (G-23).
                    AppLog.Write("Failed to update the startup entry; settings may disagree with the registry");
                }
                return enabled;
            },
            updateHotkey: settings => _mainWindow?.ViewModel.UpdateHotkey(settings.Hotkey),
            updateImagePreview: settings =>
            {
                // The panel reads the image preview mode through a dependency property so the
                // per-item size bindings re-run; a plain field would leave the old sizing until
                // the next restart.
                if (_mainWindow is { } panel)
                {
                    panel.ImagePreviewMode = settings.ImagePreview;
                    panel.ViewModel.SetImagePreviewMode(settings.ImagePreview);
                }
            },
            applyFloatingSettings: settings =>
            {
                // The floating bar is a global input listener: only run it while the user asked for
                // it, and pick up new timings immediately after saving.
                if (settings.NativeFloatingEnabled)
                {
                    StartFloatingBar();
                    _floatingBar?.ApplySettings();
                }
                else
                {
                    StopFloatingBar();
                }
            },
            persistSettings: TrySaveSettings);

        if (result.Exception is { } exception)
        {
            AppLog.Error("Apply settings side effects", exception);
        }

        return (result.Success, result.Message);
    }

    /// <summary>
    /// Arguments the autostart Run entry must carry: a <c>--data-dir</c> profile has to
    /// relaunch against the same profile, not the default store (U-01).
    /// </summary>
    private static string? StartupArguments => AppPaths.IsDefaultLocation
        ? null
        : StartupService.BuildDataDirectoryArguments(AppPaths.DataDirectory);

    private async void ClearAllHistory()
    {
        Window? owner = _settingsWindow is { IsVisible: true }
            ? _settingsWindow
            : _mainWindow is { IsVisible: true } ? _mainWindow : null;

        if (!FluentMessageWindow.Confirm(
                owner,
                "清空全部剪贴板历史？固定项也会被删除。",
                primary: "清空",
                secondary: "取消"))
        {
            return;
        }

        try
        {
            if (_mainWindow is { } panel)
            {
                // The view model clears its pending undo only after SQLite commits. On failure
                // both the row-level undo and its image remain available for retry.
                await panel.ViewModel.ClearAllAsync();
            }
            else
            {
                await ImageReferenceMutationGate.Semaphore.WaitAsync();
                try
                {
                    var images = await _repository.ClearAsync(includePinned: true);
                    foreach (var image in images)
                    {
                        ImageStore.TryDelete(image);
                    }
                }
                finally
                {
                    ImageReferenceMutationGate.Semaphore.Release();
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Clear all history", ex);
            _tray?.ShowBalloon("清空失败", "剪贴板历史未能清空，请检查数据目录后重试。");
        }
    }

    private static void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("Open data folder", ex);
        }
    }

    private void ExitApp()
    {
        _mainWindow?.PrepareForExit();
        _mainWindow?.Close();
        _tray?.Dispose();
        _monitor?.Dispose();
        _hotkey?.Dispose();

        // Stop the listener before tearing the window down: it used to be woken by its own
        // shutdown signal and queue a show against a window that was already closed.
        _floatingBar?.Dispose();
        _floatingBar = null;
        _showListener?.Dispose();
        _showListener = null;
        _compactListener?.Dispose();
        _compactListener = null;
        _compactEvent?.Dispose();
        _compactEvent = null;

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _monitor?.Dispose();
        _hotkey?.Dispose();
        _showListener?.Dispose();
        _showListener = null;
        _showEvent = null;
        _compactListener?.Dispose();
        _compactListener = null;
        _compactEvent?.Dispose();
        _compactEvent = null;
        _floatingBar?.Dispose();
        _floatingBar = null;

        // Releasing the mutex is the guard's job: it also handles the case where shutdown runs
        // on a thread that does not own it, instead of leaking a held mutex on exit.
        _singleInstance?.Dispose();
        _singleInstance = null;

        base.OnExit(e);
    }
}
