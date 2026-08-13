using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.Utilities;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.ViewModels.CivitaiBrowser;

/// <summary>
/// Civitai browser sub-tab. Search, filters, multi-select, cursor pagination, and
/// a persistent concurrent download queue with SHA256 verification + sidecar writing.
/// </summary>
public partial class CivitaiBrowserViewModel : ObservableObject
{
    private readonly ICivitaiClient? _civitaiClient;
    private readonly IAppSettingsService? _settingsService;
    private readonly IUnifiedLogger? _logger;
    private readonly CivitaiDownloadQueue _queue;
    private readonly CivitaiWaitlist _waitlist;
    private Avalonia.Threading.DispatcherTimer? _waitlistCountdownTimer;

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _debounceCts;
    private string? _nextCursor;
    private bool _isLoading;
    private bool _initialized;
    private CivitaiInstalledIndex _installed = CivitaiInstalledIndex.Empty;
    private CivitaiResultViewModel? _lastClickedItem;

    public CivitaiBrowserViewModel()
        : this(null, null, null, new CivitaiDownloadQueue(null), new CivitaiWaitlist(null, null), null)
    {
        // Design-time only
        Results.Add(CivitaiResultViewModel.CreateDesignSample());
    }

    public CivitaiBrowserViewModel(
        ICivitaiClient? civitaiClient,
        IAppSettingsService? settingsService,
        IUnifiedLogger? logger,
        CivitaiDownloadQueue queue,
        CivitaiWaitlist waitlist,
        ObservableCollection<BaseModelFilterItem>? sharedBaseModelSource)
    {
        _civitaiClient = civitaiClient;
        _settingsService = settingsService;
        _logger = logger;
        _queue = queue;
        _waitlist = waitlist;
        StartWaitlistCountdownTimer();

        SortOptions = new ObservableCollection<string>
        {
            CivitaiModelSort.HighestRated,
            CivitaiModelSort.MostDownloaded,
            CivitaiModelSort.Newest
        };
        SelectedSort = CivitaiModelSort.Newest;

        PeriodOptions = new ObservableCollection<CivitaiPeriod>(Enum.GetValues<CivitaiPeriod>());
        SelectedPeriod = CivitaiPeriod.AllTime;

        // Each option carries its own display label AND the concrete types-array
        // sent to Civitai. No string-switch in BuildQuery — change the label and the
        // wire format stays correct because both live on the same record.
        ModelTypeOptions = new ObservableCollection<ModelTypeOption>
        {
            new("LORA",            [CivitaiModelType.LORA]),
            new("LoCon",           [CivitaiModelType.LoCon]),
            new("DoRA",            [CivitaiModelType.DoRA]),
            new("All LoRA types",  [CivitaiModelType.LORA, CivitaiModelType.LoCon, CivitaiModelType.DoRA]),
            new("All models",      null) // null = no types filter (Checkpoints, embeddings, etc. too)
        };
        SelectedModelType = ModelTypeOptions.Single(o => o.Label == "All LoRA types");

        // Base model filter mirrors the Installed-tab list (same names) but holds its
        // own selection state — toggling here doesn't disturb the installed filter.
        AvailableBaseModels = new ObservableCollection<BaseModelFilterItem>();
        if (sharedBaseModelSource is not null)
        {
            _baseModelSource = sharedBaseModelSource;
            RebuildBaseModelMirror();
            _baseModelSource.CollectionChanged += OnBaseModelSourceChanged;
        }

        // Enable search-on-filter-change now that the initial property cascade is done,
        // then kick off exactly one initial search.
        _initialized = true;
        _ = RefreshInstalledSetAsync();
        _ = SearchAsync();
    }

    private readonly ObservableCollection<BaseModelFilterItem>? _baseModelSource;

    #region Filter Bar State

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string? _selectedSort;

    [ObservableProperty]
    private CivitaiPeriod _selectedPeriod;

    [ObservableProperty]
    private ModelTypeOption? _selectedModelType;

    [ObservableProperty]
    private bool _hideInstalledModels;

    [ObservableProperty]
    private bool _hideEarlyAccessModels;

    [ObservableProperty]
    private bool _showNsfwContent = true;

    [ObservableProperty]
    private double _cardWidth = 240;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isQueuePanelOpen = true;

    public bool IsBusy
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool HasMore => !string.IsNullOrEmpty(_nextCursor);

    public ObservableCollection<string> SortOptions { get; }
    public ObservableCollection<CivitaiPeriod> PeriodOptions { get; }
    public ObservableCollection<ModelTypeOption> ModelTypeOptions { get; }

    /// <summary>
    /// Base-model filter items mirrored from the Installed tab's <c>AvailableBaseModels</c>.
    /// Selection state is independent of the Installed tab so toggling here doesn't
    /// disturb the installed-grid filter.
    /// </summary>
    public ObservableCollection<BaseModelFilterItem> AvailableBaseModels { get; }

    /// <summary>
    /// Search text typed inside the base-model flyout. Narrows the visible option list
    /// (<see cref="FlyoutBaseModels"/>) only — selections are untouched.
    /// </summary>
    [ObservableProperty]
    private string? _baseModelFilterSearchText;

    /// <summary>
    /// The option list the flyout renders: the mirror items narrowed by
    /// <see cref="BaseModelFilterSearchText"/>, with selected items pinned visible so an
    /// active filter can always be untoggled. Holds the SAME item instances as
    /// <see cref="AvailableBaseModels"/>, so selection state stays single-sourced.
    /// </summary>
    public BatchObservableCollection<BaseModelFilterItem> FlyoutBaseModels { get; } = [];

    public bool IsBaseModelFilterActive => AvailableBaseModels.Any(f => f.IsSelected);
    public int ActiveBaseModelFilterCount => AvailableBaseModels.Count(f => f.IsSelected);

    partial void OnBaseModelFilterSearchTextChanged(string? value) => RebuildFlyoutBaseModels();

    /// <summary>
    /// Recomputes <see cref="FlyoutBaseModels"/> from the mirror + the flyout search.
    /// Single Reset notification — the flyout's ItemsControl is not virtualized.
    /// </summary>
    private void RebuildFlyoutBaseModels()
    {
        var search = BaseModelFilterSearchText?.Trim();
        var hasSearch = !string.IsNullOrWhiteSpace(search);

        var items = new List<BaseModelFilterItem>();
        foreach (var item in AvailableBaseModels)
        {
            if (!item.IsSelected
                && hasSearch
                && !item.BaseModelRaw.Contains(search!, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            items.Add(item);
        }

        FlyoutBaseModels.ReplaceAll(items);
    }

    #endregion

    #region Results + Queue

    public ObservableCollection<CivitaiResultViewModel> Results { get; } = [];

    public CivitaiDownloadQueue Queue => _queue;

    public CivitaiWaitlist Waitlist => _waitlist;

    public int SelectedCount => Results.Count(r => r.IsSelected);

    public bool HasSelection => SelectedCount > 0;

    public int VisibleCount => Results.Count(r => !r.IsHidden);

    public int HiddenByFilters => Results.Count(r => r.IsHidden);

    public bool HasHiddenResults => HiddenByFilters > 0;

    #endregion

    #region Commands

    [RelayCommand]
    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        // Stop every in-flight preview download on the old result set before we drop
        // the VMs. Otherwise rapid searches stack 50+ zombie video downloads behind
        // the global extraction gate.
        CancelAllPreviews();

        Results.Clear();
        _lastClickedItem = null;   // anchor invalidated by clear
        OnSelectionChanged();
        _nextCursor = null;
        await LoadNextAsync(ct);
    }

    private void CancelAllPreviews()
    {
        foreach (var r in Results) r.Cancel();
    }

    /// <summary>
    /// Fetches the next batch. <see cref="LoadNextAsync"/> auto-paginates internally
    /// up to <see cref="MaxAutoPaginateIterations"/> times to collect a full batch, so
    /// one click delivers ~50 more visible items.
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (!HasMore || IsBusy) return;
        await LoadNextAsync(_searchCts?.Token ?? CancellationToken.None);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        ClearSelectionSilent();
        OnSelectionChanged();
    }

    /// <summary>
    /// Selects every currently-loaded result that isn't hidden by client-side filters.
    /// </summary>
    [RelayCommand]
    private void SelectAll()
    {
        foreach (var r in Results)
        {
            if (r.IsHidden) continue;
            r.IsSelected = true;
        }
        OnSelectionChanged();
    }

    /// <summary>
    /// Pointer-driven multi-select dispatcher invoked from the view's code-behind on
    /// every card click. Mirrors the Generation Gallery / Dataset Management pattern:
    /// Shift = range from last click, Ctrl = toggle, plain click = clear-and-select-this.
    /// </summary>
    public void SelectWithModifiers(CivitaiResultViewModel? item, bool isShiftPressed, bool isCtrlPressed)
    {
        if (item is null) return;

        if (isShiftPressed && _lastClickedItem is not null)
        {
            SelectRange(_lastClickedItem, item);
        }
        else if (isCtrlPressed)
        {
            item.IsSelected = !item.IsSelected;
        }
        else
        {
            ClearSelectionSilent();
            item.IsSelected = true;
        }

        _lastClickedItem = item;
        OnSelectionChanged();
    }

    private void SelectRange(CivitaiResultViewModel from, CivitaiResultViewModel to)
    {
        var fromIndex = Results.IndexOf(from);
        var toIndex = Results.IndexOf(to);
        if (fromIndex == -1 || toIndex == -1) return;

        var start = Math.Min(fromIndex, toIndex);
        var end = Math.Max(fromIndex, toIndex);
        for (var i = start; i <= end; i++)
        {
            // Skip cards filtered out by client-side filters so range-select doesn't
            // silently re-include them and surprise the user.
            if (Results[i].IsHidden) continue;
            Results[i].IsSelected = true;
        }
    }

    private void ClearSelectionSilent()
    {
        foreach (var r in Results) r.IsSelected = false;
    }

    [RelayCommand]
    private Task AddSelectionToQueueAsync()
    {
        // Materialize the (result, pick) pairs first so the EA-prompt path and the
        // direct-enqueue path operate on the same data.
        var pairs = new List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)>();
        foreach (var result in Results.Where(r => r.IsSelected))
        {
            var picks = result.Versions.Where(v => v.IsSelected).ToList();
            if (picks.Count == 0 && result.Versions.Count > 0)
            {
                picks.Add(result.Versions[0]);
                result.Versions[0].IsSelected = true;
            }
            foreach (var pick in picks)
            {
                pairs.Add((result, pick));
            }
        }
        return EnqueueWithPreflightPromptAsync(pairs);
    }

    [RelayCommand]
    private Task StartQueueAsync() => _queue.StartAllAsync();

    [RelayCommand]
    private void ClearCompleted() => _queue.ClearCompleted();

    /// <summary>
    /// Dialog service for confirmations. Resolved lazily from the app container so
    /// headless/design-time construction stays service-free; settable for tests.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    /// <summary>
    /// Clears the download queue. When jobs are still queued or mid-download the user
    /// must confirm first, because clearing cancels them. Without a dialog service
    /// (headless) it proceeds, matching <see cref="EnqueueWithPreflightPromptAsync"/>.
    /// </summary>
    [RelayCommand]
    private async Task ClearQueueAsync()
    {
        var active = _queue.ActiveCount;
        if (active > 0)
        {
            var dialogService = DialogService ??= App.Services?.GetService<IDialogService>();
            if (dialogService is not null)
            {
                var confirmed = await dialogService.ShowConfirmAsync(
                    "Clear download queue",
                    active == 1
                        ? "1 download is still queued or in progress.\n\nClearing the queue cancels it. Continue?"
                        : $"{active} downloads are still queued or in progress.\n\nClearing the queue cancels them. Continue?");
                if (!confirmed) return;
            }
        }
        _queue.ClearAll();
    }

    /// <summary>
    /// Stops every active download but keeps the queue intact. Hit Start to resume.
    /// </summary>
    [RelayCommand]
    private void AbortQueue() => _queue.AbortAllActive();

    [RelayCommand]
    private void RemoveJob(CivitaiDownloadJob? job)
    {
        if (job is not null) _queue.Remove(job);
    }

    [RelayCommand]
    private void CancelJob(CivitaiDownloadJob? job)
    {
        if (job is not null) _queue.CancelJob(job);
    }

    [RelayCommand]
    private Task RetryJobAsync(CivitaiDownloadJob? job)
        => job is null ? Task.CompletedTask : _queue.RetryJobAsync(job);

    /// <summary>
    /// Launches URLs in the default browser. Swappable so tests can capture the
    /// URL instead of actually spawning a browser window.
    /// </summary>
    public Action<string> UrlOpener { get; set; } = url =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    private void OpenUrl(string url)
    {
        try
        {
            UrlOpener(url);
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.General, "CivitaiWaitlist", $"Failed to launch browser for {url}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RemoveWaitlistEntry(CivitaiWaitlistEntry? entry)
    {
        if (entry is not null) _waitlist.Remove(entry);
    }

    [RelayCommand]
    private void OpenWaitlistEntryOnCivitai(CivitaiWaitlistEntry? entry)
    {
        if (entry is null) return;
        // civitai.com hides NSFW from unauthenticated visitors; route those to the mirror.
        var host = entry.IsNsfw ? "civitai.red" : "civitai.com";
        OpenUrl($"https://{host}/models/{entry.ModelId}?modelVersionId={entry.VersionId}");
    }

    /// <summary>"Update" button on the Waitlist tab — re-checks every entry against the API.</summary>
    [RelayCommand]
    private async Task UpdateWaitlistAsync()
    {
        StatusMessage = "Checking waitlist against Civitai…";
        var apiKey = await GetApiKeyAsync();
        await _waitlist.RefreshAllAsync(apiKey);
        StatusMessage = $"Waitlist updated — {_waitlist.AvailableCount} of {_waitlist.Entries.Count} available.";
    }

    /// <summary>"Move ready to queue" button — re-verifies ready entries, enqueues the free ones.</summary>
    [RelayCommand]
    private async Task MoveReadyWaitlistToQueueAsync()
    {
        var readyBefore = _waitlist.AvailableCount;
        var apiKey = await GetApiKeyAsync();
        var moved = await _waitlist.MoveReadyToQueueAsync(_queue, apiKey);
        StatusMessage = readyBefore == 0
            ? "No waitlist entries are ready to download yet."
            : moved == 0
                ? $"Re-checked {readyBefore} ready entr{(readyBefore == 1 ? "y" : "ies")} — still paywalled or unverifiable; kept on the waitlist."
                : $"Moved {moved} LoRA{(moved == 1 ? "" : "s")} from the waitlist to the download queue.";
    }

    /// <summary>
    /// Local 1-minute tick keeping countdowns and the tab badge fresh without any
    /// API traffic. Skipped headless (unit tests / no Avalonia app) — DispatcherTimer
    /// needs a running Avalonia dispatcher.
    /// </summary>
    private void StartWaitlistCountdownTimer()
    {
        if (Avalonia.Application.Current is null) return;
        _waitlistCountdownTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _waitlistCountdownTimer.Tick += (_, _) => _waitlist.RefreshAvailability();
        _waitlistCountdownTimer.Start();
    }

    [RelayCommand]
    private async Task RefreshInstalledAsync() => await RefreshInstalledSetAsync();

    /// <summary>
    /// Full page refresh: re-reads the installed set (so installed badges reflect
    /// what's on disk now), then re-runs the current query from page 1.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RefreshInstalledSetAsync();
        await SearchAsync();
    }

    private async Task DebouncedSearchAsync()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var ct = _debounceCts.Token;
        try
        {
            await Task.Delay(400, ct);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        await SearchAsync();
    }

    #endregion

    #region Property change hooks

    partial void OnSearchTextChanged(string value) { if (_initialized) _ = DebouncedSearchAsync(); }
    partial void OnSelectedSortChanged(string? value) { if (_initialized) _ = SearchAsync(); }
    partial void OnSelectedPeriodChanged(CivitaiPeriod value) { if (_initialized) _ = SearchAsync(); }
    partial void OnSelectedModelTypeChanged(ModelTypeOption? value) { if (_initialized) _ = SearchAsync(); }
    // ShowNsfwContent is a client-side filter (the API call always requests NSFW). No
    // re-search needed when toggled — just reapply local filters.
    partial void OnShowNsfwContentChanged(bool value) => ApplyClientSideFilters();
    partial void OnHideEarlyAccessModelsChanged(bool value) => ApplyClientSideFilters();
    partial void OnHideInstalledModelsChanged(bool value) => ApplyClientSideFilters();

    #endregion

    private const int TargetVisibleCount = 50;
    private const int MaxAutoPaginateIterations = 10;

    /// <summary>
    /// When client-side filters drop the visible count below this floor, the VM
    /// quietly kicks off a Load-more so the user doesn't see a half-empty grid
    /// after toggling Hide Installed / Hide Early Access / Show NSFW.
    /// </summary>
    private const int AutoLoadMoreThreshold = 30;

    /// <summary>
    /// Fetches paginated results until <see cref="TargetVisibleCount"/> visible items
    /// have been collected, the cursor runs out, or <see cref="MaxAutoPaginateIterations"/>
    /// safety iterations are hit. Without this loop a filter that excludes most items
    /// would surface an incomplete set on the first search and force a Load more click.
    /// </summary>
    private async Task LoadNextAsync(CancellationToken ct)
    {
        if (_civitaiClient is null)
        {
            StatusMessage = "Civitai client is not available.";
            _logger?.Warn(LogCategory.Network, "CivitaiBrowser", "Search skipped — ICivitaiClient is null in this DI scope.");
            return;
        }

        try
        {
            IsBusy = true;
            var isFirstBatch = string.IsNullOrEmpty(_nextCursor);
            StatusMessage = isFirstBatch ? "Searching Civitai..." : "Loading more...";

            var apiKey = await GetApiKeyAsync();
            var existingIds = new HashSet<int>(Results.Where(r => r.Model is not null).Select(r => r.Model!.Id));
            var preBatchCount = Results.Count;
            var batchStart = System.Diagnostics.Stopwatch.StartNew();

            string? cursor = _nextCursor;
            CivitaiModelsQuery? lastQuery = null;
            CivitaiPagedResponse<CivitaiModel>? lastResponse = null;
            var iterations = 0;
            var totalApiItems = 0;

            while (true)
            {
                iterations++;
                var query = BuildQuery(cursor);
                lastQuery = query;

                var requestUrl = "https://civitai.com/api/v1/models?" + DescribeQuery(query);
                _logger?.Info(LogCategory.Network, "CivitaiBrowser",
                    iterations == 1 && isFirstBatch ? "Starting search"
                    : iterations == 1 ? "Fetching next batch"
                    : $"Auto-paginating (iteration {iterations})",
                    $"GET {requestUrl}\nApiKey set: {!string.IsNullOrEmpty(apiKey)}\nVisible so far: {VisibleCount}/{TargetVisibleCount}");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _civitaiClient.GetModelsAsync(query, apiKey, ct);
                sw.Stop();
                if (ct.IsCancellationRequested) return;

                lastResponse = response;
                totalApiItems += response.Items.Count;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var model in response.Items)
                    {
                        if (model.ModelVersions.Count == 0) continue;
                        if (model.Mode is not null) continue; // skip archived/taken-down
                        if (!existingIds.Add(model.Id)) continue;
                        var vm = new CivitaiResultViewModel(model, ShowNsfwContent)
                        {
                            EnqueueAllVersionsHandler = EnqueueAllVersionsForCard
                        };
                        vm.ApplyInstalledIndex(_installed);
                        vm.SelectionChanged += OnResultSelectionChanged;
                        Results.Add(vm);
                    }
                });

                ApplyClientSideFilters();

                _logger?.Debug(LogCategory.Network, "CivitaiBrowser",
                    $"Iter {iterations}: {response.Items.Count} from API → {VisibleCount} visible total ({sw.ElapsedMilliseconds} ms)");

                cursor = response.Metadata?.NextCursor;

                // Stop if: enough visible items, no more cursor, or safety cap hit.
                if (VisibleCount >= TargetVisibleCount
                    || string.IsNullOrEmpty(cursor)
                    || iterations >= MaxAutoPaginateIterations)
                {
                    break;
                }
            }

            batchStart.Stop();
            _nextCursor = cursor;
            OnPropertyChanged(nameof(HasMore));

            // Tag-fallback: REST query= is Meilisearch full-text these days (names, tags,
            // and more), but an explicit tag= query can still surface models the text
            // ranking pushed out of the fetched pages. When a fresh search yielded thin
            // results, fire a tag= query with the same text and merge unique ids in.
            if (isFirstBatch
                && !string.IsNullOrWhiteSpace(SearchText)
                && VisibleCount < TargetVisibleCount
                && lastQuery is not null)
            {
                await RunTagFallbackAsync(lastQuery, apiKey, existingIds, ct);
            }

            // The REST API silently ignores sort= whenever query= is present — pages come
            // back in Meilisearch relevance order — and the tag-fallback appends its items
            // at the end. Re-sort the collected results locally so the selected sort still
            // holds during a text search. Without a search term the API order is already
            // correct, so don't touch it.
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                await ResortResultsAsync();
            }

            var addedThisBatch = Results.Count - preBatchCount;
            _logger?.Info(LogCategory.Network, "CivitaiBrowser",
                $"Batch complete: {iterations} request(s), {totalApiItems} items from API, {addedThisBatch} new unique, {VisibleCount} visible total ({batchStart.ElapsedMilliseconds} ms)",
                $"Next cursor: {_nextCursor ?? "(none)"}\nLast response metadata.totalItems: {lastResponse?.Metadata?.TotalItems}");

            StatusMessage = Results.Count == 0 ? "No results." : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // user typed again — silently drop
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Civitai request failed: {ex.StatusCode} {ex.Message}";
            _logger?.Warn(LogCategory.Network, "CivitaiBrowser", $"HTTP {ex.StatusCode} — {ex.Message}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
            _logger?.Warn(LogCategory.Network, "CivitaiBrowser",
                $"Search failed: {ex.Message}",
                ex.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Re-sorts the result list on the UI thread by the currently selected sort option.
    /// Move-based so bound cards keep their instances (selection, loaded previews).
    /// </summary>
    private async Task ResortResultsAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (Results.Count < 2) return;
            var desired = CivitaiSearchResultSorter.Sort(Results, r => r.Model, SelectedSort);
            CivitaiSearchResultSorter.ApplyOrder(Results, desired);
        });
    }

    /// <summary>
    /// Fires a second <c>tag=</c> query using the user's search text and merges any
    /// new model ids into the result list. Civitai indexes a separate set of tags per
    /// model, and a "latex" search misses anything tagged "latex" but not named so.
    /// </summary>
    private async Task RunTagFallbackAsync(
        CivitaiModelsQuery primaryQuery,
        string? apiKey,
        HashSet<int> existingIds,
        CancellationToken ct)
    {
        try
        {
            var tagQuery = new CivitaiModelsQuery
            {
                Tag = SearchText,           // tag= instead of query=
                Types = primaryQuery.Types,
                Sort = primaryQuery.Sort,
                Period = primaryQuery.Period,
                Nsfw = primaryQuery.Nsfw,
                BaseModels = primaryQuery.BaseModels,
                Limit = 20,
            };

            var requestUrl = "https://civitai.com/api/v1/models?" + DescribeQuery(tagQuery);
            _logger?.Info(LogCategory.Network, "CivitaiBrowser",
                $"Name search returned <10; running tag-fallback for \"{SearchText}\"",
                $"GET {requestUrl}");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tagResponse = await _civitaiClient!.GetModelsAsync(tagQuery, apiKey, ct);
            sw.Stop();

            if (ct.IsCancellationRequested) return;

            var preCount = Results.Count;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var model in tagResponse.Items)
                {
                    if (model.ModelVersions.Count == 0) continue;
                    if (model.Mode is not null) continue; // skip archived/taken-down
                    if (!existingIds.Add(model.Id)) continue;
                    var vm = new CivitaiResultViewModel(model, ShowNsfwContent)
                    {
                        EnqueueAllVersionsHandler = EnqueueAllVersionsForCard
                    };
                    vm.ApplyInstalledIndex(_installed);
                    vm.SelectionChanged += OnResultSelectionChanged;
                    Results.Add(vm);
                }
            });

            // The merged cards default to visible — without a filter pass here,
            // Hide Installed / Hide Early Access / Show-NSFW were silently ignored
            // for every tag-fallback result (user-reported: named searches with
            // those toggles active showed hidden categories anyway).
            ApplyClientSideFilters();

            var addedFromTag = Results.Count - preCount;
            _logger?.Info(LogCategory.Network, "CivitaiBrowser",
                $"Tag-fallback: {tagResponse.Items.Count} items from API, {addedFromTag} new unique merged ({sw.ElapsedMilliseconds} ms)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Non-fatal — primary results are already populated.
            _logger?.Debug(LogCategory.Network, "CivitaiBrowser",
                $"Tag-fallback failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    /// Produces a human-readable query string for logging without forcing all callers
    /// to depend on the internal ToQueryString helper.
    /// </summary>
    private static string DescribeQuery(CivitaiModelsQuery q)
    {
        var parts = new List<string>();
        if (q.Limit.HasValue) parts.Add($"limit={q.Limit}");
        if (!string.IsNullOrWhiteSpace(q.Cursor)) parts.Add($"cursor={q.Cursor}");
        if (!string.IsNullOrWhiteSpace(q.Query)) parts.Add($"query={q.Query}");
        if (!string.IsNullOrWhiteSpace(q.Tag)) parts.Add($"tag={q.Tag}");
        if (q.Types is { Count: > 0 }) parts.Add($"types={string.Join("+", q.Types)}");
        if (!string.IsNullOrWhiteSpace(q.Sort)) parts.Add($"sort={q.Sort}");
        if (q.Period.HasValue) parts.Add($"period={q.Period}");
        if (!string.IsNullOrWhiteSpace(q.Nsfw)) parts.Add($"nsfw={q.Nsfw}");
        if (q.BaseModels is { Count: > 0 }) parts.Add($"baseModels={string.Join("+", q.BaseModels)}");
        return string.Join("&", parts);
    }

    private CivitaiModelsQuery BuildQuery(string? cursor)
    {
        // The selected option carries its own Types directly — no fragile
        // string-to-enum switch. A null Types list means "no types filter".
        IReadOnlyList<CivitaiModelType>? types = SelectedModelType?.Types;

        var selectedBaseModels = AvailableBaseModels
            .Where(b => b.IsSelected)
            .Select(b => b.BaseModelRaw)
            .ToList();
        // Only filter by base model when the selection is a strict subset.
        // "Nothing selected" and "all selected" both mean "don't filter".
        IReadOnlyList<string>? baseModels = selectedBaseModels.Count > 0
                                            && selectedBaseModels.Count < AvailableBaseModels.Count
                                            ? selectedBaseModels
                                            : null;

        return new CivitaiModelsQuery
        {
            Query = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
            Types = types,
            Sort = SelectedSort,
            Period = SelectedPeriod,
            // Always request NSFW from the API so we get the full result set, then
            // filter client-side via ShowNsfwContent. Sending
            // nsfw=false here strips them at the server and they can't come back.
            Nsfw = "true",
            BaseModels = baseModels,
            Limit = 50,
            Cursor = cursor
        };
    }

    [RelayCommand]
    private void ClearBaseModelFilters()
    {
        foreach (var item in AvailableBaseModels) item.IsSelected = false;
        OnPropertyChanged(nameof(IsBaseModelFilterActive));
        OnPropertyChanged(nameof(ActiveBaseModelFilterCount));
        if (!string.IsNullOrWhiteSpace(BaseModelFilterSearchText))
            RebuildFlyoutBaseModels();
        _ = SearchAsync();
    }

    /// <summary>
    /// Selection state that outlives mirror rebuilds. The source collection is
    /// cleared-then-refilled by the owning LoRA viewer during its own rebuilds
    /// (a Reset followed by per-item Adds, each landing in
    /// <see cref="RebuildBaseModelMirror"/>) — a per-rebuild snapshot would see
    /// the empty intermediate state and wipe the user's selections here.
    /// </summary>
    private readonly HashSet<string> _stickyBaseModelSelections = new(StringComparer.OrdinalIgnoreCase);

    private void RebuildBaseModelMirror()
    {
        // Fold the live item states into the sticky set: names currently listed
        // follow their checkbox; names absent from the current mirror keep their
        // remembered state until they rematerialize.
        foreach (var b in AvailableBaseModels)
        {
            if (b.IsSelected) _stickyBaseModelSelections.Add(b.BaseModelRaw);
            else _stickyBaseModelSelections.Remove(b.BaseModelRaw);
        }

        foreach (var existing in AvailableBaseModels)
        {
            existing.SelectionChanged -= OnBaseModelFilterToggled;
        }
        AvailableBaseModels.Clear();

        if (_baseModelSource is null) return;

        foreach (var src in _baseModelSource)
        {
            var item = new BaseModelFilterItem(src.BaseModelRaw)
            {
                IsSelected = _stickyBaseModelSelections.Contains(src.BaseModelRaw)
            };
            item.SelectionChanged += OnBaseModelFilterToggled;
            AvailableBaseModels.Add(item);
        }
        OnPropertyChanged(nameof(IsBaseModelFilterActive));
        OnPropertyChanged(nameof(ActiveBaseModelFilterCount));
        RebuildFlyoutBaseModels();
    }

    private void OnBaseModelSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(RebuildBaseModelMirror);
    }

    private void OnBaseModelFilterToggled(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsBaseModelFilterActive));
        OnPropertyChanged(nameof(ActiveBaseModelFilterCount));

        // A toggle can change which items the narrowed flyout shows (selected items
        // are pinned visible), so refresh the composed view while a search is active.
        if (!string.IsNullOrWhiteSpace(BaseModelFilterSearchText))
            RebuildFlyoutBaseModels();

        if (_initialized) _ = SearchAsync();
    }

    private void ApplyClientSideFilters()
    {
        foreach (var result in Results)
        {
            // Keep each card's thumbnail in line with the toggle: hiding NSFW swaps
            // adult previews for the model's safest image (no-op when unchanged).
            result.ApplyNsfwPreference(ShowNsfwContent);

            var hide = (HideEarlyAccessModels && result.IsEarlyAccess)
                       || (HideInstalledModels && result.IsInstalled)
                       || (!ShowNsfwContent && result.IsNsfw);
            result.IsHidden = hide;
        }
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HiddenByFilters));
        OnPropertyChanged(nameof(HasHiddenResults));

        MaybeTopUpVisibleResults();
    }

    /// <summary>
    /// Fires off a quiet Load-more when client-side filters have left the visible
    /// count below <see cref="AutoLoadMoreThreshold"/> and more pages exist. The
    /// <see cref="IsBusy"/> guard prevents re-entry while a fetch is already in
    /// flight — including the recursive call from within
    /// <see cref="LoadNextAsync"/>'s own end-of-iteration <see cref="ApplyClientSideFilters"/>.
    /// </summary>
    private void MaybeTopUpVisibleResults()
    {
        if (!_initialized) return;       // skip the constructor-time cascade
        if (IsBusy) return;              // a fetch is in flight; let it finish first
        if (!HasMore) return;            // nothing left to load
        if (VisibleCount >= AutoLoadMoreThreshold) return;

        _ = LoadNextAsync(_searchCts?.Token ?? CancellationToken.None);
    }

    private void OnResultSelectionChanged(object? sender, EventArgs e)
    {
        OnSelectionChanged();

        // Drive the destination preview from the most-recently-selected card so the
        // user sees an accurate target folder before clicking Start.
        if (sender is CivitaiResultViewModel { IsSelected: true } card && _queue.Destination is { } dest)
        {
            dest.PreviewBaseModel = card.BaseModel;
            dest.PreviewCategory = card.Category;
        }
    }

    private void EnqueueAllVersionsForCard(CivitaiResultViewModel card)
    {
        var pairs = card.Versions.Select(v => (card, v)).ToList();
        _ = EnqueueWithPreflightPromptAsync(pairs);
    }

    /// <summary>
    /// Enqueues the (result, pick) pairs, first showing ONE confirmation dialog when any
    /// of them is paywalled or already installed. Every flagged kind goes into that single
    /// prompt on purpose — a selection holding both an Early Access and an already-owned
    /// version must not produce two dialogs in a row.
    /// </summary>
    private async Task EnqueueWithPreflightPromptAsync(
        List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)> pairs)
    {
        if (pairs.Count == 0) return;

        // Guard: a download with no configured LoRA source has nowhere to land.
        // Without this check the job would fail at run time with "No download
        // destination set" — surface that as a clear up-front warning instead.
        if (_queue.Destination.HasNoSourceFolders
            && string.IsNullOrWhiteSpace(_queue.Destination.CustomFolderPath))
        {
            StatusMessage = "No LoRA source folder configured. Open Settings → LoRA Sources and add (and favorite) one before downloading.";
            _logger?.Warn(LogCategory.Download, "CivitaiQueue",
                $"Enqueue blocked: no LoRA source folders configured. {pairs.Count} item(s) NOT added.");
            return;
        }

        var eaPairs = pairs.Where(p => p.Pick.IsEarlyAccess).ToList();
        var installedPairs = pairs.Where(IsInstalledOnly).ToList();
        if (eaPairs.Count == 0 && installedPairs.Count == 0)
        {
            // Nothing to warn about — straight through.
            foreach (var (r, p) in pairs) _queue.Enqueue(r, p);
            return;
        }

        // Show the prompt with the flagged lists. The dialog needs a Window owner.
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
        {
            // No UI host available (design-time / headless) — fall back to "add all".
            foreach (var (r, p) in pairs) _queue.Enqueue(r, p);
            return;
        }

        static List<string> Titles(
            IEnumerable<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)> source)
            => source.Select(p => $"{p.Result.Name} — {p.Pick.Name}").Distinct().ToList();

        var tempTitles = Titles(eaPairs.Where(p => !p.Pick.IsPermanentlyPaid));
        var permanentTitles = Titles(eaPairs.Where(p => p.Pick.IsPermanentlyPaid));
        var installedTitles = Titles(installedPairs);

        // The unflagged count drives the "add the rest" button: with nothing else in the
        // selection there is no rest to add, so the dialog hides that option entirely.
        var dialog = new Views.Dialogs.DownloadPreflightDialog(
            tempTitles, permanentTitles, installedTitles,
            otherCount: pairs.Count - eaPairs.Count - installedPairs.Count);
        await dialog.ShowDialog(owner);
        ApplyPreflightChoice(dialog.Result, pairs);
    }

    /// <summary>
    /// Already-installed picks that aren't ALSO paywalled. A version can be both; the
    /// paywall is the more consequential warning, so it wins the classification and each
    /// pick appears in exactly one group of the dialog.
    /// </summary>
    private static bool IsInstalledOnly(
        (CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick) pair)
        => !pair.Pick.IsEarlyAccess && pair.Pick.IsInstalled;

    /// <summary>
    /// Applies the user's pre-download dialog choice to the pending (result, pick) pairs.
    /// Public (not shown-dialog-coupled) so every branch is unit-testable.
    /// </summary>
    public void ApplyPreflightChoice(
        Views.Dialogs.DownloadPreflightResult choice,
        List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)> pairs)
    {
        var eaPairs = pairs.Where(p => p.Pick.IsEarlyAccess).ToList();
        var installedPairs = pairs.Where(IsInstalledOnly).ToList();
        var unflagged = pairs.Where(p => !p.Pick.IsEarlyAccess && !p.Pick.IsInstalled).ToList();

        switch (choice)
        {
            case Views.Dialogs.DownloadPreflightResult.Cancel:
                _logger?.Info(LogCategory.Download, "CivitaiQueue",
                    $"Enqueue cancelled by user — {pairs.Count} version(s) NOT added ({eaPairs.Count} paid, {installedPairs.Count} already installed).");
                return;

            case Views.Dialogs.DownloadPreflightResult.SkipFlagged:
                foreach (var (r, p) in unflagged) _queue.Enqueue(r, p);
                StatusMessage = DescribeSkip(eaPairs.Count, installedPairs.Count, unflagged.Count);
                _logger?.Info(LogCategory.Download, "CivitaiQueue",
                    $"Skipped {eaPairs.Count} paid and {installedPairs.Count} already-installed version(s); enqueued {unflagged.Count}.");
                return;

            case Views.Dialogs.DownloadPreflightResult.DownloadAnyway:
                foreach (var (r, p) in pairs) _queue.Enqueue(r, p);
                _logger?.Info(LogCategory.Download, "CivitaiQueue",
                    $"User confirmed download anyway; {pairs.Count} version(s) added ({eaPairs.Count} are paid and will likely 401, {installedPairs.Count} re-downloaded).");
                return;

            case Views.Dialogs.DownloadPreflightResult.AddToWaitlist:
                // Installed picks are deliberately NOT enqueued here: the user chose the
                // cautious branch, so re-fetching a file they already own is not implied.
                foreach (var (r, p) in unflagged) _queue.Enqueue(r, p);
                var added = 0;
                var skippedPermanent = 0;
                foreach (var (r, p) in eaPairs)
                {
                    if (p.IsPermanentlyPaid) { skippedPermanent++; continue; }
                    if (_waitlist.TryAdd(r, p)) added++;
                }
                StatusMessage = $"Added {added} LoRA{(added == 1 ? "" : "s")} to the waitlist."
                    + (skippedPermanent == 0 ? "" : $" {skippedPermanent} permanently paid item{(skippedPermanent == 1 ? "" : "s")} skipped (never becomes free).")
                    + (installedPairs.Count == 0 ? "" : $" {installedPairs.Count} already-installed version{(installedPairs.Count == 1 ? "" : "s")} skipped.");
                _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
                    $"Dialog choice AddToWaitlist: {added} waitlisted, {skippedPermanent} permanent skipped, {installedPairs.Count} installed skipped, {unflagged.Count} enqueued.");
                return;

            case Views.Dialogs.DownloadPreflightResult.OpenWebsite:
                foreach (var (r, p) in unflagged) _queue.Enqueue(r, p);
                foreach (var result in eaPairs.Select(p => p.Result).Distinct())
                {
                    if (result.Model is null) continue;
                    var host = result.IsNsfw ? "civitai.red" : "civitai.com";
                    OpenUrl($"https://{host}/models/{result.Model.Id}");
                }
                _logger?.Info(LogCategory.Download, "CivitaiWaitlist",
                    $"Dialog choice OpenWebsite: opened {eaPairs.Select(p => p.Result).Distinct().Count()} model page(s), {unflagged.Count} enqueued, {installedPairs.Count} installed skipped.");
                return;
        }
    }

    private static string DescribeSkip(int paid, int installed, int enqueued)
    {
        var skipped = new List<string>();
        if (paid > 0) skipped.Add($"{paid} paid");
        if (installed > 0) skipped.Add($"{installed} already installed");
        return enqueued == 0
            ? $"Skipped {string.Join(" and ", skipped)}; nothing left to download."
            : $"Skipped {string.Join(" and ", skipped)}; queued {enqueued} download{(enqueued == 1 ? "" : "s")}.";
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
    }

    private async Task RefreshInstalledSetAsync()
    {
        try
        {
            if (App.Services is null) return;
            using var scope = App.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var uow = scope.ServiceProvider.GetService<IUnitOfWork>();
            if (uow is null) return;

            // Honor the IsEnabled toggle on LoRA sources: a LoRA stored under a
            // disabled source no longer counts as "installed" for the Hide Installed
            // filter / badge. Mirrors the Installed tab's filtering rule.
            var settings = scope.ServiceProvider.GetService<IAppSettingsService>();
            IReadOnlyList<string>? enabledRoots = null;
            if (settings is not null)
            {
                enabledRoots = await settings.GetEnabledLoraSourcesAsync();
            }

            var set = await uow.Models.GetInstalledCivitaiVersionIdsAsync(enabledRoots);
            var hashes = await uow.Models.GetInstalledFileHashesAsync(enabledRoots);
            _installed = new CivitaiInstalledIndex(set, hashes);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Refreshes the card badge AND every version row — a download that just
                // finished must light up the row it landed in, not only the card.
                foreach (var r in Results) r.ApplyInstalledIndex(_installed);
                ApplyClientSideFilters();
            });
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.Network, "CivitaiBrowser", $"Installed-set refresh failed: {ex.Message}");
        }
    }

    private async Task<string?> GetApiKeyAsync()
    {
        if (App.Services is not null)
        {
            using var scope = App.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var settingsService = scope.ServiceProvider.GetService<IAppSettingsService>();
            if (settingsService is not null)
                return await settingsService.GetCivitaiApiKeyAsync();
        }

        return _settingsService is not null
            ? await _settingsService.GetCivitaiApiKeyAsync()
            : null;
    }
}

/// <summary>
/// Pairing of a UI dropdown label with the concrete <see cref="CivitaiModelType"/>
/// array sent to Civitai. <see cref="Types"/> = <c>null</c> means "send no types
/// filter at all" (the API returns Checkpoints, embeddings, etc. alongside LoRAs).
/// <para>
/// <see cref="ToString"/> returns the label so the default <c>ComboBox</c>
/// rendering picks it up without needing a <c>DisplayMemberPath</c>.
/// </para>
/// </summary>
public sealed record ModelTypeOption(string Label, IReadOnlyList<CivitaiModelType>? Types)
{
    public override string ToString() => Label;
}
