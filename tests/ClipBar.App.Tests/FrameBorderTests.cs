using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using ClipBar.App.Platform;
using ClipBar.App.Services;
using ClipBar.Core.Models;

namespace ClipBar.App.Tests;

/// <summary>
/// White-edge regressions: every own-surface window must pin the Win11 DWM border colour
/// instead of keeping the default — the default follows the active or inactive state and the
/// system theme, which read as a light "white edge" around a dark surface; DWMWA_COLOR_NONE
/// (the older attempt) made the frame transparent so the light taskbar shone through it.
/// DWMWA_BORDER_COLOR is set-only (a read-back fails with E_INVALIDARG), so the pin wiring is
/// observed through the call it makes, on real windows shown offscreen.
/// </summary>
public class FrameBorderTests
{
    private static void RunOnUiThread(Func<Task> work)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await work();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });

            Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class Recorder
    {
        public List<(IntPtr Hwnd, uint Color)> Calls { get; } = [];
    }

    /// <summary>
    /// Wires Pin with a recorder, shows the window offscreen and returns what the pin applied.
    /// </summary>
    private static async Task<Recorder> ShowOffscreenWithRecorderAsync(Window window)
    {
        var recorder = new Recorder();
        FrameBorder.Pin(window, (hwnd, color) => recorder.Calls.Add((hwnd, color)));

        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -3000;
        window.Top = -3000;

        var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ContentRendered += (_, _) => rendered.TrySetResult(true);

        window.Show();
        await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // One more idle turn so any late corner/backdrop work has run.
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

        return recorder;
    }

    [Fact]
    public async Task TrayMenuPinsTheDwmBorderOnShow()
    {
        await Task.Yield();
        RunOnUiThread(async () =>
        {
            var window = TrayMenuWindow.Create(
                [TrayMenuEntry.Item("entry", () => { }), TrayMenuEntry.Separator(), TrayMenuEntry.Item("exit", () => { })]);
            var recorder = await ShowOffscreenWithRecorderAsync(window);

            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.NotEqual(IntPtr.Zero, hwnd);
                // SourceInitialized and ContentRendered must both have pinned the frame; every
                // call carries the fixed colour, never the transparent trap value.
                Assert.True(
                    recorder.Calls.Count >= 2,
                    "pin did not fire at both SourceInitialized and ContentRendered: " + recorder.Calls.Count);
                Assert.All(recorder.Calls, call =>
                {
                    Assert.Equal(hwnd, call.Hwnd);
                    Assert.Equal(FrameBorder.CurrentColor, call.Color);
                });
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void PinnedColourDiffersFromTheTransparentTrap()
    {
        // The whole point of the fix: COLOR_NONE (0xFFFFFFFE) is the transparent frame that
        // showed the desktop through as a light band. Neither theme pin may be that value.
        Assert.NotEqual(0xFFFFFFFEu, FrameBorder.DarkColor);
        Assert.NotEqual(0xFFFFFFFEu, FrameBorder.LightColor);
        Assert.NotEqual(0xFFFFFFFEu, FrameBorder.CurrentColor);
    }

    [Fact]
    public void TrayMenuIsAPlainWindowSoAMissingThemeDictionaryCannotRestoreTheCaption()
    {
        // The user-visible white caption bar over the tray menu happened when the FluentWindow
        // implicit style (which lives in the Application theme dictionary) failed to resolve
        // for that instance: no WindowChrome, so DWM painted a native system-themed caption
        // over the first row. A plain Window with WindowStyle=None has no caption to paint,
        // whatever the theme dictionaries are doing.
        RunOnUiThread(async () =>
        {
            await Task.Yield();
            var menu = TrayMenuWindow.Create([TrayMenuEntry.Item("entry", () => { })]);
            try
            {
                Assert.IsNotType<Wpf.Ui.Controls.FluentWindow>(menu);
                Assert.Equal(WindowStyle.None, menu.WindowStyle);
                Assert.Equal(ResizeMode.NoResize, menu.ResizeMode);
            }
            finally
            {
                menu.Close();
            }
        });
    }

    /// <summary>
    /// Every own-surface window must be a plain Window: one FluentWindow left behind keeps one
    /// caption-race instance alive (the user hit the same light caption on the confirm dialog
    /// after the tray menu was fixed).
    /// </summary>
    [Fact]
    public void AllAppSurfacesArePlainWindows()
    {
        // Checked structurally through the generated types: men
        // instantiated off-screen in a test and still had to be built to be inspected.
        Assert.True(
            !typeof(Wpf.Ui.Controls.FluentWindow).IsAssignableFrom(typeof(MainWindow)),
            "MainWindow must not derive from FluentWindow");
        Assert.True(
            !typeof(Wpf.Ui.Controls.FluentWindow).IsAssignableFrom(typeof(SettingsWindow)),
            "SettingsWindow must not derive from FluentWindow");
        Assert.True(
            !typeof(Wpf.Ui.Controls.FluentWindow).IsAssignableFrom(typeof(FluentMessageWindow)),
            "FluentMessageWindow must not derive from FluentWindow");
        Assert.True(
            !typeof(Wpf.Ui.Controls.FluentWindow).IsAssignableFrom(typeof(TrayMenuWindow)),
            "TrayMenuWindow must not derive from FluentWindow");
        Assert.True(
            !typeof(Wpf.Ui.Controls.FluentWindow).IsAssignableFrom(typeof(CompactPanelWindow)),
            "CompactPanelWindow must not derive from FluentWindow");
        Assert.True(
            !typeof(Wpf.Ui.Controls.FluentWindow).IsAssignableFrom(typeof(FloatingActionBarWindow)),
            "FloatingActionBarWindow must not derive from FluentWindow");
    }
}
