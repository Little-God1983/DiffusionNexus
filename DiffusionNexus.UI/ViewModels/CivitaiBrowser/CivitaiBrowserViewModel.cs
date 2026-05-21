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

        ModelTypeOptions = new ObservableCollection<string> { "All", "LORA", "LoCon", "DoRA" };
        SelectedModelType = "All";

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
    private bool _hideEarlyAccessModels = true;

    [ObservableProperty]
    private bool _showNsfwContent;

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

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (!HasMore || IsBusy) return;
        await LoadNextAsync(_searchCts?.Token ?? CancellationToken.None);
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
    partial void OnShowNsfwContentChanged(bool value) { if (_initialized) _ = SearchAsync(); }
    partial void OnHideEarlyAccessModelsChanged(bool value) => ApplyClientSideFilters();
    partial void OnHideInstalledModelsChanged(bool value) => ApplyClientSideFilters();

    #endregion

    private async Task LoadNextAsync(CancellationToken ct)
    {
        if (_civitaiClient is null)
        {
            StatusMessage = "Civitai client is not available.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = !string.IsNullOrEmpty(_nextCursor) ? "Loading more..." : "Searching Civitai...";

            var query = BuildQuery(_nextCursor);
            var apiKey = await GetApiKeyAsync();
            var response = await _civitaiClient.GetModelsAsync(query, apiKey, ct);

            if (ct.IsCancellationRequested) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var model in response.Items)
                {
                    if (model.ModelVersions.Count == 0) continue;
                    var vm = new CivitaiResultViewModel(model)
                    {
                        IsInstalled = model.ModelVersions.Any(v => _installedVersionIds.Contains(v.Id))
                    };
                    vm.SelectionChanged += OnResultSelectionChanged;
                    Results.Add(vm);
                }
            });

            _nextCursor = response.Metadata?.NextCursor;
            OnPropertyChanged(nameof(HasMore));

            ApplyClientSideFilters();
            StatusMessage = Results.Count == 0 ? "No results." : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // user typed again — silently drop
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Civitai request failed: {ex.StatusCode} {ex.Message}";
            _logger?.Warn(LogCategory.Network, "CivitaiBrowser", $"Search failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
            _logger?.Warn(LogCategory.Network, "CivitaiBrowser", $"Search failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private CivitaiModelsQuery BuildQuery(string? cursor)
    {
        IReadOnlyList<CivitaiModelType> types = SelectedModelType switch
        {
            "LORA" => [CivitaiModelType.LORA],
            "LoCon" => [CivitaiModelType.LoCon],
            "DoRA" => [CivitaiModelType.DoRA],
            _ => [CivitaiModelType.LORA, CivitaiModelType.LoCon, CivitaiModelType.DoRA]
        };

        var selectedBaseModels = AvailableBaseModels
            .Where(b => b.IsSelected)
            .Select(b => b.BaseModelRaw)
            .ToList();
        IReadOnlyList<string>? baseModels = selectedBaseModels.Count > 0 ? selectedBaseModels : null;

        return new CivitaiModelsQuery
        {
            Query = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
            Types = types,
            Sort = SelectedSort,
            Period = SelectedPeriod,
            Nsfw = ShowNsfwContent ? null : false,
            BaseModels = baseModels,
            Limit = 20,
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
                       || (HideInstalledModels && result.IsInstalled);
            result.IsHidden = hide;
        }
    }

    private void OnResultSelectionChanged(object? sender, EventArgs e) => OnSelectionChanged();

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
