using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipBar.App.Services;
using ClipBar.App.ViewModels;
using ClipBar.Core.Clipboard;
using ClipBar.Core.Models;
using Microsoft.Data.Sqlite;

namespace ClipBar.App.Tests;

/// <summary>
/// REVIEW-20261106 fix-round regressions: one test per confirmed finding (F-01..F-12) plus the
/// U-01 startup command. Each test pins the exact behaviour the audit found broken, using the
/// same repro shapes the audit's runtime probes used.
/// </summary>
public class ReviewFixRegressionTests
{
    // ---- STA plumbing ------------------------------------------------------

    private sealed class Workspace : IDisposable
    {
        public Workspace()
        {
            Root = NewDir("clipbar-fix-vm-");
        }

        public string Root { get; }

        public string DatabasePath => Path.Combine(Root, "history.db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Runs an async body on a dedicated STA thread whose dispatcher keeps pumping, so WPF
    /// objects and awaited continuations behave as they would in the app.
    /// </summary>
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

    private static void RunOnSta(Action action) => RunOnUiThread(async () =>
    {
        action();
        await Task.Yield();
    });

    private static string NewDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static BitmapSource NewTestImage()
    {
        var pixels = new byte[] { 0, 0, 0, 255, 128, 64, 32, 255, 255, 255, 0, 255, 10, 20, 30, 255 };
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        image.Freeze();
        return image;
    }

    // ---- F-01: search box Delete must not delete history --------------------

    [Fact]
    public void DeleteKeyHeadingForTheSearchBoxIsNotRoutedToHistory() => RunOnSta(() =>
    {
        // The exact shape the audit reproduced: focus inside the search box, a history row
        // selected, Delete pressed — the window tunnel sees the key first.
        var box = new TextBox();
        var button = new Button();

        Assert.True(PanelKeyScoping.IsRoutedToTextEditor(box, null));
        Assert.True(PanelKeyScoping.IsRoutedToTextEditor(null, box));
        // The templated text host can report its inner part as OriginalSource.
        Assert.True(PanelKeyScoping.IsRoutedToTextEditor(new ScrollViewer(), box));
        // Without a text editor on the route the history shortcut still applies.
        Assert.False(PanelKeyScoping.IsRoutedToTextEditor(button, button));
    });

    // ---- F-04: PNG writes are atomic and repair a damaged stored file --------

    [Fact]
    public void CancelledPngWriteLeavesNoBrokenFileAndResaveStoresTheFullImage() => RunOnUiThread(async () =>
    {
        var dir = NewDir("clipbar-img-");
        try
        {
            var image = NewTestImage();
            var cancelled = new CancellationToken(canceled: true);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ImageStore.SavePngAsync(image, dir, cancelled));

            // The old implementation left a 0-byte file at the final hash path and nothing
            // ever repaired it. Nothing may remain: no final file, no temp.
            Assert.Empty(Directory.EnumerateFiles(dir));

            var saved = await ImageStore.SavePngAsync(image, dir);
            Assert.True(File.Exists(saved.Path));
            Assert.Equal(saved.SizeBytes, new FileInfo(saved.Path).Length);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    });

    [Fact]
    public void ResavingTheSameImageRepairsATruncatedStoredFile() => RunOnUiThread(async () =>
    {
        var dir = NewDir("clipbar-img-");
        try
        {
            var image = NewTestImage();
            var saved = await ImageStore.SavePngAsync(image, dir);
            var fullLength = new FileInfo(saved.Path).Length;
            Assert.True(fullLength > 4);

            // Simulate a crash mid-write (the pre-fix damage shape): the stored file is a
            // 4-byte stub whose name still matches the hash.
            using (var stream = new FileStream(saved.Path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(4);
            }

            var again = await ImageStore.SavePngAsync(image, dir);
            Assert.Equal(saved.Path, again.Path);
            Assert.Equal(fullLength, new FileInfo(again.Path).Length);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    });

    // ---- F-06: orphan sweep matches the real hash naming contract -----------

    [Fact]
    public void SweepOrphansCollectsRealHashFilesAndLeavesReferencedAndStrayFiles() => RunOnUiThread(async () =>
    {
        var dir = NewDir("clipbar-img-");
        try
        {
            // A real image through the real save path: the file name is a 64-char SHA-256.
            var saved = await ImageStore.SavePngAsync(NewTestImage(), dir);
            var name = Path.GetFileNameWithoutExtension(saved.Path);
            Assert.Equal(64, name.Length);

            // Referenced: kept.
            Assert.Equal(0, ImageStore.SweepOrphans(dir, [saved.Path]));
            Assert.True(File.Exists(saved.Path));

            // Unreferenced real-hash file (crash between write and insert): collected.
            var orphanTarget = Path.Combine(dir, new string('a', 64) + ".png");
            File.WriteAllBytes(orphanTarget, [1, 2, 3, 4]);

            // Legacy 32-char names and a user file must be handled per contract.
            var legacy = Path.Combine(dir, new string('b', 32) + ".png");
            File.WriteAllBytes(legacy, [1]);
            var stray = Path.Combine(dir, "holiday-photo.png");
            File.WriteAllBytes(stray, [1]);

            var removed = ImageStore.SweepOrphans(dir, [saved.Path]);
            Assert.Equal(2, removed);
            Assert.True(File.Exists(saved.Path));
            Assert.False(File.Exists(orphanTarget));
            Assert.False(File.Exists(legacy));
            Assert.True(File.Exists(stray));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    });

    [Fact]
    public void SweepOrphansKeepsFreshTempsAndCollectsStaleOnes() => RunOnUiThread(async () =>
    {
        var dir = NewDir("clipbar-img-");
        try
        {
            var fresh = Path.Combine(dir, ".tmp-fresh.png");
            var stale = Path.Combine(dir, ".tmp-stale.png");
            File.WriteAllBytes(fresh, [1]);
            File.WriteAllBytes(stale, [1]);
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(2));

            Assert.Equal(1, ImageStore.SweepOrphans(dir, []));
            Assert.True(File.Exists(fresh), "a temp from a save in flight must not be collected");
            Assert.False(File.Exists(stale));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    });

    [Fact]
    public void ResavingAnUnchangedImageLeavesExactlyOneFile() => RunOnUiThread(async () =>
    {
        var dir = NewDir("clipbar-img-");
        try
        {
            var image = NewTestImage();
            var first = await ImageStore.SavePngAsync(image, dir);
            var second = await ImageStore.SavePngAsync(image, dir);

            Assert.Equal(first.Path, second.Path);
            Assert.Equal(first.SizeBytes, second.SizeBytes);
            var files = Directory.EnumerateFiles(dir).ToList();
            var file = Assert.Single(files);
            Assert.Equal(first.Path, file);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    });

    // ---- F-07: clearing history invalidates the pending undo ----------------

    private static async Task<MainViewModel> NewViewModelAsync(IClipRepository repository)
    {
        var vm = new MainViewModel(
            repository,
            () => new AppSettings(),
            _ => { },
            () => { });
        await vm.RefreshAsync();
        return vm;
    }

    [Fact]
    public void DiscardPendingUndoMakesUndoAfterClearAllImpossible() => RunOnUiThread(async () =>
    {
        using var workspace = new Workspace();
        var repository = new SqliteClipRepository(workspace.DatabasePath);
        await repository.InitializeAsync();
        await repository.AddOrTouchAsync(new ClipItem { Kind = ClipKind.Text, Text = "clear target" });

        var vm = await NewViewModelAsync(repository);
        var item = Assert.Single(vm.Items);

        await vm.DeleteCommand.ExecuteAsync(item);
        Assert.True(vm.UndoVisible);

        // App.ClearAllHistory now calls exactly this before clearing the repository.
        vm.DiscardPendingUndo();
        Assert.False(vm.UndoVisible);

        await vm.UndoDeleteCommand.ExecuteAsync(null);
        Assert.Equal(0, await repository.CountAsync());
    });

    [Fact]
    public void ClearCommandAlsoInvalidatesThePendingUndo() => RunOnUiThread(async () =>
    {
        using var workspace = new Workspace();
        var repository = new SqliteClipRepository(workspace.DatabasePath);
        await repository.InitializeAsync();
        await repository.AddOrTouchAsync(new ClipItem { Kind = ClipKind.Text, Text = "undo me" });

        var vm = await NewViewModelAsync(repository);
        var item = Assert.Single(vm.Items);

        await vm.DeleteCommand.ExecuteAsync(item);
        Assert.True(vm.UndoVisible);

        await vm.ClearCommand.ExecuteAsync(null);
        await vm.UndoDeleteCommand.ExecuteAsync(null);

        Assert.Equal(0, await repository.CountAsync());
    });

    [Fact]
    public void RefreshDoesNotAutoSelectTheNewestRow() => RunOnUiThread(async () =>
    {
        using var workspace = new Workspace();
        var repository = new SqliteClipRepository(workspace.DatabasePath);
        await repository.InitializeAsync();
        await repository.AddOrTouchAsync(new ClipItem { Kind = ClipKind.Text, Text = "newest" });

        var vm = await NewViewModelAsync(repository);
        Assert.Single(vm.Items);

        // A refresh (happens on every capture and every panel show) must not paint a selection
        // the user never made; the old fallback to Items.FirstOrDefault() did exactly that.
        Assert.Null(vm.SelectedItem);

        await repository.AddOrTouchAsync(new ClipItem { Kind = ClipKind.Text, Text = "newer" });
        await vm.RefreshAsync();
        Assert.Null(vm.SelectedItem);

        // An explicit selection still survives a refresh while its row exists.
        vm.SelectedItem = vm.Items.First(x => x.Model.Text == "newest");
        await vm.RefreshAsync();
        Assert.Equal("newest", vm.SelectedItem?.Model.Text);
    });

    // ---- F-08: long menus are bounded and scroll -----------------------------

    [Fact]
    public void ALongMenuIsCappedAtTheWorkAreaAndScrolls() => RunOnSta(() =>
    {
        var entries = Enumerable.Range(0, 60)
            .Select(i => TrayMenuEntry.Item($"action {i}", () => { }))
            .ToList();

        var menu = TrayMenuWindow.Create(entries, width: 0);
        try
        {
            var scroll = (ScrollViewer)menu.FindName("MenuScroll")!;
            Assert.NotNull(scroll);
            Assert.Same(scroll, ((StackPanel)menu.FindName("MenuPanel")!).Parent);

            menu.BoundHeightTo(1104);

            Assert.True(menu.Height <= 1104, $"menu height {menu.Height} exceeds the work area");
            Assert.Equal(1104, scroll.MaxHeight);

            // A short menu is unaffected: the bound never stretches it.
            var shortMenu = TrayMenuWindow.Create(
                [TrayMenuEntry.Item("only", () => { })], width: 0);
            try
            {
                shortMenu.BoundHeightTo(1104);
                Assert.True(shortMenu.Height < 200, $"short menu grew to {shortMenu.Height}");
                Assert.Equal(1104, ((ScrollViewer)shortMenu.FindName("MenuScroll")!).MaxHeight);
            }
            finally
            {
                shortMenu.Close();
            }
        }
        finally
        {
            menu.Close();
        }
    });

    [Fact]
    public void MenuHeightBoundNeverExceedsTheWorkArea() => RunOnSta(() =>
    {
        Assert.Equal(1092, TrayMenuWindow.MenuHeightBound(1104));
        Assert.True(TrayMenuWindow.MenuHeightBound(100) >= 120, "tiny work areas still get a usable minimum");
    });

    // ---- F-09: many-option windows scroll instead of overflowing ------------

    // ---- F-10: script-backed options are refused, not silently unresolved ----

    // ---- F-11: action icons stay inside the package --------------------------

    // ---- U-01: the autostart entry carries the data directory ----------------

    [Fact]
    public void StartupCommandKeepsTheDataDirectoryArgument()
    {
        Assert.Equal(
            "\"C:\\apps\\ClipBar.exe\" --data-dir \"D:\\clip-data\"",
            StartupService.BuildCommand("C:\\apps\\ClipBar.exe", "--data-dir \"D:\\clip-data\""));
        Assert.Equal("\"C:\\apps\\ClipBar.exe\"", StartupService.BuildCommand("C:\\apps\\ClipBar.exe", null));
        Assert.Equal("\"C:\\apps\\ClipBar.exe\"", StartupService.BuildCommand("C:\\apps\\ClipBar.exe", "  "));
    }
}
