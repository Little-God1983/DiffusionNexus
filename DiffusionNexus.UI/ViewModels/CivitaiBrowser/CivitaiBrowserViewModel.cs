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
        SelectedSort = CivitaiModelSort.HighestRated;

        PeriodOptions = new ObservableCollection<CivitaiPeriod>(Enum.GetValues<CivitaiPeriod>());
        SelectedPeriod = CivitaiPeriod.AllTime;

        ModelTypeOptions = new ObservableCollection<string> { "LORA", "LoCon", "DoRA", "All LoRA types", "All models" };
        SelectedModelType = "LORA";

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
    private string? _selectedModelType;

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
    public ObservableCollection<string> ModelTypeOptions { get; }

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

        Results.Clear();
        OnSelectionChanged();
        _nextCursor = null;
        await LoadNextAsync(ct);
    }

    /// <summary>
    /// Fetches the next page. If client-side filters (Hide installed / Hide early-access)
    /// hide every newly-loaded card, automatically follow the cursor up to <c>maxChained</c>
    /// times so the user doesn't have to click through fully-filtered pages.
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (!HasMore || IsBusy) return;

        const int maxChained = 3;
        var ct = _searchCts?.Token ?? CancellationToken.None;

        for (var i = 0; i < maxChained; i++)
        {
            var visibleBefore = VisibleCount;
            await LoadNextAsync(ct);
            if (ct.IsCancellationRequested) return;

            // Got new visible cards, or no more pages → stop chaining.
            if (VisibleCount > visibleBefore || !HasMore) return;
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var r in Results)
        {
            r.IsSelected = false;
        }
        OnSelectionChanged();
    }

    [RelayCommand]
    private void AddSelectionToQueue()
    {
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
                _queue.Enqueue(result, pick);
            }
        }
    }

    [RelayCommand]
    private Task StartQueueAsync() => _queue.StartAllAsync();

    [RelayCommand]
    private void ClearCompleted() => _queue.ClearCompleted();

    [RelayCommand]
    private void ClearQueue() => _queue.ClearAll();

    [RelayCommand]
    private void RemoveJob(CivitaiDownloadJob? job)
    {
        if (job is not null) _queue.Remove(job);
    }

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
    partial void OnSelectedModelTypeChanged(string? value) { if (_initialized) _ = SearchAsync(); }
    // ShowNsfwContent is a client-side filter (the API call always requests NSFW). No
    // re-search needed when toggled — just reapply local filters.
    partial void OnShowNsfwContentChanged(bool value) => ApplyClientSideFilters();
    partial void OnHideEarlyAccessModelsChanged(bool value) => ApplyClientSideFilters();
    partial void OnHideInstalledModelsChanged(bool value) => ApplyClientSideFilters();

    #endregion

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
            var isFirstPage = string.IsNullOrEmpty(_nextCursor);
            StatusMessage = isFirstPage ? "Searching Civitai..." : "Loading more...";

            var query = BuildQuery(_nextCursor);
            var apiKey = await GetApiKeyAsync();

            var requestUrl = "https://civitai.com/api/v1/models?" + DescribeQuery(query);
            _logger?.Info(LogCategory.Network, "CivitaiBrowser",
                isFirstPage ? "Starting search" : "Fetching next page",
                $"GET {requestUrl}\nApiKey set: {!string.IsNullOrEmpty(apiKey)}");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await _civitaiClient.GetModelsAsync(query, apiKey, ct);
            sw.Stop();

            if (ct.IsCancellationRequested) return;

            var apiCount = response.Items.Count;
            var preFilterCount = Results.Count;
            var existingIds = new HashSet<int>(Results.Where(r => r.Model is not null).Select(r => r.Model!.Id));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var model in response.Items)
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

            var addedCount = Results.Count - preFilterCount;
            _nextCursor = response.Metadata?.NextCursor;
            OnPropertyChanged(nameof(HasMore));

            // Tag-fallback: Civitai's REST query= is a name-only substring match. The web
            // search index also matches tags and descriptions, which is where most of the
            // "missing" results live. When a fresh name-search returned <10 items, fire a
            // tag= query with the same text and merge unique model ids in.
            if (isFirstPage
                && !string.IsNullOrWhiteSpace(SearchText)
                && response.Items.Count < 10)
            {
                await RunTagFallbackAsync(query, apiKey, existingIds, ct);
            }

            ApplyClientSideFilters();

            var hiddenCount = HiddenByFilters;
            var visibleCount = VisibleCount;

            _logger?.Info(LogCategory.Network, "CivitaiBrowser",
                $"Response: {apiCount} items from API, {addedCount} added, {visibleCount} visible, {hiddenCount} hidden ({sw.ElapsedMilliseconds} ms)",
                $"Total results so far: {Results.Count}\nNext cursor: {_nextCursor ?? "(none)"}\nMetadata.totalItems: {response.Metadata?.TotalItems}\nMetadata.totalPages: {response.Metadata?.TotalPages}\nMetadata.currentPage: {response.Metadata?.CurrentPage}\nMetadata.nextPage: {response.Metadata?.NextPage ?? "(none)"}");

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
        IReadOnlyList<CivitaiModelType>? types = SelectedModelType switch
        {
            "LORA" => [CivitaiModelType.LORA],
            "LoCon" => [CivitaiModelType.LoCon],
            "DoRA" => [CivitaiModelType.DoRA],
            "All LoRA types" => [CivitaiModelType.LORA, CivitaiModelType.LoCon, CivitaiModelType.DoRA],
            "All models" => null, // no types filter → API returns Checkpoints, embeddings, etc. too
            _ => [CivitaiModelType.LORA]
        };

        var selectedBaseModels = AvailableBaseModels
            .Where(b => b.IsSelected)
            .Select(b => b.BaseModelRaw)
            .ToList();
        // Match StabilityMatrix: only filter by base model when the selection is a
        // strict subset. "Nothing selected" and "all selected" both mean "don't filter".
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
            // StabilityMatrix-equivalent: always request NSFW from the API so we get the
            // full result set, then filter client-side via ShowNsfwContent. Sending
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
    }

    private void OnResultSelectionChanged(object? sender, EventArgs e) => OnSelectionChanged();

    private void EnqueueAllVersionsForCard(CivitaiResultViewModel card)
    {
        foreach (var pick in card.Versions)
        {
            _queue.Enqueue(card, pick);
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

            var set = await uow.Models.GetInstalledCivitaiVersionIdsAsync();
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
