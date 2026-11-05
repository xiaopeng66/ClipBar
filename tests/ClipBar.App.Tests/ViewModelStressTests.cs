using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using ClipBar.App.Services;
using ClipBar.App.ViewModels;
using ClipBar.Core.Clipboard;
using ClipBar.Core.Models;

namespace ClipBar.App.Tests;

/// <summary>
/// Plan §5.2 T-28 / T-30: the view-model's own guards — debounced search and the single-slot
/// extension runner. <see cref="MainViewModel"/> builds a WPF <c>CollectionView</c> in its
/// constructor, and that view refuses collection changes from any thread other than the one that
/// created it, so every test body runs on its own STA thread with a live dispatcher.
/// </summary>
public class ViewModelStressTests
{
    private sealed class Workspace : IDisposable
    {
        public Workspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "clipbar-vm-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string DatabasePath => Path.Combine(Root, "history.db");

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
    /// Runs <paramref name="work"/> on a dedicated STA thread whose dispatcher keeps pumping, so
    /// awaits inside the view model resume where the collection view expects them.
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

    private static MainViewModel NewViewModel(IClipRepository repository, ExtensionService extensions)
        => NewViewModel(repository, extensions, _ => { });

    private static MainViewModel NewViewModel(
        IClipRepository repository,
        ExtensionService extensions,
        Action<Exception> refreshFailed)
        => new(
            repository,
            () => new AppSettings(),
            _ => { },
            () => { },
            extensions,
            new ExtensionActionRunner(() => IntPtr.Zero),
            _ => null,
            _ => { },
            () => null,
            refreshFailed);

    private static async Task<SqliteClipRepository> NewRepositoryAsync(Workspace workspace)
    {
        var repository = new SqliteClipRepository(workspace.DatabasePath);
        await repository.InitializeAsync();
        return repository;
    }

    /// <summary>Writes a PowerShell action script and returns the action pointing at it.</summary>
    private static SnipDoAction WriteAction(string scriptDir, string fileName, string body)
    {
        Directory.CreateDirectory(scriptDir);
        File.WriteAllText(Path.Combine(scriptDir, fileName), body, System.Text.Encoding.UTF8);
        return new SnipDoAction
        {
            Title = fileName,
            PowershellFile = fileName,
            ScriptRoot = scriptDir,
        };
    }

    private static SnipDoExtension NewExtension(SnipDoAction action) => new()
    {
        Name = "Stress",
        Identifier = "stress",
        Actions = [action],
    };

    // ---- T-30: double-clicking an extension action -----------------------

    [Fact]
    public void FiveRapidClicksOnAnExtensionRunTheScriptOnce() => RunOnUiThread(async () =>
    {
        using var workspace = new Workspace();
        var repository = await NewRepositoryAsync(workspace);
        var extensions = new ExtensionService(Path.Combine(workspace.Root, "extensions"));

        var scriptDir = Path.Combine(workspace.Root, "ext");
        var counter = Path.Combine(workspace.Root, "runs.txt");

        // The script records one line per launch and then dawdles, so overlapping calls have a
        // real window in which to double-spawn a process.
        var action = WriteAction(
            scriptDir,
            "slow.ps1",
            """
            param([string]$PLAIN_TEXT)
            Add-Content -Path $PLAIN_TEXT -Value 'run'
            Start-Sleep -Milliseconds 250
            """);

        var vm = NewViewModel(repository, extensions);
        vm.SelectedItem = new ClipItemViewModel(new ClipItem { Kind = ClipKind.Text, Text = counter });
        var entry = new ExtensionActionViewModel(new LoadedExtension(NewExtension(action), scriptDir), action);

        // Five clicks, all issued before the first run has finished.
        var clicks = Enumerable.Range(0, 5)
            .Select(_ => vm.RunExtensionCommand.ExecuteAsync(new ExtensionActionRequest(vm.SelectedItem!, entry)))
            .ToArray();
        await Task.WhenAll(clicks);

        Assert.True(File.Exists(counter), "扩展脚本根本没有执行");
        var lines = File.ReadAllLines(counter).Where(l => l.Trim().Length > 0).ToArray();
        Assert.Single(lines);
    });

    [Fact]
    public void ASecondRunIsAllowedAfterTheFirstFinishes() => RunOnUiThread(async () =>
    {
        using var workspace = new Workspace();
        var repository = await NewRepositoryAsync(workspace);
        var extensions = new ExtensionService(Path.Combine(workspace.Root, "extensions"));

        var scriptDir = Path.Combine(workspace.Root, "ext");
        var counter = Path.Combine(workspace.Root, "runs.txt");
        var action = WriteAction(
            scriptDir,
            "fast.ps1",
            """
            param([string]$PLAIN_TEXT)
            Add-Content -Path $PLAIN_TEXT -Value 'run'
            """);

        var vm = NewViewModel(repository, extensions);
        vm.SelectedItem = new ClipItemViewModel(new ClipItem { Kind = ClipKind.Text, Text = counter });
        var entry = new ExtensionActionViewModel(new LoadedExtension(NewExtension(action), scriptDir), action);

        // The guard must be released in `finally`, not leaked: a second deliberate click works.
        await vm.RunExtensionCommand.ExecuteAsync(new ExtensionActionRequest(vm.SelectedItem!, entry));
        await vm.RunExtensionCommand.ExecuteAsync(new ExtensionActionRequest(vm.SelectedItem!, entry));

        Assert.Equal(2, File.ReadAllLines(counter).Count(l => l.Trim().Length > 0));
    });

    [Fact]
    public void ANullRequestDoesNothing() => RunOnUiThread(async () =>
    {
        using var workspace = new Workspace();
        var repository = await NewRepositoryAsync(workspace);
        var extensions = new ExtensionService(Path.Combine(workspace.Root, "extensions"));
        var vm = NewViewModel(repository, extensions);

        var action = new SnipDoAction { Title = "Never", KeyCombo = "^v" };
        var entry = new ExtensionActionViewModel(
            new LoadedExtension(NewExtension(action), workspace.Root),
            action);

        // The clip travels with the request now, so there is no "no selection" state to check —
        // a missing request must still return immediately instead of sending keystrokes.
        await vm.RunExtensionCommand.ExecuteAsync(null);
        await vm.RunExtensionCommand.ExecuteAsync(null);
    });

    // ---- P-0: the query limit must follow MaxItems, not a hardcoded 500 ----

    [Fact]
    public void QueryLimitFollowsTheMaxItemsSetting() => RunOnUiThread(async () =>
    {
        using var image = new PreviewImageFile();
        var repository = new ImagePreviewRepository();
        var settings = new AppSettings { MaxItems = 777 };
        var vm = new MainViewModel(
            repository,
            () => settings,
            _ => { },
            () => { },
            new ExtensionService(Path.Combine(image.ExtensionDirectory, "extensions")),
            new ExtensionActionRunner(() => IntPtr.Zero),
            _ => null,
            _ => { },
            () => null);

        await vm.RefreshAsync();

        Assert.Equal(777, repository.LastLimit);
    });

    // ---- A6: delete offers undo and undo restores the row ----

    [Fact]
    public void DeleteOffersUndoAndUndoRestoresTheRow() => RunOnUiThread(async () =>
    {
        using var workspace = new Workspace();
        var repository = await NewRepositoryAsync(workspace);
        await repository.AddOrTouchAsync(new ClipItem { Kind = ClipKind.Text, Text = "undo me" });

        Action? undo = null;
        var vm = new MainViewModel(
            repository,
            () => new AppSettings(),
            _ => { },
            () => { },
            new ExtensionService(Path.Combine(workspace.Root, "extensions")),
            new ExtensionActionRunner(() => IntPtr.Zero),
            _ => null,
            _ => { },
            () => null,
            refreshFailed: null,
            notifyWithAction: (_, action) => undo = action);

        await vm.RefreshAsync();
        var item = Assert.Single(vm.Items);

        await vm.DeleteCommand.ExecuteAsync(item);

        Assert.Empty(vm.Items);
        Assert.NotNull(undo);

        undo!();

        // Undo is fire-and-forget on the UI thread; wait for the row to come back.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && vm.Items.Count == 0)
        {
            await Task.Delay(50);
        }

        Assert.Contains(vm.Items, x => x.Model.Text == "undo me");
        Assert.Equal(1, await repository.CountAsync());
    });

    // ---- right-click menu: per-clip matching and captured-clip execution ----

    [Fact]
    public void ApplicableActionsFollowTheRequestedClipNotTheSelection() => RunOnUiThread(async () =>
    {
        using var workspace = new Workspace();
        var repository = await NewRepositoryAsync(workspace);
        var extensions = new ExtensionService(Path.Combine(workspace.Root, "extensions"));

        // Regex-init actions come from real packages ("open this URL with Bing"); only a clip
        // whose text matches may offer it, regardless of what the panel has selected.
        var action = new SnipDoAction
        {
            Title = "Bing",
            Url = "https://www.bing.com/search?q={URLENCODED_TEXT}",
            Regex = "bing",
        };
        var vm = NewViewModel(repository, extensions);
        var entry = new ExtensionActionViewModel(new LoadedExtension(NewExtension(action), workspace.Root), action);

        var bing = new ClipItemViewModel(new ClipItem { Id = "bing", Kind = ClipKind.Text, Text = "bing clipbar" });
        var other = new ClipItemViewModel(new ClipItem { Id = "other", Kind = ClipKind.Text, Text = "nothing to match" });
        var image = new ClipItemViewModel(new ClipItem { Id = "image", Kind = ClipKind.Image, Text = "bing" });
        var loaded = new LoadedExtension(NewExtension(action), workspace.Root);

        vm.SelectedItem = other;

        // Matching is per clip: the same panel state offers different actions for different rows,
        // and images never get any.
        Assert.Empty(vm.GetApplicableActions(image));
        Assert.Empty(MainViewModel.CollectApplicableActions([loaded], other.Model, null));
        var forBing = MainViewModel.CollectApplicableActions([loaded], bing.Model, null);
        Assert.Single(forBing);
        Assert.Equal("Bing", forBing[0].Title);
    });

    [Fact]
    public void AContextRequestRunsAgainstTheRightClickedClip() => RunOnUiThread(async () =>
    {
        using var workspace = new Workspace();
        var repository = await NewRepositoryAsync(workspace);
        var extensions = new ExtensionService(Path.Combine(workspace.Root, "extensions"));

        var scriptDir = Path.Combine(workspace.Root, "ext");
        var action = WriteAction(
            scriptDir,
            "echo.ps1",
            """
            param([string]$PLAIN_TEXT)
            Add-Content -Path $PLAIN_TEXT -Value 'run'
            """);

        // The clip text doubles as the script's output path, so the run itself proves which clip
        // the action executed against.
        var clipA = new ClipItemViewModel(new ClipItem { Kind = ClipKind.Text, Text = Path.Combine(workspace.Root, "from-a.txt") });
        var clipB = new ClipItemViewModel(new ClipItem { Kind = ClipKind.Text, Text = Path.Combine(workspace.Root, "from-b.txt") });
        var vm = NewViewModel(repository, extensions);
        var entry = new ExtensionActionViewModel(new LoadedExtension(NewExtension(action), scriptDir), action);
        vm.SelectedItem = clipA;

        // The menu was opened on B; moving the selection afterwards must not redirect the run.
        await vm.RunExtensionCommand.ExecuteAsync(new ExtensionActionRequest(clipB, entry));

        Assert.True(File.Exists(Path.Combine(workspace.Root, "from-b.txt")), "右键的记录没有被执行");
        Assert.False(File.Exists(Path.Combine(workspace.Root, "from-a.txt")), "动作跑到了选中的另一条记录上");
    });

    private sealed class ImagePreviewRepository : IClipRepository
    {
        public IReadOnlyList<ClipItem> Results { get; set; } = [];

        public int LastLimit { get; private set; } = -1;

        public Task<IReadOnlyList<ClipItem>> QueryAsync(
            ClipFilter filter,
            string? search,
            int limit = 200,
            CancellationToken cancellationToken = default)
        {
            LastLimit = limit;
            return Task.FromResult(Results);
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ClipItem> AddOrTouchAsync(ClipItem item, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ClipItem?> GetAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetPinnedAsync(string id, bool pinned, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string?> DeleteAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ClearAsync(
            bool includePinned = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> CountAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> TrimAsync(
            int maxNonPinned,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetImagePathsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class PreviewImageFile : IDisposable
    {
        private readonly string _directory;

        public PreviewImageFile()
        {
            _directory = Path.Combine(Path.GetTempPath(), "clipbar-main-vm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            FilePath = Path.Combine(_directory, "image.png");
            ExtensionDirectory = Path.Combine(_directory, "extensions");
            File.WriteAllBytes(FilePath, []);
        }

        public string FilePath { get; }

        public string ExtensionDirectory { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // ---- T-51: a background refresh that fails must be reported -------------

    /// <summary>
    /// Stands in for a history file that became unreadable under a running instance (measured:
    /// <c>QueryAsync</c> then throws <c>SqliteException: no such table: clips</c>). Everything else
    /// is unimplemented because the refresh path only queries.
    /// </summary>
    private sealed class FaultingRepository : IClipRepository
    {
        public bool ShouldFail { get; set; } = true;

        public int Queries { get; private set; }

        public Task<IReadOnlyList<ClipItem>> QueryAsync(
            ClipFilter filter, string? search, int limit = 200, CancellationToken cancellationToken = default)
        {
            Queries++;
            if (ShouldFail)
            {
                return Task.FromException<IReadOnlyList<ClipItem>>(
                    new InvalidOperationException("no such table: clips"));
            }

            return Task.FromResult<IReadOnlyList<ClipItem>>([]);
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ClipItem> AddOrTouchAsync(ClipItem item, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ClipItem?> GetAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetPinnedAsync(string id, bool pinned, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string?> DeleteAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ClearAsync(
            bool includePinned = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> CountAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> TrimAsync(int maxNonPinned, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetImagePathsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public void AFailedBackgroundRefreshIsReportedInsteadOfVanishing() => RunOnUiThread(async () =>
    {
        using var workspace = new Workspace();
        var repository = new FaultingRepository();
        var extensions = new ExtensionService(Path.Combine(workspace.Root, "extensions"));

        var reported = new List<Exception>();
        var vm = NewViewModel(repository, extensions, reported.Add);

        // The panel calls this on every show and after every capture. Before the fix the exception
        // was never observed: .NET swallows unobserved task faults (measured), so the list simply
        // stayed stale with nothing in the log.
        vm.RefreshInBackground();

        // Let the faulted task be observed by the wrapper.
        for (var i = 0; i < 50 && reported.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Single(reported);
        Assert.Equal(1, repository.Queries);
    });

    [Fact]
    public void ARepeatedlyFailingRefreshIsReportedOnlyOnce() => RunOnUiThread(async () =>
    {
        using var workspace = new Workspace();
        var repository = new FaultingRepository();
        var extensions = new ExtensionService(Path.Combine(workspace.Root, "extensions"));

        var reported = new List<Exception>();
        var vm = NewViewModel(repository, extensions, reported.Add);

        // Every keystroke schedules a refresh; a permanently broken database must not produce one
        // log line and one dialog per character typed.
        for (var round = 0; round < 5; round++)
        {
            vm.SearchText = $"q{round}";
            vm.SelectedFilter = round % 2 == 0 ? ClipFilter.Text : ClipFilter.All;
        }

        await Task.Delay(500);

        Assert.Single(reported);
        Assert.True(repository.Queries >= 1, "刷新根本没有发生");
    });

    [Fact]
    public void ASuccessResetsTheReportSoALaterFailureIsReportedAgain() => RunOnUiThread(async () =>
    {
        using var workspace = new Workspace();
        var repository = new FaultingRepository();
        var extensions = new ExtensionService(Path.Combine(workspace.Root, "extensions"));

        var reported = new List<Exception>();
        var vm = NewViewModel(repository, extensions, reported.Add);

        vm.RefreshInBackground();
        for (var i = 0; i < 50 && reported.Count == 0; i++)
        {
            await Task.Delay(20);
        }
        Assert.Single(reported);

        // The user restarts / the file comes back: one good refresh re-arms the reporter, so the
        // next genuine failure is not swallowed as "already told them once".
        repository.ShouldFail = false;
        await vm.RefreshAsync();
        Assert.Empty(vm.Items);

        repository.ShouldFail = true;
        vm.RefreshInBackground();
        for (var i = 0; i < 50 && reported.Count < 2; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(2, reported.Count);
    });

    [Fact]
    public void ASupersededSearchIsNotReportedAsAFailure() => RunOnUiThread(async () =>
    {
        using var workspace = new Workspace();
        var repository = new FaultingRepository();
        var extensions = new ExtensionService(Path.Combine(workspace.Root, "extensions"));

        var reported = new List<Exception>();
        var vm = NewViewModel(repository, extensions, reported.Add);

        // Typing fast cancels the pending delay; a cancellation is normal, not an error worth a
        // dialog. Only the surviving refresh may report.
        for (var length = 1; length <= 5; length++)
        {
            vm.SearchText = "search"[..length];
            await Task.Delay(10);
        }

        await Task.Delay(500);

        Assert.Single(reported);
    });

    // ---- T-28: search storm ----------------------------------------------

    [Fact]
    public void TypingTheSameQueryTenTimesSettlesOnOneCorrectResult() => RunOnUiThread(async () =>
    {
        using var workspace = new Workspace();
        var repository = await NewRepositoryAsync(workspace);

        for (var i = 0; i < 500; i++)
        {
            await repository.AddOrTouchAsync(new ClipItem
            {
                Kind = ClipKind.Text,
                Text = $"第{i}条 剪贴板历史",
                SourceApp = "stress.exe",
            });
        }

        var extensions = new ExtensionService(Path.Combine(workspace.Root, "extensions"));
        var vm = NewViewModel(repository, extensions);

        // Every keystroke schedules a debounced refresh; each one cancels the previous timer.
        for (var round = 0; round < 10; round++)
        {
            foreach (var length in Enumerable.Range(1, "剪贴板历史".Length))
            {
                vm.SearchText = "剪贴板历史"[..length];
            }
        }

        // Let the debounce fire, then confirm the view matches the database.
        await Task.Delay(400);
        await vm.RefreshAsync();

        Assert.Equal(500, vm.Items.Count);
        Assert.False(vm.IsEmpty);
        Assert.Equal("500 项", vm.CountText);
    });

    [Fact]
    public void CancellingDuringARefreshDoesNotLeaveAStaleView() => RunOnUiThread(async () =>
    {
        using var workspace = new Workspace();
        var repository = await NewRepositoryAsync(workspace);
        for (var i = 0; i < 200; i++)
        {
            await repository.AddOrTouchAsync(new ClipItem { Kind = ClipKind.Text, Text = $"item-{i}" });
        }

        var extensions = new ExtensionService(Path.Combine(workspace.Root, "extensions"));
        var vm = NewViewModel(repository, extensions);

        // A refresh superseded by a newer one must not apply its (older) result set.
        await Task.WhenAll(vm.RefreshAsync(), vm.RefreshAsync());

        Assert.Equal(200, vm.Items.Count);
        Assert.Equal(vm.Items.Count, vm.Items.Select(x => x.Id).Distinct().Count());
    });

    // ---- refresh diff keeps row identity ---------------------------------

    [Fact]
    public void RefreshReusesRowsInsteadOfRebuildingThem() => RunOnUiThread(async () =>
    {
        using var workspace = new Workspace();
        var repository = await NewRepositoryAsync(workspace);
        await repository.AddOrTouchAsync(new ClipItem { Kind = ClipKind.Text, Text = "one" });
        await repository.AddOrTouchAsync(new ClipItem { Kind = ClipKind.Text, Text = "two" });

        var extensions = new ExtensionService(Path.Combine(workspace.Root, "extensions"));
        var vm = NewViewModel(repository, extensions);

        await vm.RefreshAsync();
        var before = vm.Items.ToDictionary(x => x.Id);

        await vm.RefreshAsync();

        // The panel refreshes on every capture; rebuilding would discard decoded thumbnails
        // and scroll position, so surviving rows must be the same instances.
        Assert.Equal(before.Count, vm.Items.Count);
        foreach (var item in vm.Items)
        {
            Assert.Same(before[item.Id], item);
        }
    });

    [Fact]
    public void ImagePreviewModeReachesReusedAndNewRows() => RunOnUiThread(async () =>
    {
        using var image = new PreviewImageFile();
        var firstModel = new ClipItem
        {
            Id = "preview-first",
            Kind = ClipKind.Image,
            ImagePath = image.FilePath,
            ImageWidth = 320,
            ImageHeight = 200,
        };
        var secondModel = new ClipItem
        {
            Id = "preview-second",
            Kind = ClipKind.Image,
            ImagePath = image.FilePath,
            ImageWidth = 640,
            ImageHeight = 480,
        };
        var repository = new ImagePreviewRepository { Results = [firstModel] };
        var vm = NewViewModel(repository, new ExtensionService(image.ExtensionDirectory));

        await vm.RefreshAsync();
        var first = Assert.Single(vm.Items);
        Assert.True(first.ShowPreview);
        Assert.False(first.ShowTextRow);

        // Settings changes apply immediately to the row that is already on screen.
        vm.SetImagePreviewMode("Small");
        Assert.False(first.ShowPreview);
        Assert.True(first.ShowTextRow);
        Assert.True(first.ShowIconImage);

        // A later refresh keeps the existing row and gives the current mode to a newly created
        // row; neither path is allowed to fall back to the default Large layout.
        repository.Results = [firstModel, secondModel];
        await vm.RefreshAsync();

        var reused = Assert.Single(vm.Items, item => item.Id == firstModel.Id);
        var added = Assert.Single(vm.Items, item => item.Id == secondModel.Id);
        Assert.Same(first, reused);
        Assert.True(reused.ShowTextRow);
        Assert.True(reused.ShowIconImage);
        Assert.True(added.ShowTextRow);
        Assert.True(added.ShowIconImage);

        vm.SetImagePreviewMode("Medium");
        Assert.All(vm.Items, item =>
        {
            Assert.True(item.ShowPreview);
            Assert.False(item.ShowTextRow);
            Assert.False(item.ShowIconImage);
        });
    });
}
