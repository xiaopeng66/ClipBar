using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Windows.Data;
using ClipBar.App.Services;
using ClipBar.Core.Clipboard;
using ClipBar.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClipBar.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IClipRepository _repository;
    private readonly Func<AppSettings> _settings;
    private readonly Action<ClipItem> _pasteRequested;
    private readonly Action _settingsRequested;
    private readonly Action<Exception> _refreshFailed;
    private readonly Action<Exception> _mutationFailed;
    private readonly Action<string> _log;
    private readonly IImageTrashOperations _imageTrash;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private CancellationTokenSource? _searchCts;
    private int _refreshVersion;
    private int _refreshFailureReported;

    public MainViewModel(
        IClipRepository repository,
        Func<AppSettings> settings,
        Action<ClipItem> pasteRequested,
        Action settingsRequested,
        Action<Exception>? refreshFailed = null,
        Action<Exception>? mutationFailed = null,
        IImageTrashOperations? imageTrash = null,
        Action<string>? log = null)
    {
        _repository = repository;
        _settings = settings;
        _pasteRequested = pasteRequested;
        _settingsRequested = settingsRequested;
        _refreshFailed = refreshFailed ?? (_ => { });
        _mutationFailed = mutationFailed ?? (_ => { });
        _imageTrash = imageTrash ?? ImageTrashOperations.Instance;
        _log = log ?? AppLog.Write;
        _hotkeyText = settings().Hotkey;

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ClipItemViewModel.GroupLabel)));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ClipItemViewModel.IsPinned), ListSortDirection.Descending));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ClipItemViewModel.UpdatedAt), ListSortDirection.Descending));
    }

    public ObservableCollection<ClipItemViewModel> Items { get; } = [];

    /// <summary>
    /// Image preview size from 设置. Pushed into every row so its layout is decided in the view
    /// model (plain boolean bindings) instead of by a MultiBinding that has to reach the window
    /// from inside the item template.
    /// </summary>
    private string _imagePreviewMode = "Large";

    /// <summary>Applies a new preview size to the existing rows; new rows pick it up as they appear.
    /// The value goes through the same canonical mapping as settings, so a raw legacy value
    /// ("Thumbnail") cannot split the window, the rows and the converter onto two mappings (G-24).</summary>
    public void SetImagePreviewMode(string mode)
    {
        _imagePreviewMode = ClipBar.Core.Models.AppSettings.CanonicalImagePreview(mode);

        foreach (var item in Items)
        {
            item.SetImagePreviewMode(_imagePreviewMode);
        }
    }

    public ObservableCollection<FilterOption> Filters { get; } =
    [
        new("全部", ClipFilter.All),
        new("文本", ClipFilter.Text),
        new("图片", ClipFilter.Image),
        new("文件", ClipFilter.Files),
    ];

    /// <summary>Source-app dropdown, rebuilt from the current result set.</summary>
    public ObservableCollection<string> SourceOptions { get; } = ["全部来源"];

    [ObservableProperty]
    private string _selectedSource = "全部来源";

    private bool _updatingSources;

    /// <summary>True while a specific source is selected (drives the filter button's accent dot).</summary>
    public bool IsSourceFiltered => !string.Equals(SelectedSource, "全部来源", StringComparison.Ordinal);

    partial void OnSelectedSourceChanged(string value)
    {
        OnPropertyChanged(nameof(IsSourceFiltered));
        if (!_updatingSources)
        {
            FireAndForget(RefreshAsync());
        }
    }

    /// <summary>Source-app display name, matching the subtitle's capitalisation.</summary>
    public static string SourceDisplayName(ClipItem item)
    {
        var name = string.IsNullOrWhiteSpace(item.SourceApp)
            ? "未知来源"
            : Path.GetFileNameWithoutExtension(item.SourceApp);
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }

    public static bool MatchesSourceFilter(ClipItem item, string? selected)
        => string.IsNullOrWhiteSpace(selected)
            || selected == "全部来源"
            || string.Equals(SourceDisplayName(item), selected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Rebuilds the dropdown from the unfiltered result set, keeping the selection.</summary>
    private void UpdateSourceOptions(IReadOnlyList<ClipItem> results)
    {
        var names = results
            .Select(SourceDisplayName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        names.Insert(0, "全部来源");
        if (SourceOptions.SequenceEqual(names, StringComparer.Ordinal))
        {
            return;
        }

        _updatingSources = true;
        try
        {
            // Clear() pushes SelectedItem=null through the two-way binding, so the previous value
            // must be captured first and restored afterwards — otherwise the combo box renders
            // blank on the very first refresh.
            var previous = SelectedSource;

            SourceOptions.Clear();
            foreach (var name in names)
            {
                SourceOptions.Add(name);
            }

            // Keep the current selection even when the filtered result set no longer contains it
            // (e.g. switching the type filter): silently resetting looked like the filter broke.
            if (!string.IsNullOrWhiteSpace(previous) && !SourceOptions.Contains(previous))
            {
                SourceOptions.Add(previous);
            }

            SelectedSource = string.IsNullOrWhiteSpace(previous) ? "全部来源" : previous;
        }
        finally
        {
            _updatingSources = false;
        }
    }

    public ICollectionView ItemsView { get; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ClipFilter _selectedFilter = ClipFilter.All;

    [ObservableProperty]
    private ClipItemViewModel? _selectedItem;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>Inline "已删除 · 撤销" bar inside the panel (replaces the separate toast window).</summary>
    [ObservableProperty]
    private bool _undoVisible;

    [ObservableProperty]
    private string _undoMessage = string.Empty;

    private sealed record PendingUndo(ClipItem Snapshot, string? TrashPath, bool ImageAtOriginalPath);

    private PendingUndo? _pendingUndo;
    private CancellationTokenSource? _undoCts;

    [ObservableProperty]
    private string _countText = "0 项";

    [ObservableProperty]
    private string _hotkeyText;

    partial void OnSearchTextChanged(string value)
    {
        FireAndForget(RefreshDebouncedAsync());
    }

    partial void OnSelectedFilterChanged(ClipFilter value)
    {
        FireAndForget(RefreshAsync());
    }

    /// <summary>
    /// Applies a filter chip. The chips are ordinary buttons (same WPF-UI template as the
    /// settings window), so the selection is a command instead of a ListBox selection.
    /// </summary>
    [RelayCommand]
    private void SelectFilter(FilterOption? option)
    {
        if (option is not null)
        {
            SelectedFilter = option.Value;
        }
    }

    /// <summary>
    /// Starts a refresh nobody awaits, and makes sure its failure is reported instead of
    /// disappearing.
    /// <para>
    /// Measured (not assumed): .NET does <b>not</b> terminate the process on an unobserved task
    /// exception — a probe with three faulted fire-and-forget tasks produced two
    /// <c>UnobservedTaskException</c> events and the process stayed alive through every GC round.
    /// So the danger here is not a crash but silence: <c>QueryAsync</c> does throw
    /// (<c>SqliteException: no such table: clips</c> when the history file is lost under a running
    /// instance), and without this wrapper the panel would keep showing the old list with no
    /// indication that the history became unreadable.
    /// </para>
    /// Reports at most once per failure streak so a persistently broken database does not write a
    /// log line and raise a dialog on every keystroke; the flag resets on the next success.
    /// </summary>
    private void FireAndForget(Task work)
    {
        _ = ObserveAsync(work);
    }

    /// <summary>
    /// Refreshes without blocking the caller, reporting (not swallowing) a failure. The panel
    /// calls this on every show and after every captured clip, so it must never take the UI down
    /// or fail silently.
    /// </summary>
    public void RefreshInBackground() => FireAndForget(RefreshAsync());

    private Task? _initialLoad;

    /// <summary>
    /// Single-flight first load: startup, the hotkey, the tray and <c>--show</c> all await the
    /// same task, so the panel's first visible frame already carries the history instead of an
    /// empty shell that fills in a beat later. A failure still completes the gate — an
    /// unreadable database must not keep the panel hidden forever — and goes through the same
    /// once-per-streak reporter as <see cref="RefreshInBackground"/>.
    /// </summary>
    public Task EnsureInitialLoadedAsync()
    {
        if (_initialLoad is { } load)
        {
            return load;
        }

        var completion = InitialLoadAsync();
        _initialLoad = completion;
        return completion;
    }

    private async Task InitialLoadAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Same contract as ObserveAsync: report once per failure streak, never crash.
            // Startup reports while the panel is still hidden, so the report is log-only —
            // re-arm the streak, or the first failure the user could actually SEE (right
            // after they open the panel) stays suppressed too and the list looks empty with
            // no explanation (R13-F-01).
            _refreshFailed(ex);
            Volatile.Write(ref _refreshFailureReported, 0);
        }
        finally
        {
            // The gate stays open permanently; IsEmpty/CountText carry the resulting state.
        }
    }

    private async Task ObserveAsync(Task work)
    {
        try
        {
            await work.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A superseded search cancels its own delay; that is not a failure. It must also not
            // count as a success: RefreshDebouncedAsync swallows its own cancellation, so re-arming
            // the reporter here would let the next keystroke report a second time for one outage.
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _refreshFailureReported, 1) == 0)
            {
                _refreshFailed(ex);
            }
        }
    }

    /// <summary>True when the built-in 用 Bing 搜索 action is enabled in settings.</summary>
    public bool ShowBingSearch => _settings().ShowBingSearch;

    public void UpdateHotkey(string hotkey)
    {
        HotkeyText = hotkey;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var version = Interlocked.Increment(ref _refreshVersion);
        // The limit is the non-pinned budget from settings; the repository adds pinned rows on
        // top. The old hardcoded 500 silently hid everything past it once the user raised
        // MaxItems above 500 (P-0, docs/IMPROVEMENT-PROPOSALS.md).
        var results = await _repository
            .QueryAsync(SelectedFilter, SearchText, _settings().MaxItems, cancellationToken)
            .ConfigureAwait(true);

        // The history was readable: re-arm the failure reporter so the next outage is reported
        // again. This lives here rather than in the observer because callers that await
        // RefreshAsync directly (pin, delete, clear) bypass the observer entirely.
        Volatile.Write(ref _refreshFailureReported, 0);

        if (version != _refreshVersion || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var selectedId = SelectedItem?.Id;

        // Diff against what is already on screen instead of Clear() + rebuild. The panel
        // refreshes on every captured clip and after every search keystroke; rebuilding threw
        // away 500 view models each time, which also discarded every decoded thumbnail and
        // reset the list's scroll position. Rows whose Id survives are updated in place.
        var existing = new Dictionary<string, ClipItemViewModel>(Items.Count, StringComparer.Ordinal);
        foreach (var item in Items)
        {
            existing[item.Id] = item;
        }

        UpdateSourceOptions(results);

        var desired = new List<ClipItemViewModel>(results.Count);
        var needsReorder = false;
        foreach (var model in results)
        {
            // The source filter runs over the already-queried rows; the type filter stays in SQL.
            if (!MatchesSourceFilter(model, SelectedSource))
            {
                continue;
            }

            if (existing.Remove(model.Id, out var reused))
            {
                needsReorder |= reused.Update(model);
                desired.Add(reused);
            }
            else
            {
                var created = new ClipItemViewModel(model);
                created.SetImagePreviewMode(_imagePreviewMode);
                desired.Add(created);
            }
        }

        // Whatever is still in `existing` fell out of the result set: deleted, evicted by the
        // item cap, or filtered out by the current search.
        if (existing.Count > 0)
        {
            foreach (var stale in existing.Values)
            {
                Items.Remove(stale);
            }
        }

        // Align the collection with the query order using moves only, so the panel keeps the
        // rows it has and the collection view stays stable.
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < Items.Count && ReferenceEquals(Items[index], desired[index]))
            {
                continue;
            }

            var current = Items.IndexOf(desired[index]);
            if (current >= 0)
            {
                Items.Move(current, index);
            }
            else
            {
                Items.Insert(index, desired[index]);
            }
        }

        // The collection view sorts and groups on IsPinned/UpdatedAt/GroupLabel; when one of
        // those changed in place it has to re-evaluate. Additions and removals it notices itself.
        if (needsReorder)
        {
            ItemsView.Refresh();
        }

        IsEmpty = Items.Count == 0;
        CountText = $"{Items.Count} 项";
        HotkeyText = _settings().Hotkey;

        // Only restore a selection that already existed. The old "?? Items.FirstOrDefault()"
        // fallback re-selected the newest row on every refresh (every capture, every show),
        // which painted a highlight the user never asked for and read as a stuck hover state.
        // Keyboard use is unaffected: Down selects the first row on demand and Enter with no
        // selection is a no-op.
        SelectedItem = selectedId is null ? null : Items.FirstOrDefault(x => x.Id == selectedId);
    }

    private async Task RefreshDebouncedAsync()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        try
        {
            await Task.Delay(120, token).ConfigureAwait(true);
            await RefreshAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private void PasteSelected()
    {
        if (SelectedItem is not null)
        {
            _pasteRequested(SelectedItem.Model);
        }
    }

    [RelayCommand]
    private async Task TogglePinAsync(ClipItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        await _repository.SetPinnedAsync(item.Id, !item.IsPinned).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteAsync(ClipItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        await _mutationGate.WaitAsync().ConfigureAwait(true);
        await ImageReferenceMutationGate.Semaphore.WaitAsync().ConfigureAwait(true);
        try
        {
            ClipItem snapshot;
            string? image;
            try
            {
                var current = await _repository.GetAsync(item.Id).ConfigureAwait(true);
                if (current is null)
                {
                    await RefreshAfterMutationAsync().ConfigureAwait(true);
                    return;
                }

                snapshot = current;
                image = await _repository.DeleteAsync(item.Id).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                RenewPendingUndoExpiry();
                ReportMutationFailure(ex);
                return;
            }

            // Replacing the current undo is safe only after this delete has committed. If it
            // failed, the previous row and its image are still the only recoverable operation.
            FinalizePendingUndo();

            string? trash = null;
            try
            {
                trash = _imageTrash.MoveToTrash(image);
            }
            catch (Exception ex)
            {
                ReportMutationFailure(ex);
            }

            var imageAtOriginalPath = snapshot.Kind != ClipKind.Image
                || (!string.IsNullOrWhiteSpace(snapshot.ImagePath) && File.Exists(snapshot.ImagePath));
            _pendingUndo = new PendingUndo(snapshot, trash, imageAtOriginalPath);
            UndoMessage = $"已删除「{item.Title}」";
            UndoVisible = true;
            StartUndoExpiry();

            await RefreshAfterMutationAsync().ConfigureAwait(true);
        }
        finally
        {
            ImageReferenceMutationGate.Semaphore.Release();
            _mutationGate.Release();
        }
    }

    /// <summary>Restores the last deleted row without leaving the panel.</summary>
    [RelayCommand]
    private async Task UndoDeleteAsync()
    {
        await _mutationGate.WaitAsync().ConfigureAwait(true);
        await ImageReferenceMutationGate.Semaphore.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_pendingUndo is not { } pending)
            {
                UndoVisible = false;
                return;
            }

            var restoredFromTrash = false;
            if (pending.TrashPath is { } trashPath)
            {
                // images/trash/x.png -> images/ (derived, so an isolated test workspace restores
                // into its own folder instead of the live data directory).
                var imagesDirectory = Path.GetDirectoryName(Path.GetDirectoryName(trashPath));
                bool restored;
                try
                {
                    restored = imagesDirectory is not null
                        && _imageTrash.RestoreFromTrash(trashPath, imagesDirectory);
                }
                catch (Exception ex)
                {
                    RenewPendingUndoExpiry();
                    ReportMutationFailure(ex);
                    return;
                }

                if (!restored)
                {
                    RenewPendingUndoExpiry();
                    ReportMutationFailure(new IOException("The deleted image could not be restored from trash."));
                    return;
                }
                else
                {
                    restoredFromTrash = true;
                    pending = pending with { TrashPath = null, ImageAtOriginalPath = true };
                    _pendingUndo = pending;
                }
            }

            if (pending.Snapshot.Kind == ClipKind.Image
                && (string.IsNullOrWhiteSpace(pending.Snapshot.ImagePath)
                    || !pending.ImageAtOriginalPath
                    || !File.Exists(pending.Snapshot.ImagePath)))
            {
                RenewPendingUndoExpiry();
                ReportMutationFailure(new IOException("The image file is unavailable; the history row was not restored."));
                return;
            }

            try
            {
                await _repository.AddOrTouchAsync(pending.Snapshot).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Exception? compensationError = null;
                if (restoredFromTrash && pending.Snapshot.ImagePath is { } imagePath)
                {
                    string? trashAgain = null;
                    try
                    {
                        trashAgain = _imageTrash.MoveToTrash(imagePath);
                    }
                    catch (Exception moveBackError)
                    {
                        compensationError = moveBackError;
                    }

                    // A failed compensation must not leave stale metadata that tells the retry
                    // path to restore a file which is already beside images/.
                    pending = pending with
                    {
                        TrashPath = trashAgain,
                        ImageAtOriginalPath = trashAgain is null && File.Exists(imagePath),
                    };
                    _pendingUndo = pending;
                }

                RenewPendingUndoExpiry();
                ReportMutationFailure(compensationError is null
                    ? ex
                    : new AggregateException("Undo failed and its image could not be moved back to trash.", ex, compensationError));
                return;
            }

            FinalizePendingUndo();
            _log("Undo delete: " + pending.Snapshot.Id);
            await RefreshAfterMutationAsync().ConfigureAwait(true);
        }
        finally
        {
            ImageReferenceMutationGate.Semaphore.Release();
            _mutationGate.Release();
        }
    }

    private void StartUndoExpiry()
    {
        _undoCts?.Cancel();
        _undoCts?.Dispose();
        _undoCts = new CancellationTokenSource();
        FireAndForget(ExpireUndoAsync(_undoCts.Token));
    }

    private void RenewPendingUndoExpiry()
    {
        if (_pendingUndo is not null)
        {
            StartUndoExpiry();
        }
    }

    private async Task ExpireUndoAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), token).ConfigureAwait(true);
            await _mutationGate.WaitAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_undoCts is null || _undoCts.Token != token)
            {
                return;
            }

            FinalizePendingUndo();
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>Drops the pending undo and its trashed file after a successful replacement operation.</summary>
    private void FinalizePendingUndo()
    {
        _undoCts?.Cancel();
        _undoCts?.Dispose();
        _undoCts = null;
        var pending = _pendingUndo;
        _pendingUndo = null;
        UndoVisible = false;

        if (pending?.TrashPath is { } trashPath)
        {
            try
            {
                _imageTrash.DeleteTrashEntry(trashPath);
            }
            catch (Exception ex)
            {
                ReportMutationFailure(ex);
            }
        }
    }

    private async Task RefreshAfterMutationAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _refreshFailed(ex);
        }
    }

    private void ReportMutationFailure(Exception exception)
    {
        try
        {
            _mutationFailed(exception);
        }
        catch (Exception reportError)
        {
            try
            {
                _log($"ERROR reporting history mutation failure: {reportError}");
            }
            catch
            {
            }
        }
    }

    /// <summary>Clears all history, including pinned rows, and invalidates pending undo on success.</summary>
    public Task ClearAllAsync() => ClearHistoryAsync(includePinned: true);

    [RelayCommand]
    private async Task ClearAsync()
    {
        try
        {
            await ClearHistoryAsync(includePinned: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportMutationFailure(ex);
        }
    }

    private async Task ClearHistoryAsync(bool includePinned)
    {
        await _mutationGate.WaitAsync().ConfigureAwait(true);
        await ImageReferenceMutationGate.Semaphore.WaitAsync().ConfigureAwait(true);
        try
        {
            IReadOnlyList<string> images;
            try
            {
                images = await _repository.ClearAsync(includePinned).ConfigureAwait(true);
            }
            catch
            {
                RenewPendingUndoExpiry();
                throw;
            }

            // Keep the prior undo available unless the clear actually committed.
            FinalizePendingUndo();
            foreach (var image in images)
            {
                ImageStore.TryDelete(image);
            }

            try
            {
                await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _refreshFailed(ex);
            }
        }
        finally
        {
            ImageReferenceMutationGate.Semaphore.Release();
            _mutationGate.Release();
        }
    }

    [RelayCommand]
    private void OpenSettings() => _settingsRequested();
}

public sealed record FilterOption(string Label, ClipFilter Value);
