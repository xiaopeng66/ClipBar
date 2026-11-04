using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using ClipBar.App.Services;
using ClipBar.Core.Clipboard;
using ClipBar.Core.Extensions;
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
    private readonly ExtensionService _extensions;
    private readonly ExtensionActionRunner _runner;
    private readonly Func<SnipDoAction, IReadOnlyDictionary<string, string>?> _optionPrompt;
    private readonly Action<string> _reportError;
    private readonly Func<string?> _targetProcessName;
    private readonly Action<Exception> _refreshFailed;
    private CancellationTokenSource? _searchCts;
    private int _refreshVersion;
    private int _extensionRunning;
    private int _refreshFailureReported;

    public MainViewModel(
        IClipRepository repository,
        Func<AppSettings> settings,
        Action<ClipItem> pasteRequested,
        Action settingsRequested,
        ExtensionService extensions,
        ExtensionActionRunner runner,
        Func<SnipDoAction, IReadOnlyDictionary<string, string>?> optionPrompt,
        Action<string> reportError,
        Func<string?> targetProcessName,
        Action<Exception>? refreshFailed = null)
    {
        _repository = repository;
        _settings = settings;
        _pasteRequested = pasteRequested;
        _settingsRequested = settingsRequested;
        _extensions = extensions;
        _runner = runner;
        _optionPrompt = optionPrompt;
        _reportError = reportError;
        _targetProcessName = targetProcessName;
        _refreshFailed = refreshFailed ?? (_ => { });
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

    public ICollectionView ItemsView { get; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ClipFilter _selectedFilter = ClipFilter.All;

    [ObservableProperty]
    private ClipItemViewModel? _selectedItem;

    [ObservableProperty]
    private bool _isEmpty;

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

    /// <summary>
    /// The SnipDo actions that apply to one specific clip — not the mutable selection. The
    /// right-click menu is built from this, so what it offers is decided by the row the user
    /// actually right-clicked, and a later selection change cannot change it.
    /// </summary>
    public IReadOnlyList<ExtensionActionViewModel> GetApplicableActions(ClipItemViewModel? item)
    {
        var model = item?.Model;
        if (model is null || model.Kind == ClipKind.Image)
        {
            return [];
        }

        _extensions.EnsureLoaded();

        // Actions target the app the clip came from (SnipDo's noShowApps/showOnlyApps semantics).
        var processName = model.SourceApp;
        if (string.IsNullOrWhiteSpace(processName))
        {
            processName = _targetProcessName();
        }

        return CollectApplicableActions(_extensions.Extensions, model, processName);
    }

    /// <summary>Pure matching, so the per-clip filtering is testable without a loaded ExtensionService.</summary>
    public static IReadOnlyList<ExtensionActionViewModel> CollectApplicableActions(
        IReadOnlyList<LoadedExtension> extensions,
        ClipItem model,
        string? processName)
    {
        var context = new ActionContext(model.Text ?? string.Empty, model.FilePaths, processName);
        var actions = new List<ExtensionActionViewModel>();
        foreach (var loaded in extensions)
        {
            foreach (var action in loaded.Extension.Actions)
            {
                if (SnipDoActionMatcher.IsVisible(action, context))
                {
                    actions.Add(new ExtensionActionViewModel(loaded, action));
                }
            }
        }

        return actions;
    }

    /// <summary>Runs an extension action against the clip it was opened for; option-based actions ask the user first.</summary>
    [RelayCommand]
    private async Task RunExtensionAsync(ExtensionActionRequest? request)
    {
        if (request?.Item is null || request.Action is null)
        {
            return;
        }

        // Extensions are third-party code and some run for seconds. A double-click used to
        // launch the same action twice, each spawning its own process, so hold a single slot.
        if (Interlocked.Exchange(ref _extensionRunning, 1) == 1)
        {
            // A different action clicked while one is running: dropped silently used to look
            // like "the menu did nothing" (G-19). One log line is enough — no modal nagging.
            AppLog.Write($"Extension action '{request.Action.Title}' ignored: another action is still running");
            return;
        }

        try
        {
            IReadOnlyDictionary<string, string>? appOptions = null;
            if (request.Action.Action.Options.Count > 0)
            {
                try
                {
                    appOptions = _optionPrompt(request.Action.Action);
                }
                catch (Exception ex)
                {
                    // The command is fire-and-forget from the menu click; without this catch a
                    // prompt failure is an unobserved task and the click silently does nothing
                    // (G-22, same class as F-41).
                    _reportError($"{request.Action.Title} 的参数窗口打开失败：{ex.Message}");
                    return;
                }

                if (appOptions is null)
                {
                    return;
                }
            }

            // The clip is captured before the prompt: the request carries the row that was
            // right-clicked, and neither a prompt nor a refresh may redirect the action.
            var item = request.Item.Model;
            var result = await _runner
                .RunAsync(request.Action.Action, item.Text ?? string.Empty, item.FilePaths, appOptions)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                _reportError($"{request.Action.Title} 执行失败：{result.Error ?? "未知错误"}");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _extensionRunning, 0);
        }
    }

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

        var desired = new List<ClipItemViewModel>(results.Count);
        var needsReorder = false;
        foreach (var model in results)
        {
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

        SelectedItem = Items.FirstOrDefault(x => x.Id == selectedId) ?? Items.FirstOrDefault();
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

        var image = await _repository.DeleteAsync(item.Id).ConfigureAwait(true);
        ImageStore.TryDelete(image);
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        var images = await _repository.ClearAsync(includePinned: false).ConfigureAwait(true);
        foreach (var image in images)
        {
            ImageStore.TryDelete(image);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenSettings() => _settingsRequested();
}

public sealed record FilterOption(string Label, ClipFilter Value);

/// <summary>
/// An extension action bound to the exact clip its menu was opened for. Executing from this
/// snapshot — instead of the live <see cref="MainViewModel.SelectedItem"/> — means a selection
/// change or a history refresh between the right-click and the click can never redirect the
/// action to a different clip.
/// </summary>
public sealed record ExtensionActionRequest(ClipItemViewModel Item, ExtensionActionViewModel Action);
