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

    private SingleInstanceGuard? _singleInstance;
    private EventWaitHandle? _showEvent;
    private ShowSignalListener? _showListener;
    private JsonSettingsStore _settingsStore = null!;
    private AppSettings _settings = null!;
    private SqliteClipRepository _repository = null!;
    private ClipboardMonitor _monitor = null!;
    private HotkeyService _hotkey = null!;
    private PasteService _pasteService = null!;
    private TrayService _tray = null!;
    private StartupService _startup = null!;
    private ExtensionService _extensions = null!;
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;

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

        // Headless maintenance: rebuild (and optionally install) the SnipDo plugin from the
        // same exe, so this path can be scripted without opening any window.
        if (PluginCommandRunner.IsRequested(e.Args))
        {
            Environment.ExitCode = PluginCommandRunner.Run(
                e.Args,
                () => new SnipDoPluginExporter(),
                AppLog.Write,
                AppLog.Error);
            Shutdown();
            return;
        }

        // A second launch (including the one a SnipDo action starts) only wakes the
        // running instance; --show is what makes that explicit at the call site.
        var showOnStart = e.Args.Any(arg => string.Equals(arg, "--show", StringComparison.OrdinalIgnoreCase));

        _singleInstance = SingleInstanceGuard.TryAcquire(
            MutexName + AppPaths.InstanceSuffix,
            ShowEventName + AppPaths.InstanceSuffix);
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

            _extensions = new ExtensionService(AppPaths.ExtensionsDirectory, AppLog.Write);
            _extensions.Reload();
            AppLog.Write($"已加载 {_extensions.Extensions.Count} 个 SnipDo 扩展");

            _mainWindow = new MainWindow(
                _repository,
                () => _settings,
                _pasteService,
                _extensions,
                ShowSettings,
                NotePanelGeometry);

            _tray = new TrayService(
                () => ShowPanel("tray"),
                ShowSettings,
                ClearAllHistory,
                ExitApp,
                () => _monitor.IsPaused,
                ToggleCapturePaused);

            // G-02: a show signal can arrive while the repository init is still awaiting, before
            // the window exists; it used to vanish silently. Consume it now instead.
            if (_pendingShow)
            {
                _pendingShow = false;
                ShowPanel("deferred-signal");
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
                _startup.SetEnabled(true);
            }

            var actualStartup = _startup.IsEnabled;
            if (_settings.StartWithWindows != actualStartup)
            {
                _settings.StartWithWindows = actualStartup;
                TrySaveSettings();
            }

            AppLog.Write("ClipBar started");

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
    }

    private bool _pendingShow;

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
            FlashFreeShow.Prepare(parked, () => parked.Activate());
            parked.Show();
            return;
        }

        var created = new SettingsWindow(
            () => _settings,
            SaveSettings,
            ClearAllHistory,
            OpenDataFolder,
            _extensions);

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
        FlashFreeShow.Prepare(created, () => created.Activate());
        created.Show();
    }

    private (bool Success, string Message) SaveSettings(AppSettings candidate)
    {
        candidate.Normalize();

        // Register() restores the previous combo itself when the new one is rejected, so a
        // failed save never leaves the app without a working hotkey.
        if (!_hotkey.Register(candidate.Hotkey, out var hotkeyError))
        {
            return (false, hotkeyError);
        }

        _settings = candidate;

        try
        {
            // Apply() is a no-op when the theme is unchanged: re-applying it rebuilds every
            // window's backdrop, which left the acrylic panel painted flat white after a plain
            // 应用 click.
            ThemeService.Apply(_settings.Theme);
            if (!_startup.SetEnabled(_settings.StartWithWindows))
            {
                // The registry write can fail (policy/ACL); the next launch reconciles, but say
                // so instead of letting settings.json and the registry silently disagree (G-23).
                AppLog.Write("Failed to update the startup entry; settings may disagree with the registry");
            }
            _mainWindow?.ViewModel.UpdateHotkey(_settings.Hotkey);

            // The panel reads the image preview mode through a dependency property so the
            // per-item size bindings re-run; a plain field would leave the old sizing until the
            // next restart.
            if (_mainWindow is { } panel)
            {
                panel.ImagePreviewMode = _settings.ImagePreview;
                panel.ViewModel.SetImagePreviewMode(_settings.ImagePreview);
            }
        }
        catch (Exception ex)
        {
            // The candidate is already live; the caller keeps the form dirty so the state the
            // user sees is what is running (R12-F-10).
            AppLog.Error("Apply settings side effects", ex);
            return (false, "设置已生效，但应用过程中出错：" + ex.Message + "（未写入设置文件，重启后会恢复为修改前的值。）");
        }

        if (!TrySaveSettings())
        {
            // The settings are live in memory either way; just say they will not survive a restart.
            return (false, "设置已应用，但无法写入设置文件，重启后会丢失。");
        }

        return (true, string.Empty);
    }

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

        var images = await _repository.ClearAsync(includePinned: true);
        foreach (var image in images)
        {
            ImageStore.TryDelete(image);
        }

        if (_mainWindow is not null)
        {
            await _mainWindow.ViewModel.RefreshAsync();
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
        _showListener?.Dispose();
        _showListener = null;

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

        // Releasing the mutex is the guard's job: it also handles the case where shutdown runs
        // on a thread that does not own it, instead of leaking a held mutex on exit.
        _singleInstance?.Dispose();
        _singleInstance = null;

        base.OnExit(e);
    }
}
