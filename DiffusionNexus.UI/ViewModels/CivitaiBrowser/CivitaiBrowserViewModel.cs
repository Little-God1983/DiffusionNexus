using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Infrastructure.Services;
using DiffusionNexus.UI.Models;
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
    private ICivitaiApiKeyProvider? _apiKeyProvider;
    private Avalonia.Threading.DispatcherTimer? _waitlistCountdownTimer;

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _debounceCts;
    private string? _nextCursor;
    private bool _isLoading;
    private bool _initialized;
    private CivitaiInstalledIndex _installed = CivitaiInstalledIndex.Empty;
    private CivitaiResultViewModel? _lastClickedItem;

    /// <summary>
    /// The "All LoRA types" <see cref="ModelTypeOption"/>, captured once in the constructor.
    /// <see cref="ResetFilter"/> and <see cref="CanReset"/> read this same instance instead of
    /// re-deriving the label lookup, so a future relabel can't rot one copy and not the other.
    /// </summary>
    private readonly ModelTypeOption _defaultModelType;

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
        ObservableCollection<BaseModelFilterItem>? sharedBaseModelSource,
        ICivitaiApiKeyProvider? apiKeyProvider = null)
    {
        _civitaiClient = civitaiClient;
        _settingsService = settingsService;
        _logger = logger;
        _queue = queue;
        _waitlist = waitlist;
        _apiKeyProvider = apiKeyProvider;
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
        _defaultModelType = ModelTypeOptions.Single(o => o.Label == "All LoRA types");
        SelectedModelType = _defaultModelType;

        // Base model filter mirrors the Installed-tab list (same names) but holds its
        // own selection state — toggling here doesn't disturb the installed filter.
        AvailableBaseModels = new ObservableCollection<BaseModelFilterItem>();
        if (sharedBaseModelSource is not null)
        {
            _baseModelSource = sharedBaseModelSource;
            RebuildBaseModelMirror();
            _baseModelSource.CollectionChanged += OnBaseModelSourceChanged;
        }

        // Enable search-on-filter-change now that the initial property cascade is done. The first
        // search waits for EnsureLoadedAsync: this VM is constructed when the LoRA Viewer opens,
        // so searching here meant opening the Installed tab spent up to ten paginated Civitai
        // requests on a tab the user may never look at.
        _initialized = true;
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

    /// <summary>
    /// The "Show" flyout's four positively-phrased toggles, all ticked by default.
    /// Each hides exactly what its badge marks on <see cref="CivitaiResultViewModel"/> —
    /// see the predicate in <see cref="ApplyClientSideFilters"/>. All four are
    /// client-side only (the API call always requests the full unfiltered set), so
    /// their change hooks re-apply local filters and never re-search.
    /// </summary>
    [ObservableProperty]
    private bool _showInstalled = true;

    [ObservableProperty]
    private bool _showEarlyAccess = true;

    [ObservableProperty]
    private bool _showPaywalled = true;

    [ObservableProperty]
    private bool _showNsfw = true;

    /// <summary>Number of the four Show-flyout toggles currently unticked (0 = filter inactive).</summary>
    public int ActiveShowFilterCount =>
        (ShowInstalled ? 0 : 1) + (ShowEarlyAccess ? 0 : 1) + (ShowPaywalled ? 0 : 1) + (ShowNsfw ? 0 : 1);

    public bool IsShowFilterActive => ActiveShowFilterCount > 0;

    /// <summary>Re-ticks all four Show-flyout toggles. Analogue of <see cref="ClearBaseModelFilters"/>.</summary>
    [RelayCommand]
    private void ShowAllResultFilters()
    {
        ShowInstalled = true;
        ShowEarlyAccess = true;
        ShowPaywalled = true;
        ShowNsfw = true;
    }

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

    /// <summary>
    /// True when anything in the filter bar differs from the constructor's defaults — drives
    /// the Reset button's <c>IsEnabled</c> so it greys out once the bar is already at rest.
    /// Sourced the same way <see cref="ResetFilter"/> restores them: the constants
    /// <see cref="CivitaiModelSort.Newest"/> / <see cref="CivitaiPeriod.AllTime"/> and the
    /// captured <see cref="_defaultModelType"/> instance, plus <see cref="_stickyBaseModelSelections"/>
    /// so a parked-but-not-yet-materialized base model still counts as "not at rest". Re-raised
    /// everywhere the other computed badge properties (<see cref="ActiveShowFilterCount"/>,
    /// <see cref="ActiveBaseModelFilterCount"/>) are re-raised.
    /// </summary>
    public bool CanReset =>
        !string.IsNullOrEmpty(SearchText)
        || !ShowInstalled || !ShowEarlyAccess || !ShowPaywalled || !ShowNsfw
        || IsBaseModelFilterActive
        || _stickyBaseModelSelections.Count > 0
        || SelectedSort != CivitaiModelSort.Newest
        || SelectedPeriod != CivitaiPeriod.AllTime
        || SelectedModelType != _defaultModelType;

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
        // Marks that SOME search has been kicked off, so EnsureLoadedAsync's deferred
        // initial search (see SearchIfNotAlreadyStartedAsync) backs off instead of
        // cancelling/clobbering it if the user got here first.
        Interlocked.Exchange(ref _searchStarted, 1);

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

    private int _loaded;
    private int _searchStarted;

    /// <summary>
    /// Runs the first search, once, however many times the tab is shown. Called when the Browse
    /// Civitai view is first attached to the visual tree.
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        if (Interlocked.Exchange(ref _loaded, 1) == 1) return;

        _logger?.Debug(LogCategory.Network, "CivitaiBrowser", "Deferred initial load starting (view attached + DataContext bound).");

        // Restore the saved filter BEFORE the deferred search: BuildQuery reads the
        // base-model selection (AvailableBaseModels), so a restore landing after that
        // search would issue a query for the wrong filter. This is NOT a bare
        // single-row read: GetCivitaiBrowserFilterJsonAsync goes through
        // AppSettingsService.GetSettingsAsync, which clears the EF change tracker,
        // loads AppSettings with four Include()d collections (LoraSources,
        // DatasetCategories, ImageGalleries, BaseModelFolders), calls SaveChangesAsync,
        // and — on a brand-new database — seeds default dataset categories and
        // reloads. It's still warm and bounded in the case that matters here: by the
        // time this view can be reached the EF model is already built and the
        // in-process settings row already materialized from earlier startup reads, so
        // this is a cheap re-read of an already-warm context, not RefreshInstalledSetAsync's
        // full-library scan below — that scan is what actually gated the "browser opens
        // empty" regression, and it still runs concurrently with the search, unaffected.
        await RestoreSavedFilterAsync();

        // Run concurrently, same as the constructor did before the first search moved here.
        // RefreshInstalledSetAsync re-applies itself to whatever is in Results once it lands
        // (see its own doc comment), so it does not need to finish before the search starts —
        // and gating the search behind a full-library DB scan is exactly the "browser opens
        // empty" regression this fixes: on a large library the scan can take long enough that
        // the user perceives an empty grid even though the search itself would have been fast.
        await Task.WhenAll(RefreshInstalledSetAsync(), SearchIfNotAlreadyStartedAsync());
    }

    /// <summary>
    /// Snapshots the current filter for persistence: base-model selection (including any
    /// saved names still pending a mirror rebuild, via <see cref="FoldLiveBaseModelSelectionsIntoSticky"/>
    /// so re-saving while a name is temporarily absent from the source doesn't truncate it —
    /// same intent as <c>LoraViewerViewModel.CaptureFilter</c>) plus the four Show flags.
    /// </summary>
    internal CivitaiBrowserFilterData CaptureFilter()
    {
        FoldLiveBaseModelSelectionsIntoSticky();

        return new CivitaiBrowserFilterData
        {
            SelectedBaseModels = _stickyBaseModelSelections.ToList(),
            ShowInstalled = ShowInstalled,
            ShowEarlyAccess = ShowEarlyAccess,
            ShowPaywalled = ShowPaywalled,
            ShowNsfw = ShowNsfw,
        };
    }

    /// <summary>
    /// Applies a saved filter: the four Show flags (null means "show" — see
    /// <see cref="CivitaiBrowserFilterData.ShowInstalled"/>) and the base-model selection.
    /// Selection is written directly to the live mirror items (case-insensitive name match)
    /// with <see cref="OnBaseModelFilterToggled"/> suppressed — that handler unconditionally
    /// re-searches, and this is called once per restored name, so without the guard a
    /// four-base-model saved filter would fire four separate Civitai requests instead of
    /// letting <see cref="EnsureLoadedAsync"/>'s single deferred search run afterwards. Names
    /// the mirror doesn't know yet (catalog/base-model source hasn't produced them) are parked
    /// in <see cref="_stickyBaseModelSelections"/> so <see cref="RebuildBaseModelMirror"/>
    /// re-selects them the moment they materialize — the same mechanism a live toggle already
    /// relies on when its name later drops out of the source.
    /// </summary>
    /// <param name="data">The saved filter to apply.</param>
    /// <param name="applyBaseModelSelection">
    /// When <see langword="false"/>, the four Show flags still apply (safe — they act
    /// directly on already-fetched <see cref="Results"/> via <see cref="ApplyClientSideFilters"/>,
    /// not on the query) but the base-model selection is left untouched. Used by
    /// <see cref="RestoreSavedFilterAsync"/> when the user's own search already started
    /// before the restore reached this point: <see cref="BuildQuery"/> already ran for
    /// that search without the restored base models, and applying the selection now
    /// would only move the badge — nothing here re-queries — leaving the badge
    /// claiming a filter that isn't actually filtering the grid.
    /// </param>
    internal void ApplySavedFilter(CivitaiBrowserFilterData data, bool applyBaseModelSelection = true)
    {
        ShowInstalled = data.ShowInstalled ?? true;
        ShowEarlyAccess = data.ShowEarlyAccess ?? true;
        ShowPaywalled = data.ShowPaywalled ?? true;
        ShowNsfw = data.ShowNsfw ?? true;

        if (!applyBaseModelSelection) return;

        var wanted = (data.SelectedBaseModels ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        _suppressBaseModelFilterEvents = true;
        try
        {
            foreach (var item in AvailableBaseModels)
            {
                item.IsSelected = wanted.Remove(item.BaseModelRaw);
            }
        }
        finally
        {
            _suppressBaseModelFilterEvents = false;
        }

        foreach (var name in wanted) _stickyBaseModelSelections.Add(name);

        OnPropertyChanged(nameof(IsBaseModelFilterActive));
        OnPropertyChanged(nameof(ActiveBaseModelFilterCount));
        OnPropertyChanged(nameof(CanReset));
        RebuildFlyoutBaseModels();
    }

    /// <summary>
    /// Persists the current filter (base-model selection + the four Show flags) to
    /// AppSettings. Single slot — saving overwrites the previous filter. Restored
    /// automatically the next time the browser opens (<see cref="EnsureLoadedAsync"/>).
    /// </summary>
    [RelayCommand]
    private async Task SaveFilterAsync()
    {
        if (_settingsService is null) return;
        try
        {
            var json = JsonSerializer.Serialize(CaptureFilter());
            await UseSettingsServiceAsync(async s =>
            {
                await s.SetCivitaiBrowserFilterJsonAsync(json).ConfigureAwait(false);
                return true;
            });
            StatusMessage = "Filter saved.";
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.General, "CivitaiBrowser", $"Could not save filter: {ex.Message}");
            StatusMessage = "Could not save the filter.";
        }
    }

    /// <summary>
    /// Loads the saved filter from AppSettings and applies it. Called by
    /// <see cref="EnsureLoadedAsync"/> before the deferred initial search — the base-model
    /// selection feeds <see cref="BuildQuery"/>, so a restore landing after that search would
    /// issue a query for the wrong filter. Corrupt or missing data degrades silently to the
    /// unfiltered default (all four Show flags stay ticked).
    /// </summary>
    private async Task RestoreSavedFilterAsync()
    {
        if (_settingsService is null) return;
        try
        {
            var json = await UseSettingsServiceAsync(s => s.GetCivitaiBrowserFilterJsonAsync())
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return;

            var data = JsonSerializer.Deserialize<CivitaiBrowserFilterData>(json);
            if (data is null) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // The settings read above can take long enough for the user to start
                // their own search first (typed, changed a filter, hit Refresh —
                // anything that reaches SearchAsync, which sets _searchStarted). If
                // that happened, BuildQuery already ran for that search without the
                // restored base models — applying the selection now would only move
                // the badge, not the grid, since nothing here re-queries. Rather than
                // force a second search out from under the user (the exact
                // clobbering SearchIfNotAlreadyStartedAsync exists to prevent — see
                // its own doc comment) or leave the badge lying about what's actually
                // filtering the grid, skip the base-model half and keep the two
                // consistent: no selection applied, no badge claiming one. The four
                // Show flags still apply either way — they act on the already-fetched
                // Results directly, so they stay correct regardless of this race.
                var userAlreadySearched = Volatile.Read(ref _searchStarted) == 1;
                if (userAlreadySearched)
                {
                    _logger?.Debug(LogCategory.Network, "CivitaiBrowser",
                        "Restore raced a user-started search — applying the Show flags only, " +
                        "leaving the base-model selection as the user's own search left it.");
                }
                ApplySavedFilter(data, applyBaseModelSelection: !userAlreadySearched);
            });
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.General, "CivitaiBrowser", $"Could not restore saved filter: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a settings operation on a fresh scoped <see cref="IAppSettingsService"/> when DI
    /// is available. The constructor-injected instance is shared with other startup tasks
    /// (destination folders, the Installed tab's own filter restore) over a single
    /// non-thread-safe DbContext, so the save/restore paths here — which can run concurrently
    /// with them — must not reuse it. Falls back to the injected instance without DI
    /// (design-time/tests). Mirrors <c>LoraViewerViewModel.UseSettingsServiceAsync</c>.
    /// </summary>
    private async Task<T?> UseSettingsServiceAsync<T>(Func<IAppSettingsService, Task<T>> action)
    {
        var scopeFactory = App.Services?.GetService<IServiceScopeFactory>();
        if (scopeFactory is not null)
        {
            using var scope = scopeFactory.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<IAppSettingsService>())
                .ConfigureAwait(false);
        }

        if (_settingsService is not null)
            return await action(_settingsService).ConfigureAwait(false);

        return default;
    }

    /// <summary>
    /// Runs the deferred initial search unless the user has already triggered one of their own
    /// (typed, changed a filter, hit Refresh) by the time this gets a chance to run.
    /// <see cref="SearchAsync"/> has no re-entrancy guard — it unconditionally cancels whatever
    /// search is in flight and clears <see cref="Results"/> — so letting this fire after a
    /// user-started search would cancel and wipe out results the user is already looking at,
    /// for no benefit (the deferred search asks for nothing the user's own search doesn't
    /// already cover on first load). <see cref="_searchStarted"/> is set by
    /// <see cref="SearchAsync"/> itself, so whichever of the two reaches it first wins.
    /// </summary>
    private Task SearchIfNotAlreadyStartedAsync() =>
        Interlocked.CompareExchange(ref _searchStarted, 1, 0) == 0
            ? SearchAsync()
            : Task.CompletedTask;

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

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanReset));
        if (_initialized && !_suppressFilterChangeSearch) _ = DebouncedSearchAsync();
    }

    // Debounced like the text box: a user comparing two sorts or three periods used to spend a
    // full paginated search on each intermediate choice.
    partial void OnSelectedSortChanged(string? value) => OnQueryOptionChanged();
    partial void OnSelectedPeriodChanged(CivitaiPeriod value) => OnQueryOptionChanged();
    partial void OnSelectedModelTypeChanged(ModelTypeOption? value) => OnQueryOptionChanged();

    /// <summary>
    /// Sort/Period/Model-type changed. The re-search itself stays debounced (see
    /// <see cref="DebouncedSearchAsync"/>) — comparing two sorts or three periods shouldn't cost a
    /// full paginated search per intermediate choice — but the cursor must not survive the 400ms
    /// debounce window: <see cref="LoadMoreAsync"/> and the auto-load-more in
    /// <see cref="MaybeTopUpVisibleResults"/> both read <see cref="HasMore"/> synchronously, and
    /// either could otherwise paginate the OLD query's cursor under the NEW filter before the
    /// debounced <see cref="SearchAsync"/> gets a chance to reset it. Clearing it here, synchronously
    /// in the hook, closes that window without touching the debounce.
    /// <para>
    /// <see cref="_suppressFilterChangeSearch"/> gates ONLY the search below, not the cursor
    /// clear/<see cref="HasMore"/> raise above — <see cref="ResetFilter"/> holds that flag for
    /// the whole reset (so three property writes don't fire three searches) but the stale
    /// cursor from before the reset must still be invalidated synchronously, same as any other
    /// query-option change.
    /// </para>
    /// </summary>
    private void OnQueryOptionChanged()
    {
        if (!_initialized) return; // skip the constructor-time cascade
        _nextCursor = null;
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(CanReset));
        if (_suppressFilterChangeSearch) return;
        _ = DebouncedSearchAsync();
    }
    // All four Show-flyout toggles are client-side filters (the API call always
    // requests the full unfiltered set). No re-search needed when toggled — just
    // reapply local filters and refresh the flyout's count badge.
    partial void OnShowInstalledChanged(bool value) => OnShowFilterChanged();
    partial void OnShowEarlyAccessChanged(bool value) => OnShowFilterChanged();
    partial void OnShowPaywalledChanged(bool value) => OnShowFilterChanged();
    partial void OnShowNsfwChanged(bool value) => OnShowFilterChanged();

    private void OnShowFilterChanged()
    {
        OnPropertyChanged(nameof(ActiveShowFilterCount));
        OnPropertyChanged(nameof(IsShowFilterActive));
        OnPropertyChanged(nameof(CanReset));
        ApplyClientSideFilters();
    }

    #endregion

    private const int TargetVisibleCount = 50;
    private const int MaxAutoPaginateIterations = 10;

    /// <summary>
    /// Items requested per API page — Civitai's documented maximum.
    /// <para>
    /// Not merely "bigger is better": the API applies <c>query=</c> (relevance search)
    /// first to build a page window, then post-filters that window by <c>baseModels=</c>.
    /// A text search combined with a base-model filter therefore yields only the
    /// survivors of each window, so a 50-item window can come back with two items —
    /// measured 2026-08-29 for <c>query=latex</c>: 20/page for Illustrious, 12 for Pony,
    /// 2 for Flux.1 D, against a full 50 for the same filter with no search term. The
    /// auto-paginate loop then burned all <see cref="MaxAutoPaginateIterations"/>
    /// iterations (~7 s once each request is spaced by the gateway's interactive pace)
    /// and still showed a near-empty grid.
    /// </para>
    /// <para>
    /// Doubling the window roughly triples the yield (45 and 6 respectively for the
    /// cases above) for ~165 ms of extra transfer, which is far cheaper than the
    /// additional paced round trips it saves.
    /// </para>
    /// </summary>
    private const int PageSize = 100;

    /// <summary>
    /// When client-side filters drop the visible count below this floor, the VM
    /// quietly kicks off a Load-more so the user doesn't see a half-empty grid
    /// after toggling any of the "Show" flyout's four filters (Installed / Early
    /// Access / Paywalled / NSFW).
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
                    // Re-check here, not just before the await: a cancellation can land after
                    // the pre-dispatch check passed but before this callback actually runs on
                    // the UI thread (SearchAsync has no re-entrancy guard and cancels whatever
                    // is in flight), and without this the cancelled pipeline's stale response
                    // still lands in Results right alongside — or ahead of — the search that
                    // superseded it.
                    if (ct.IsCancellationRequested) return;

                    foreach (var model in response.Items)
                    {
                        if (model.ModelVersions.Count == 0) continue;
                        if (model.Mode is not null) continue; // skip archived/taken-down
                        if (!existingIds.Add(model.Id)) continue;
                        Results.Add(CreateResultCard(model));
                    }
                });
                if (ct.IsCancellationRequested) return;

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
    /// Builds one search-result card, wired identically regardless of which loop (primary
    /// search or tag-fallback) discovered the model. A prior bug wired only
    /// <see cref="CivitaiResultViewModel.EnqueueAllVersionsHandler"/> on the primary-search
    /// path — "Add selected to queue" silently no-oped on almost every card, since tag-fallback
    /// results are a small minority (spec RC5). Sharing this factory between both loops means
    /// they can no longer drift apart the same way again.
    /// </summary>
    private CivitaiResultViewModel CreateResultCard(CivitaiModel model)
    {
        var vm = new CivitaiResultViewModel(model, ShowNsfw)
        {
            EnqueueAllVersionsHandler = EnqueueAllVersionsForCard,
            EnqueueSelectedVersionsHandler = EnqueueSelectedVersionsForCard
        };
        vm.ApplyInstalledIndex(_installed);
        vm.SelectionChanged += OnResultSelectionChanged;
        return vm;
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
                // See the matching check in LoadNextAsync's own dispatch: cancellation can
                // land after the pre-dispatch check passed but before this callback runs.
                if (ct.IsCancellationRequested) return;

                foreach (var model in tagResponse.Items)
                {
                    if (model.ModelVersions.Count == 0) continue;
                    if (model.Mode is not null) continue; // skip archived/taken-down
                    if (!existingIds.Add(model.Id)) continue;
                    Results.Add(CreateResultCard(model));
                }
            });
            if (ct.IsCancellationRequested) return;

            // The merged cards default to visible — without a filter pass here, the
            // Show flyout's toggles (Installed / Early Access / Paywalled / NSFW)
            // were silently ignored for every tag-fallback result (user-reported:
            // named searches with those toggles active showed hidden categories anyway).
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
            // filter client-side via ShowNsfw. Sending
            // nsfw=false here strips them at the server and they can't come back.
            Nsfw = "true",
            BaseModels = baseModels,
            Limit = PageSize,
            Cursor = cursor
        };
    }

    [RelayCommand]
    private void ClearBaseModelFilters()
    {
        // Suppress OnBaseModelFilterToggled while clearing: unguarded, each of the N
        // deselections fires its own SearchAsync, and each one cancels the last — N
        // wasted requests instead of the one explicit search below.
        _suppressBaseModelFilterEvents = true;
        try
        {
            foreach (var item in AvailableBaseModels) item.IsSelected = false;
        }
        finally
        {
            _suppressBaseModelFilterEvents = false;
        }

        // Also clear the sticky set — not just the live items. ApplySavedFilter parks
        // names the mirror doesn't contain yet, and CaptureFilter now persists the
        // whole sticky set (see FoldLiveBaseModelSelectionsIntoSticky), so leaving a
        // pending name in there would let a Save right after "Clear all" silently
        // re-persist a base model the user just explicitly cleared — it would then
        // reselect itself and start filtering results again the moment the source
        // materializes it. Mirrors LoraViewerViewModel.ClearBaseModelFilters's
        // `_pendingRestoredSelections = null;`.
        _stickyBaseModelSelections.Clear();

        OnPropertyChanged(nameof(IsBaseModelFilterActive));
        OnPropertyChanged(nameof(ActiveBaseModelFilterCount));
        OnPropertyChanged(nameof(CanReset));
        if (!string.IsNullOrWhiteSpace(BaseModelFilterSearchText))
            RebuildFlyoutBaseModels();
        _ = SearchAsync();
    }

    /// <summary>
    /// Returns the ENTIRE filter bar to the constructor's defaults in one click: search text,
    /// the four Show toggles, the base-model selection — including
    /// <see cref="_stickyBaseModelSelections"/>, same as <see cref="ClearBaseModelFilters"/>, so a
    /// parked/not-yet-materialized name doesn't survive an explicit reset invisibly and start
    /// re-filtering results once it does materialize — and Sort/Period/Model type.
    /// <para>
    /// Live view only. Does NOT touch the saved filter: no call to <see cref="SaveFilterAsync"/>,
    /// no settings write. The persisted filter (<see cref="RestoreSavedFilterAsync"/> reads it back
    /// on next open) stays exactly as it was until the user explicitly hits Save again.
    /// </para>
    /// <para>
    /// Several of the properties reset here independently kick off a search when written one at a
    /// time: <see cref="OnSearchTextChanged"/> and <see cref="OnQueryOptionChanged"/> (Sort/Period/
    /// Model type) both debounce into <see cref="SearchAsync"/>, and each base-model item's
    /// <see cref="OnBaseModelFilterToggled"/> searches immediately. Both suppression flags —
    /// <see cref="_suppressBaseModelFilterEvents"/> (already used by <see cref="ClearBaseModelFilters"/>)
    /// and <see cref="_suppressFilterChangeSearch"/> (its equivalent for the property hooks) — are
    /// held for the whole reset, and exactly one explicit <see cref="SearchAsync"/> fires at the
    /// end, mirroring <see cref="ClearBaseModelFilters"/>'s own pattern.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void ResetFilter()
    {
        _suppressBaseModelFilterEvents = true;
        _suppressFilterChangeSearch = true;

        // A search text or query-option change made just before Reset (but within its 400ms
        // debounce window) leaves a pending DebouncedSearchAsync timer. Suppressing this reset's
        // OWN property writes never touches it — DebouncedSearchAsync only cancels the timer IT
        // schedules, and a suppressed hook never calls it — so without cancelling here that stale
        // timer would still fire its own SearchAsync up to 400ms later, alongside the explicit one
        // below: two searches instead of one.
        _debounceCts?.Cancel();

        try
        {
            SearchText = string.Empty;

            ShowInstalled = true;
            ShowEarlyAccess = true;
            ShowPaywalled = true;
            ShowNsfw = true;

            foreach (var item in AvailableBaseModels) item.IsSelected = false;
            _stickyBaseModelSelections.Clear();

            SelectedSort = CivitaiModelSort.Newest;
            SelectedPeriod = CivitaiPeriod.AllTime;
            SelectedModelType = _defaultModelType;
        }
        finally
        {
            _suppressBaseModelFilterEvents = false;
            _suppressFilterChangeSearch = false;
        }

        OnPropertyChanged(nameof(IsBaseModelFilterActive));
        OnPropertyChanged(nameof(ActiveBaseModelFilterCount));
        OnPropertyChanged(nameof(ActiveShowFilterCount));
        OnPropertyChanged(nameof(IsShowFilterActive));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(CanReset));
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

    /// <summary>
    /// True while a batch restore (<see cref="ApplySavedFilter"/>) is writing directly
    /// to <see cref="BaseModelFilterItem.IsSelected"/>. Without this guard each restored
    /// selection would fire <see cref="OnBaseModelFilterToggled"/> — which unconditionally
    /// re-searches — kicking off one Civitai request per restored base model instead of
    /// letting <see cref="EnsureLoadedAsync"/> run the single deferred search afterwards.
    /// </summary>
    private bool _suppressBaseModelFilterEvents;

    /// <summary>
    /// True while <see cref="ResetFilter"/> is writing <see cref="SearchText"/>,
    /// <see cref="SelectedSort"/>, <see cref="SelectedPeriod"/> and <see cref="SelectedModelType"/>
    /// back to their defaults. Gates only the search each of those properties' change hook would
    /// otherwise fire (<see cref="OnSearchTextChanged"/>'s <see cref="DebouncedSearchAsync"/> call,
    /// <see cref="OnQueryOptionChanged"/>'s) and the auto-top-up in
    /// <see cref="MaybeTopUpVisibleResults"/> — never the cursor clear / <see cref="HasMore"/>
    /// raise in <see cref="OnQueryOptionChanged"/>, which must still run synchronously for a reset
    /// exactly like any other query-option change. The equivalent of
    /// <see cref="_suppressBaseModelFilterEvents"/> for this half of the filter bar.
    /// </summary>
    private bool _suppressFilterChangeSearch;

    /// <summary>
    /// Folds each mirror item's live <see cref="BaseModelFilterItem.IsSelected"/> state
    /// into <see cref="_stickyBaseModelSelections"/>: selected names are added, unselected
    /// ones removed. Used both before <see cref="RebuildBaseModelMirror"/> wipes the item
    /// list (so the wipe doesn't lose the user's latest toggles) and when
    /// <see cref="CaptureFilter"/> snapshots the filter for persistence (so a save between
    /// rebuilds reflects those same latest toggles).
    /// </summary>
    private void FoldLiveBaseModelSelectionsIntoSticky()
    {
        foreach (var b in AvailableBaseModels)
        {
            if (b.IsSelected) _stickyBaseModelSelections.Add(b.BaseModelRaw);
            else _stickyBaseModelSelections.Remove(b.BaseModelRaw);
        }
    }

    private void RebuildBaseModelMirror()
    {
        // Fold the live item states into the sticky set: names currently listed
        // follow their checkbox; names absent from the current mirror keep their
        // remembered state until they rematerialize.
        FoldLiveBaseModelSelectionsIntoSticky();

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
        OnPropertyChanged(nameof(CanReset));
        RebuildFlyoutBaseModels();
    }

    private void OnBaseModelSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(RebuildBaseModelMirror);
    }

    private void OnBaseModelFilterToggled(object? sender, EventArgs e)
    {
        if (_suppressBaseModelFilterEvents) return;

        OnPropertyChanged(nameof(IsBaseModelFilterActive));
        OnPropertyChanged(nameof(ActiveBaseModelFilterCount));
        OnPropertyChanged(nameof(CanReset));

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
            result.ApplyNsfwPreference(ShowNsfw);

            // Each toggle hides exactly what its badge marks. Early Access and
            // Paywalled are deliberately disjoint (ShowEarlyAccessBadge is already
            // IsEarlyAccess && !IsPermanentlyPaid) — filtering on the raw IsEarlyAccess
            // flag here would make the Paywalled toggle a no-op for permanently-paid
            // cards, since they'd already be caught by Early Access.
            var hide = (!ShowInstalled && result.IsInstalled)
                       || (!ShowEarlyAccess && result.ShowEarlyAccessBadge)
                       || (!ShowPaywalled && result.IsPermanentlyPaid)
                       || (!ShowNsfw && result.IsNsfw);
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
    /// Also skipped while <see cref="_suppressFilterChangeSearch"/> is held: a Show-flag write
    /// mid-<see cref="ResetFilter"/> could otherwise sneak in an unwanted API call — via this
    /// auto-top-up, not the debounced search path — ahead of the reset's own single explicit
    /// search, if a stale cursor/low visible-count happened to still be in effect from before
    /// the reset started.
    /// </summary>
    private void MaybeTopUpVisibleResults()
    {
        if (!_initialized) return;       // skip the constructor-time cascade
        if (_suppressFilterChangeSearch) return; // ResetFilter is mid-flight; its own search covers this
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
    /// Enqueues only the ticked rows of one card's version picker. Unlike the grid-wide
    /// "Add selection to queue" this ignores whether the card itself is selected — the
    /// user is acting on the picker in front of them.
    /// </summary>
    private void EnqueueSelectedVersionsForCard(CivitaiResultViewModel card)
    {
        var pairs = card.Versions.Where(v => v.IsSelected).Select(v => (card, v)).ToList();
        if (pairs.Count == 0) return;
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
            // disabled source no longer counts as "installed" for the Show flyout's
            // Installed toggle / the card's Installed badge. Mirrors the Installed
            // tab's filtering rule.
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

    private Task<string?> GetApiKeyAsync()
    {
        _apiKeyProvider ??= CivitaiApiKeys.Resolve(fallbackSettings: _settingsService);
        return _apiKeyProvider.GetApiKeyAsync();
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
