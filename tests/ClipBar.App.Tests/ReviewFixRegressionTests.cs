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
using ClipBar.Core.Extensions;
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

    // ---- F-02: failed replacement import keeps the old package --------------

    private const string GoodManifest = """{ "name": "Roll", "identifier": "roll.back", "actions": [ { "title": "Run", "url": "https://example.com/" } ] }""";
    private const string NewerManifest = """{ "name": "RollNew", "identifier": "roll.back", "actions": [ { "title": "Run2", "url": "https://example.org/" } ] }""";

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, System.Text.Encoding.UTF8);
    }

    private static string ReadStoreManifest(string store, string markerNeedle)
    {
        var folder = Directory.EnumerateDirectories(store).Single(f => !Path.GetFileName(f).StartsWith('.'));
        Assert.Contains(markerNeedle, File.ReadAllText(Path.Combine(folder, "extension.json")));
        return folder;
    }

    private static void AssertNoImportResidue(string store)
    {
        Assert.Empty(Directory.EnumerateDirectories(store, ".import-*"));
        Assert.Empty(Directory.EnumerateDirectories(store, ".backup-*"));
    }

    [Fact]
    public async Task FolderImportFailureLeavesTheInstalledPackageUntouched()
    {
        var root = NewDir("clipbar-fix-");
        try
        {
            var store = Path.Combine(root, "extensions");
            var service = new ExtensionService(store);

            var installed = Path.Combine(root, "installed-src");
            WriteFile(Path.Combine(installed, "extension.json"), GoodManifest);
            WriteFile(Path.Combine(installed, "marker.txt"), "original");
            await service.ImportFolderAsync(installed);
            Assert.Single(service.Extensions);

            // A different source with the same id, one file locked with an exclusive handle:
            // the copy fails mid-flight and the old package must still be in the store.
            var replacement = Path.Combine(root, "replacement-src");
            WriteFile(Path.Combine(replacement, "extension.json"), NewerManifest);
            WriteFile(Path.Combine(replacement, "locked.bin"), "payload");
            using (new FileStream(
                Path.Combine(replacement, "locked.bin"),
                FileMode.Open,
                FileAccess.Read,
                FileShare.None))
            {
                // The exclusive lock makes the copy fail mid-flight (the audit's locked.bin
                // shape); the old implementation had already deleted the store copy by then.
                await Assert.ThrowsAsync<IOException>(
                    () => service.ImportFolderAsync(replacement));
            }

            var folder = ReadStoreManifest(store, "\"Roll\"");
            Assert.True(File.Exists(Path.Combine(folder, "marker.txt")), "old package was deleted by the failed import");
            Assert.DoesNotContain("\"RollNew\"", File.ReadAllText(Path.Combine(folder, "extension.json")));
            AssertNoImportResidue(store);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ZipImportFailureDuringTheSwapKeepsTheOldPackage()
    {
        var root = NewDir("clipbar-fix-");
        try
        {
            var store = Path.Combine(root, "extensions");
            var service = new ExtensionService(store);

            // First import the good package through the real zip path.
            var packageDir = Path.Combine(root, "pkg");
            Directory.CreateDirectory(packageDir);
            var packagePath = Path.Combine(packageDir, "good.pbar");
            using (var stream = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var manifest = archive.CreateEntry("extension.json");
                using (var writer = new StreamWriter(manifest.Open()))
                {
                    writer.Write(GoodManifest);
                }

                var marker = archive.CreateEntry("marker.txt");
                using (var writer = new StreamWriter(marker.Open()))
                {
                    writer.Write("original");
                }
            }

            await service.ImportAsync(packagePath);
            var folder = ReadStoreManifest(store, "\"Roll\"");

            var newer = Path.Combine(packageDir, "newer.pbar");
            using (var stream = new FileStream(newer, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var manifest = archive.CreateEntry("extension.json");
                using (var writer = new StreamWriter(manifest.Open()))
                {
                    writer.Write(NewerManifest);
                }
            }

            // Locking a file inside the installed folder makes the backup move fail: the
            // import aborts and the old package (including the locked file) must survive.
            var lockedPath = Path.Combine(folder, "marker.txt");
            using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Assert.ThrowsAsync<IOException>(() => service.ImportAsync(newer));
            }

            Assert.True(File.Exists(lockedPath));
            Assert.Contains("\"Roll\"", File.ReadAllText(Path.Combine(folder, "extension.json")));
            AssertNoImportResidue(store);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ---- F-05: slug collisions must not displace another extension ----------

    [Fact]
    public async Task DifferentIdentifiersThatSlugToTheSameFolderDoNotReplaceEachOther()
    {
        var root = NewDir("clipbar-fix-");
        try
        {
            var store = Path.Combine(root, "extensions");
            var service = new ExtensionService(store);

            var first = Path.Combine(root, "first-src");
            WriteFile(Path.Combine(first, "extension.json"),
                """{ "name": "A", "identifier": "audit/a", "actions": [ { "title": "Run", "url": "https://example.com/" } ] }""");
            WriteFile(Path.Combine(first, "marker-a.txt"), "a");
            await service.ImportFolderAsync(first);

            // "audit-a" slugs to the same folder name as "audit/a". Importing it used to
            // delete the first extension; it must now be refused.
            var second = Path.Combine(root, "second-src");
            WriteFile(Path.Combine(second, "extension.json"),
                """{ "name": "B", "identifier": "audit-a", "actions": [ { "title": "Run", "url": "https://example.org/" } ] }""");
            await Assert.ThrowsAsync<SnipDoManifestException>(() => service.ImportFolderAsync(second));

            var loaded = Assert.Single(service.Extensions);
            Assert.Equal("audit/a", loaded.Extension.Identifier);
            Assert.True(File.Exists(Path.Combine(loaded.FolderPath, "marker-a.txt")));
            AssertNoImportResidue(store);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ReimportingTheSameIdentifierStillReplacesTheExtension()
    {
        var root = NewDir("clipbar-fix-");
        try
        {
            var store = Path.Combine(root, "extensions");
            var service = new ExtensionService(store);

            var first = Path.Combine(root, "first-src");
            WriteFile(Path.Combine(first, "extension.json"), GoodManifest);
            WriteFile(Path.Combine(first, "old-only.txt"), "stale");
            await service.ImportFolderAsync(first);

            var second = Path.Combine(root, "second-src");
            WriteFile(Path.Combine(second, "extension.json"), NewerManifest);
            await service.ImportFolderAsync(second);

            var loaded = Assert.Single(service.Extensions);
            Assert.Equal("roll.back", loaded.Extension.Identifier);
            Assert.Contains("\"RollNew\"", File.ReadAllText(Path.Combine(loaded.FolderPath, "extension.json")));
            Assert.False(File.Exists(Path.Combine(loaded.FolderPath, "old-only.txt")), "stale file from the previous package survived replacement");
            AssertNoImportResidue(store);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ImportingAnAlreadyInstalledFolderOntoItselfIsSafe()
    {
        var root = NewDir("clipbar-fix-");
        try
        {
            var store = Path.Combine(root, "extensions");
            var service = new ExtensionService(store);

            var source = Path.Combine(root, "installed-src");
            WriteFile(Path.Combine(source, "extension.json"), GoodManifest);
            WriteFile(Path.Combine(source, "marker.txt"), "original");
            var first = await service.ImportFolderAsync(source);

            // Source == store folder: the staging copy completes before the swap, so the
            // import keeps exactly one folder with the manifest intact.
            var again = await service.ImportFolderAsync(first.FolderPath);

            var loaded = Assert.Single(service.Extensions);
            Assert.Equal(first.FolderPath, loaded.FolderPath);
            Assert.Equal(again.FolderPath, loaded.FolderPath);
            Assert.Contains("\"Roll\"", File.ReadAllText(Path.Combine(loaded.FolderPath, "extension.json")));
            Assert.True(File.Exists(Path.Combine(loaded.FolderPath, "marker.txt")));
            AssertNoImportResidue(store);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ReloadIgnoresImportStagingFolders()
    {
        var root = NewDir("clipbar-fix-");
        try
        {
            var store = Path.Combine(root, "extensions");
            Directory.CreateDirectory(store);
            var staging = Path.Combine(store, ".import-abcdef01");
            WriteFile(Path.Combine(staging, "extension.json"), GoodManifest);

            var service = new ExtensionService(store);
            service.Reload();

            Assert.Empty(service.Extensions);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ---- F-03: malformed SVG colours must not break the menu ----------------

    private const string BadColorSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
          <rect x="0" y="0" width="24" height="24" fill="#GGGGGG" />
        </svg>
        """;

    private const string GoodColorSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
          <rect x="0" y="0" width="24" height="24" fill="#000000" />
        </svg>
        """;

    private static string WriteSvg(string dir, string name, string content)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content, System.Text.Encoding.UTF8);
        return path;
    }

    [Fact]
    public void MalformedSvgColorDoesNotThrowAndMenuConstructionSurvives() => RunOnSta(() =>
    {
        var pkg = NewDir("clipbar-svg-");
        try
        {
            WriteSvg(pkg, "icon.svg", BadColorSvg);
            var path = Path.Combine(pkg, "icon.svg");

            // The old parser threw FormatException for an invalid hex colour; the loader now
            // degrades to a fallback instead of letting the exception escape.
            var exception = Record.Exception(() => SvgIconLoader.Load(path, 16));
            Assert.Null(exception);

            // The full menu-construction path used to be aborted by the same icon.
            var extension = new SnipDoExtension
            {
                Name = "Bad",
                Identifier = "bad.color",
                RootPath = pkg,
                IconColored = "icon.svg",
                Actions =
                [
                    new SnipDoAction { Title = "Run", Url = "https://example.com/", IconColored = "icon.svg" },
                ],
            };
            var loaded = new LoadedExtension(extension, pkg);
            var model = new ClipItem { Kind = ClipKind.Text, Text = "hello world" };

            var actions = MainViewModel.CollectApplicableActions([loaded], model, "notepad.exe");
            var action = Assert.Single(actions);
            Assert.Equal("Run", action.Title);
        }
        finally
        {
            try { Directory.Delete(pkg, recursive: true); } catch (IOException) { }
        }
    });

    [Fact]
    public void UnclosedRgbFunctionReturnsNullInsteadOfThrowing() => RunOnSta(() =>
    {
        var pkg = NewDir("clipbar-svg-");
        try
        {
            // "rgb(1,2,3" has no closing paren: the old range slice threw ArgumentOutOfRangeException.
            var path = WriteSvg(pkg, "icon.svg",
                """
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
                  <rect x="0" y="0" width="24" height="24" fill="rgb(1,2,3" />
                </svg>
                """);

            var exception = Record.Exception(() => SvgIconLoader.Load(path, 16));
            Assert.Null(exception);
        }
        finally
        {
            try { Directory.Delete(pkg, recursive: true); } catch (IOException) { }
        }
    });

    [Fact]
    public void ValidSvgColorStillRenders() => RunOnSta(() =>
    {
        var pkg = NewDir("clipbar-svg-");
        try
        {
            var path = WriteSvg(pkg, "icon.svg", GoodColorSvg);
            Assert.NotNull(SvgIconLoader.Load(path, 16));
        }
        finally
        {
            try { Directory.Delete(pkg, recursive: true); } catch (IOException) { }
        }
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

    private static async Task<MainViewModel> NewViewModelAsync(
        Workspace workspace,
        SqliteClipRepository repository)
    {
        var vm = new MainViewModel(
            repository,
            () => new AppSettings(),
            _ => { },
            () => { },
            new ExtensionService(Path.Combine(workspace.Root, "extensions")),
            new ExtensionActionRunner(() => IntPtr.Zero),
            _ => null,
            _ => { },
            () => null);
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

        var vm = await NewViewModelAsync(workspace, repository);
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

        var vm = await NewViewModelAsync(workspace, repository);
        var item = Assert.Single(vm.Items);

        await vm.DeleteCommand.ExecuteAsync(item);
        Assert.True(vm.UndoVisible);

        await vm.ClearCommand.ExecuteAsync(null);
        await vm.UndoDeleteCommand.ExecuteAsync(null);

        Assert.Equal(0, await repository.CountAsync());
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

    [Fact]
    public void OptionWindowWith24SelectorsStaysInsideTheWorkArea() => RunOnSta(() =>
    {
        var options = Enumerable.Range(0, 24)
            .Select(i => new SnipDoAppOption
            {
                Identifier = $"option_{i}",
                Values = new Dictionary<string, string> { ["a"] = $"value-{i}" },
            })
            .ToList();
        var action = new SnipDoAction { Title = "With options", Url = "https://example.com/" };

        var window = new AppOptionWindow(action, options);
        try
        {
            var scroll = (ScrollViewer)window.FindName("OptionScroll")!;
            Assert.NotNull(scroll);
            Assert.Same(scroll, ((StackPanel)window.FindName("OptionPanel")!).Parent);

            // The selector area must be capped below the work area so the button row — and
            // every selector behind the cap — stays reachable.
            Assert.True(scroll.MaxHeight > 0);
            Assert.True(scroll.MaxHeight <= SystemParameters.WorkArea.Height,
                $"selector cap {scroll.MaxHeight} exceeds the work area {SystemParameters.WorkArea.Height}");
        }
        finally
        {
            window.Close();
        }
    });

    // ---- F-10: script-backed options are refused, not silently unresolved ----

    [Fact]
    public async Task ActionWithScriptBackedOptionIsRefusedBeforeAnyExecutorRuns()
    {
        var runner = new ExtensionActionRunner(() => IntPtr.Zero);
        var executed = 0;
        runner.Register(
            SnipDoActionKind.Url,
            (_, _) =>
            {
                executed++;
                return Task.FromResult(new ActionResult(true, null, null));
            });

        var action = new SnipDoAction
        {
            Title = "Script option",
            Url = "https://example.invalid/?x={APPOPTION value}",
            Options =
            [
                new SnipDoAppOption
                {
                    Identifier = "value",
                    Values = [],
                    PowershellFile = "values.ps1",
                },
            ],
        };

        var result = await runner.RunAsync(action, "clip text", null, null);

        Assert.False(result.Success);
        Assert.Contains("不支持", result.Error);
        Assert.Equal(0, executed);
    }

    [Fact]
    public async Task ActionWithStaticOptionValuesStillRuns()
    {
        var runner = new ExtensionActionRunner(() => IntPtr.Zero);
        var executed = 0;
        runner.Register(
            SnipDoActionKind.Url,
            (_, _) =>
            {
                executed++;
                return Task.FromResult(new ActionResult(true, null, null));
            });

        var action = new SnipDoAction
        {
            Title = "Static option",
            Url = "https://example.com/?x={APPOPTION value}",
            Options =
            [
                new SnipDoAppOption
                {
                    Identifier = "value",
                    Values = new Dictionary<string, string> { ["a"] = "1" },
                },
            ],
        };

        var result = await runner.RunAsync(action, "clip text", null, new Dictionary<string, string> { ["value"] = "1" });

        Assert.True(result.Success);
        Assert.Equal(1, executed);
    }

    // ---- F-11: action icons stay inside the package --------------------------

    [Fact]
    public void ActionIconsCannotEscapeThePackage() => RunOnSta(() =>
    {
        var root = NewDir("clipbar-icon-");
        try
        {
            var pkg = Path.Combine(root, "pkg");
            WriteSvg(pkg, "inside.svg", GoodColorSvg);
            WriteSvg(root, "outside.svg", GoodColorSvg);

            var extension = new SnipDoExtension { Name = "Icon", Identifier = "icon.test", RootPath = pkg };
            var loaded = new LoadedExtension(extension, pkg);

            // Inside the package: loads.
            var inside = new ExtensionActionViewModel(
                loaded, new SnipDoAction { Title = "In", IconColored = "inside.svg" });
            Assert.NotNull(inside.IconSource);

            // ../ escape: the old consumer resolver read it; containment returns null.
            var escape = new ExtensionActionViewModel(
                loaded, new SnipDoAction { Title = "Out", IconColored = "../outside.svg" });
            Assert.Null(escape.IconSource);

            // Absolute path: same refusal.
            var absolute = new ExtensionActionViewModel(
                loaded, new SnipDoAction { Title = "Abs", IconColored = Path.Combine(root, "outside.svg") });
            Assert.Null(absolute.IconSource);

            // Missing file: unchanged behaviour.
            var missing = new ExtensionActionViewModel(
                loaded, new SnipDoAction { Title = "Missing", IconColored = "missing.svg" });
            Assert.Null(missing.IconSource);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    });

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
