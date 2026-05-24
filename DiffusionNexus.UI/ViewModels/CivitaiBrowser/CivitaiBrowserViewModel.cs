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

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _debounceCts;
    private string? _nextCursor;
    private bool _isLoading;
    private bool _initialized;
    private HashSet<int> _installedVersionIds = [];
    private CivitaiResultViewModel? _lastClickedItem;

    public CivitaiBrowserViewModel()
        : this(null, null, null, new CivitaiDownloadQueue(null), null)
    {
        // Design-time only
        Results.Add(CivitaiResultViewModel.CreateDesignSample());
    }

    public CivitaiBrowserViewModel(
        ICivitaiClient? civitaiClient,
        IAppSettingsService? settingsService,
        IUnifiedLogger? logger,
        CivitaiDownloadQueue queue,
        ObservableCollection<BaseModelFilterItem>? sharedBaseModelSource)
    {
        _civitaiClient = civitaiClient;
        _settingsService = settingsService;
        _logger = logger;
        _queue = queue;

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

    public bool IsBaseModelFilterActive => AvailableBaseModels.Any(f => f.IsSelected);
    public int ActiveBaseModelFilterCount => AvailableBaseModels.Count(f => f.IsSelected);

    #endregion

    #region Results + Queue

    public ObservableCollection<CivitaiResultViewModel> Results { get; } = [];

    public CivitaiDownloadQueue Queue => _queue;

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
        return EnqueueWithEarlyAccessPromptAsync(pairs);
    }

    [RelayCommand]
    private Task StartQueueAsync() => _queue.StartAllAsync();

    [RelayCommand]
    private void ClearCompleted() => _queue.ClearCompleted();

    [RelayCommand]
    private void ClearQueue() => _queue.ClearAll();

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

    [RelayCommand]
    private async Task RefreshInstalledAsync() => await RefreshInstalledSetAsync();

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
                        var vm = new CivitaiResultViewModel(model)
                        {
                            IsInstalled = model.ModelVersions.Any(v => _installedVersionIds.Contains(v.Id)),
                            EnqueueAllVersionsHandler = EnqueueAllVersionsForCard
                        };
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

            // Tag-fallback: REST query= is a name-only substring match; civitai.com's web
            // index also matches tags/descriptions. When a fresh name-search yielded thin
            // results, fire a tag= query with the same text and merge unique ids in.
            if (isFirstBatch
                && !string.IsNullOrWhiteSpace(SearchText)
                && VisibleCount < TargetVisibleCount
                && lastQuery is not null)
            {
                await RunTagFallbackAsync(lastQuery, apiKey, existingIds, ct);
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
                    if (!existingIds.Add(model.Id)) continue;
                    var vm = new CivitaiResultViewModel(model)
                    {
                        IsInstalled = model.ModelVersions.Any(v => _installedVersionIds.Contains(v.Id)),
                        EnqueueAllVersionsHandler = EnqueueAllVersionsForCard
                    };
                    vm.SelectionChanged += OnResultSelectionChanged;
                    Results.Add(vm);
                }
            });

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
        _ = SearchAsync();
    }

    private void RebuildBaseModelMirror()
    {
        // Preserve current selection by raw name.
        var previouslySelected = new HashSet<string>(
            AvailableBaseModels.Where(b => b.IsSelected).Select(b => b.BaseModelRaw),
            StringComparer.OrdinalIgnoreCase);

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
                IsSelected = previouslySelected.Contains(src.BaseModelRaw)
            };
            item.SelectionChanged += OnBaseModelFilterToggled;
            AvailableBaseModels.Add(item);
        }
        OnPropertyChanged(nameof(IsBaseModelFilterActive));
        OnPropertyChanged(nameof(ActiveBaseModelFilterCount));
    }

    private void OnBaseModelSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(RebuildBaseModelMirror);
    }

    private void OnBaseModelFilterToggled(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsBaseModelFilterActive));
        OnPropertyChanged(nameof(ActiveBaseModelFilterCount));
        if (_initialized) _ = SearchAsync();
    }

    private void ApplyClientSideFilters()
    {
        foreach (var result in Results)
        {
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
        _ = EnqueueWithEarlyAccessPromptAsync(pairs);
    }

    /// <summary>
    /// Filters out any (result, pick) pairs flagged Early Access and shows a
    /// confirmation dialog when any are present, letting the user choose to add
    /// them anyway, drop them and download the rest, or cancel.
    /// </summary>
    private async Task EnqueueWithEarlyAccessPromptAsync(
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
        if (eaPairs.Count == 0)
        {
            // No EA items — straight through.
            foreach (var (r, p) in pairs) _queue.Enqueue(r, p);
            return;
        }

        // Show the prompt with the EA list. The dialog needs a Window owner.
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
        {
            // No UI host available (design-time / headless) — fall back to "add all".
            foreach (var (r, p) in pairs) _queue.Enqueue(r, p);
            return;
        }

        var titles = eaPairs
            .Select(p => $"{p.Result.Name} — {p.Pick.Name}")
            .Distinct()
            .ToList();

        var dialog = new Views.Dialogs.EarlyAccessConfirmDialog(titles);
        await dialog.ShowDialog(owner);

        switch (dialog.Result)
        {
            case Views.Dialogs.EarlyAccessConfirmResult.Cancel:
                _logger?.Info(LogCategory.Download, "CivitaiQueue",
                    $"Enqueue cancelled by user — {pairs.Count} version(s) NOT added ({eaPairs.Count} were EA).");
                return;

            case Views.Dialogs.EarlyAccessConfirmResult.SkipEarlyAccess:
                var nonEa = pairs.Where(p => !p.Pick.IsEarlyAccess).ToList();
                foreach (var (r, p) in nonEa) _queue.Enqueue(r, p);
                _logger?.Info(LogCategory.Download, "CivitaiQueue",
                    $"Skipped {eaPairs.Count} Early Access version(s); enqueued {nonEa.Count} non-EA.");
                return;

            case Views.Dialogs.EarlyAccessConfirmResult.AddAnyway:
                foreach (var (r, p) in pairs) _queue.Enqueue(r, p);
                _logger?.Info(LogCategory.Download, "CivitaiQueue",
                    $"User confirmed Early Access enqueue; {pairs.Count} version(s) added ({eaPairs.Count} are EA and will likely 401).");
                return;
        }
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
            _installedVersionIds = set;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var r in Results)
                {
                    if (r.Model is null) continue;
                    r.IsInstalled = r.Model.ModelVersions.Any(v => set.Contains(v.Id));
                }
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
