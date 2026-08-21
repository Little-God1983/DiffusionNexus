using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Infrastructure;
using DiffusionNexus.Service.Services;
using DiffusionNexus.Service.Services.IO;
using DiffusionNexus.Service.Services.Sync;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.Services.Lora.Sorting;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the LoRA Viewer view displaying model tiles.
/// </summary>
public partial class LoraViewerViewModel : BusyViewModelBase
{
    private readonly IAppSettingsService? _settingsService;
    private readonly IModelSyncService? _syncService;
    private readonly ICivitaiClient? _civitaiClient;
    private readonly ICivitaiBaseModelCatalog? _baseModelCatalog;
    private readonly ISecureStorage? _secureStorage;
    private readonly IUnifiedLogger? _logger;
    private readonly ILoraUpdateChecker? _updateChecker;

    /// <summary>
    /// The metadata sync pipeline (#521 WP2). The viewer plans a run, shows the outcome and
    /// rebuilds its grid; selecting, hashing, calling Civitai and persisting all live in the
    /// service, which is why this ViewModel no longer owns any sync phase of its own.
    /// </summary>
    private readonly ILibrarySyncService? _librarySync;

    /// <summary>UI-thread marshalling seam (#437). Null only in the design-time ctor.</summary>
    private readonly IUiScheduler? _uiScheduler;

    /// <summary>
    /// Scope factory for the fresh-<c>DbContext</c> reads (tile rebuild, API key, sorter
    /// file list). Injected so those paths are reachable in tests; falls back to
    /// <c>App.Services</c> exactly as the direct locator calls did.
    /// </summary>
    private readonly IServiceScopeFactory? _scopeFactory;

    /// <summary>
    /// Cancels the in-flight update-check batch when the visible tile set changes
    /// (filter edits, refreshes, page navigation). A fresh token is issued
    /// every time a new batch starts.
    /// </summary>
    private CancellationTokenSource? _updateCheckCts;

    /// <summary>Cancels the in-flight "Download Metadata" sync; null when idle.</summary>
    private CancellationTokenSource? _metadataSyncCts;

    /// <summary>
    /// Debounces search-text keystrokes. Every edit cancels the previous pending
    /// filter pass and schedules a new one, so <see cref="ApplyFilters"/> (which
    /// rebuilds the visible tile window — too expensive per keystroke) runs once
    /// after typing pauses instead of blocking the UI thread on every character.
    /// </summary>
    private CancellationTokenSource? _searchDebounceCts;

    /// <summary>Delay between the last keystroke and the debounced filter pass. Internal so tests can shorten it.</summary>
    internal TimeSpan SearchDebounceInterval { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>The pending debounced filter pass, exposed so tests can await it instead of sleeping.</summary>
    internal Task? SearchDebounceTask { get; private set; }

    #region Observable Properties

    /// <summary>
    /// Search text for filtering models.
    /// </summary>
    [ObservableProperty]
    private string? _searchText;

    /// <summary>
    /// Whether to show NSFW models.
    /// </summary>
    [ObservableProperty]
    private bool _showNsfw = true;

    /// <summary>
    /// Search text typed inside the base-model flyout. Narrows the visible option
    /// list (<see cref="FlyoutBaseModels"/>) only — selections are untouched.
    /// </summary>
    [ObservableProperty]
    private string? _baseModelFilterSearchText;

    /// <summary>
    /// When true, the base-model flyout lists only base models actually present
    /// among the installed LoRAs (plus "Unknown" when placeholder tiles exist).
    /// Off by default.
    /// </summary>
    [ObservableProperty]
    private bool _onlyInstalledBaseModels;

    /// <summary>
    /// Sort options offered in the Installed-tab "Sort by" dropdown. The record's
    /// <c>ToString</c> returns its label, so the ComboBox renders it without a template.
    /// </summary>
    public IReadOnlyList<LoraSortOption> SortOptions { get; } =
    [
        new LoraSortOption("Name", LoraSortField.Name),
        new LoraSortOption("Date added", LoraSortField.DateAdded),
    ];

    /// <summary>
    /// Currently selected sort field for the Installed tab. Defaults to Name.
    /// (The historical default was database insertion order — roughly date-added
    /// ascending — which this dropdown now makes explicit and switchable.)
    /// </summary>
    [ObservableProperty]
    private LoraSortOption _selectedSortOption;

    /// <summary>
    /// Sort direction for the Installed tab. <c>false</c> = ascending (A→Z / oldest
    /// first), <c>true</c> = descending (Z→A / newest first).
    /// </summary>
    [ObservableProperty]
    private bool _sortDescending;

    /// <summary>
    /// Currently selected model tile.
    /// </summary>
    [ObservableProperty]
    private ModelTileViewModel? _selectedTile;

    /// <summary>
    /// Total model count.
    /// </summary>
    [ObservableProperty]
    private int _totalModelCount;

    /// <summary>
    /// Filtered model count.
    /// </summary>
    [ObservableProperty]
    private int _filteredModelCount;

    /// <summary>
    /// Status message for sync progress.
    /// </summary>
    [ObservableProperty]
    private string? _syncStatus;

    /// <summary>
    /// True only while a metadata-download sync is running. Drives the Cancel button
    /// in the busy overlay so it does not appear during the (non-cancellable) Refresh.
    /// </summary>
    [ObservableProperty]
    private bool _isCancellable;

    /// <summary>
    /// Whether any base model filter is currently active (for visual indicator on the filter button).
    /// Includes the "Unknown" sentinel.
    /// </summary>
    public bool IsBaseModelFilterActive
        => UnknownBaseModelItem.IsSelected || AvailableBaseModels.Any(f => f.IsSelected);

    /// <summary>
    /// Count of currently active base model filters (including the "Unknown" sentinel).
    /// </summary>
    public int ActiveBaseModelFilterCount
        => AvailableBaseModels.Count(f => f.IsSelected) + (UnknownBaseModelItem.IsSelected ? 1 : 0);

    /// <summary>
    /// Whether the detail panel is open.
    /// </summary>
    [ObservableProperty]
    private bool _isDetailOpen;

    /// <summary>
    /// ViewModel for the detail panel.
    /// </summary>
    [ObservableProperty]
    private ModelDetailViewModel? _detailViewModel;

    #endregion

    #region Collections

    /// <summary>
    /// All model tiles.
    /// </summary>
    public ObservableCollection<ModelTileViewModel> AllTiles { get; } = [];

    /// <summary>
    /// The filtered, sorted tile set bound to the view. The view renders it through a
    /// virtualizing <c>ItemsRepeater</c> (<c>UniformGridLayout</c>), so only the tiles
    /// inside the scroll viewport are realized — the full set can hold thousands of
    /// tiles without the UI thread paying to materialize (or load thumbnails for) them
    /// all. Each realized <see cref="ModelTileViewModel"/> loads its thumbnail on
    /// <see cref="ModelTileViewModel.Activate"/> and releases it on
    /// <see cref="ModelTileViewModel.Deactivate"/> as its container recycles.
    /// </summary>
    public BatchObservableCollection<ModelTileViewModel> FilteredTiles { get; } = [];

    /// <summary>
    /// Distinct base model names available for filtering, built from all tiles.
    /// </summary>
    public ObservableCollection<BaseModelFilterItem> AvailableBaseModels { get; } = [];

    /// <summary>Display label of the "Unknown" pseudo base model.</summary>
    public const string UnknownBaseModelLabel = "Unknown";

    /// <summary>
    /// Sentinel filter item matching tiles whose base model is the "???" placeholder
    /// (local files without metadata). Owned by the Installed tab only — it is NEVER
    /// added to <see cref="AvailableBaseModels"/>, which the Civitai browser mirrors
    /// and whose entries are sent to the Civitai API.
    /// </summary>
    public BaseModelFilterItem UnknownBaseModelItem { get; } = new(UnknownBaseModelLabel);

    /// <summary>
    /// The option list the Installed tab's flyout renders: "Unknown" first, then the
    /// shared <see cref="AvailableBaseModels"/> items, narrowed by
    /// <see cref="BaseModelFilterSearchText"/> and <see cref="OnlyInstalledBaseModels"/>.
    /// Holds the SAME item instances as the shared list, so toggling a checkbox here
    /// drives the same selection state the filter pipeline and the browser mirror use.
    /// </summary>
    public BatchObservableCollection<BaseModelFilterItem> FlyoutBaseModels { get; } = [];

    /// <summary>
    /// Cached catalog labels (full Civitai base-model list). When non-empty, drives
    /// <see cref="RebuildAvailableBaseModels"/> instead of the distinct-from-installed
    /// fallback. Populated by <see cref="LoadBaseModelCatalogAsync"/>.
    /// </summary>
    private IReadOnlyList<string>? _catalogBaseModels;

    /// <summary>
    /// Distinct non-placeholder base models across installed tiles, cached so the
    /// flyout narrowing doesn't rescan every tile version on each keystroke.
    /// Rebuilt in <see cref="RebuildAvailableBaseModels"/> (all tile-change paths).
    /// </summary>
    private readonly HashSet<string> _installedBaseModels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether any installed tile has a placeholder ("???") base model.</summary>
    private bool _hasUnknownInstalled;

    /// <summary>
    /// Saved-filter names that didn't exist in <see cref="AvailableBaseModels"/> when the
    /// filter was restored (stale catalog, offline start). Reconciled — and consumed — by
    /// the next <see cref="RebuildAvailableBaseModels"/> that surfaces them, and included
    /// in <see cref="CaptureFilter"/> so re-saving never truncates the saved intent.
    /// </summary>
    private HashSet<string>? _pendingRestoredSelections;

    /// <summary>
    /// Batches multi-item selection changes (clear/reset/restore): while set,
    /// <see cref="OnBaseModelFilterChanged"/> is a no-op and the batch operation raises
    /// the indicator properties and runs <see cref="ApplyFilters"/> once at the end.
    /// </summary>
    private bool _suppressBaseModelFilterEvents;

    /// <summary>Saved-filter names awaiting a list rebuild that contains them. Test seam.</summary>
    internal IReadOnlyCollection<string>? PendingRestoredBaseModels => _pendingRestoredSelections;

    /// <summary>
    /// Upper bound on how many tiles the passive "new version available" check looks
    /// at per filter pass. With the virtualizing grid the view no longer tracks a
    /// scroll window, so this simply caps the opportunistic background check at the
    /// same magnitude as the old visible window — preventing a request storm (and
    /// Civitai rate-limiting) for libraries with thousands of LoRAs. The explicit
    /// "Download Metadata" flow remains the way to sync the entire library.
    /// </summary>
    private const int PassiveUpdateCheckLimit = 200;

    #endregion

    #region Constructors

    /// <summary>
    /// Design-time constructor with demo data.
    /// </summary>
    public LoraViewerViewModel()
    {
        _selectedSortOption = SortOptions[0];
        _settingsService = null;
        _syncService = null;
        _civitaiClient = null;
        _secureStorage = null;
        _logger = null;
        _baseModelCatalog = null;
        _updateChecker = null;
        _librarySync = null;
        _uiScheduler = null;
        _scopeFactory = null;
        UnknownBaseModelItem.SelectionChanged += OnBaseModelFilterChanged;
        BrowserViewModel = new CivitaiBrowserViewModel();
        SorterViewModel = new LoraSorterViewModel();
        // Load demo data for design-time preview
        LoadDemoData();
    }

    /// <summary>
    /// Runtime constructor with DI.
    /// </summary>
    public LoraViewerViewModel(
        IAppSettingsService settingsService,
        IModelSyncService syncService,
        ICivitaiClient? civitaiClient = null,
        ISecureStorage? secureStorage = null,
        IUnifiedLogger? logger = null,
        ICivitaiBaseModelCatalog? baseModelCatalog = null,
        ILoraUpdateChecker? updateChecker = null,
        ILibrarySyncService? librarySync = null,
        IUiScheduler? uiScheduler = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _selectedSortOption = SortOptions[0];
        UnknownBaseModelItem.SelectionChanged += OnBaseModelFilterChanged;
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _civitaiClient = civitaiClient;
        _secureStorage = secureStorage;
        _logger = logger;
        _baseModelCatalog = baseModelCatalog;
        _updateChecker = updateChecker;
        // Optional collaborators: injected by the DI factory, resolved from the locator only as
        // a fallback so a hand-constructed viewer (tools, tests) still behaves like the real one.
        _librarySync = librarySync ?? App.Services?.GetService<ILibrarySyncService>();
        _uiScheduler = uiScheduler ?? App.Services?.GetService<IUiScheduler>();
        _scopeFactory = scopeFactory ?? App.Services?.GetService<IServiceScopeFactory>();

        // Live-update the base-model filter whenever the catalog is force-refreshed
        // (e.g. from the "Update base-model filter" button in Settings). Both the
        // Installed and Browse Civitai filters share AvailableBaseModels, so a single
        // rebuild covers both tabs without requiring an app restart.
        if (_baseModelCatalog is not null)
        {
            _baseModelCatalog.StatusChanged += OnBaseModelCatalogStatusChanged;
        }

        // Civitai browser sub-tab. Reuses the same ICivitaiClient and settings service.
        // The base-model filter list is mirrored from AvailableBaseModels which is itself
        // sourced from the full Civitai catalog (with distinct-from-installed as fallback).
        var downloadService = App.Services?.GetService<LoraDownloadService>();
        var dialogService = App.Services?.GetService<IDialogService>();
        var destination = new DownloadDestinationViewModel(dialogService);
        var queue = new CivitaiDownloadQueue(downloadService, _logger, _civitaiClient, destination);
        var waitlist = new CivitaiWaitlist(_civitaiClient, _logger);
        BrowserViewModel = new CivitaiBrowserViewModel(_civitaiClient, _settingsService, _logger, queue, waitlist, AvailableBaseModels);

        // LoRA Sorter sub-tab. Same DB-backed source of truth as the Installed tab;
        // disk seams are the production implementations.
        SorterViewModel = _scopeFactory is null
            ? new LoraSorterViewModel()
            : new LoraSorterViewModel(
                _settingsService, _syncService, _logger,
                new DbLocalPathUpdater(_scopeFactory, _logger),
                new SorterMetadataResolver(_civitaiClient, GetApiKeyForSorterAsync,
                    SorterMetadataResolver.DefaultCacheDirectory, FileHasher.Sha256Upper, _logger),
                new FileOperations(),
                DiskUtility.GetAvailableSpace,
                FileHasher.Sha256Upper,
                File.Exists,
                SortHistoryWriter.DefaultHistoryDirectory,
                loadCachedFiles: LoadCachedFilesForSorterAsync);
        SorterViewModel.SortCompleted += (_, _) => _ = RefreshAsync();

        _ = InitializeBaseModelFilterAsync();
        _ = LoadDestinationFoldersAsync(destination);
    }

    /// <summary>
    /// View model for the "Browse Civitai" sub-tab.
    /// </summary>
    public CivitaiBrowserViewModel BrowserViewModel { get; }

    /// <summary>View model for the "LoRA Sorter" sub-tab.</summary>
    public LoraSorterViewModel SorterViewModel { get; }

    #endregion

    #region Commands

    /// <summary>
    /// Refresh the model list, database-first: show the cached tiles from the catalog DB
    /// immediately (a lightweight projection query + in-memory grouping — no filesystem
    /// walk), so the grid paints in well under a second even for thousands of LoRAs. The
    /// slow work — scanning the source folders for new files and verifying that existing
    /// files still exist on disk — then runs in the background via
    /// <see cref="ReconcileLibraryInBackgroundAsync"/>, which rebuilds the grid only if it
    /// finds a change (a new file appears, a deleted file drops out, a moved file relocates).
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Design-time or missing services fallback
        if (_syncService is null)
        {
            LoadDemoData();
            return;
        }

        var showedCachedTiles = false;
        try
        {
            IsBusy = true;
            BusyMessage = "Loading models...";
            SyncStatus = "Loading models from database...";

            // 1. Show whatever is already cached — instantly.
            var (uniqueModelCount, tiles) = await Task.Run(LoadCachedTilesAsync);

            if (tiles.Count > 0)
            {
                ReplaceTiles(tiles);
                SyncStatus = $"Loaded {uniqueModelCount} models ({AllTiles.Count} tiles)";
                showedCachedTiles = true;
            }
            else
            {
                // Empty cache (first run / never scanned): discover inline so the grid fills
                // in one pass instead of flashing the "No Models" empty state.
                SyncStatus = "Discovering models...";
                await Task.Run(async () =>
                {
                    await DiscoverNewFilesAsync();
                    await BackfillCivitaiModelPageIdAsync();
                });
                var (freshCount, freshTiles) = await Task.Run(LoadCachedTilesAsync);
                ReplaceTiles(freshTiles);
                SyncStatus = $"Loaded {freshCount} models ({AllTiles.Count} tiles)";
            }
        }
        catch (Exception ex)
        {
            SyncStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }

        // 2. Reconcile against disk in the background (adds + deletions + moves). When the
        //    cache was empty we already discovered inline above, so only verification remains.
        if (showedCachedTiles)
            _ = ReconcileLibraryInBackgroundAsync();
        else
            _ = VerifyFilesInBackgroundAsync();
    }

    /// <summary>
    /// Loads the installed-file rows from the catalog DB (a lightweight projection — no
    /// thumbnail BLOBs, no filesystem access) and groups them into per-location tiles
    /// (issue #380: one tile per (Model, LoRA-source root)). Runs on the thread pool.
    /// </summary>
    private async Task<(int UniqueModelCount, List<ModelTileViewModel> Tiles)> LoadCachedTilesAsync()
    {
        using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var freshSyncService = scope.ServiceProvider.GetRequiredService<IModelSyncService>();
        var files = await freshSyncService.LoadCachedFilesAsync();
        var distinctModels = files.Select(f => f.Model.Id).Distinct().Count();
        var tiles = BuildPerLocationTiles(files);
        return (distinctModels, tiles);
    }

    /// <summary>
    /// Swaps the tile set on the UI thread: unsubscribes the outgoing tiles, replaces
    /// <see cref="AllTiles"/> with <paramref name="tiles"/>, and refreshes the counts,
    /// base-model filter, and filtered view.
    /// </summary>
    private void ReplaceTiles(List<ModelTileViewModel> tiles)
    {
        foreach (var oldTile in AllTiles)
        {
            oldTile.Deleted -= OnTileDeleted;
            oldTile.DetailRequested -= OnTileDetailRequested;
        }

        AllTiles.Clear();
        foreach (var tile in tiles)
        {
            tile.Deleted += OnTileDeleted;
            tile.DetailRequested += OnTileDetailRequested;
            AllTiles.Add(tile);
        }

        TotalModelCount = AllTiles.Sum(t => t.ModelCount);
        RebuildAvailableBaseModels();
        ApplyFilters();
    }

    /// <summary>
    /// Background reconciliation against the filesystem: discovers newly added files and
    /// verifies existing ones (marking on-disk-deleted files invalid so they drop out of
    /// the grid, and relocating moved files). Rebuilds the grid from the DB only when
    /// something actually changed, so the common "nothing changed since last launch" case
    /// leaves the visible tiles (and the scroll position) untouched.
    /// </summary>
    private async Task ReconcileLibraryInBackgroundAsync()
    {
        try
        {
            _logger?.Info(LogCategory.General, "LoraReconcile", "Background reconcile started (discover + verify)");

            var (added, missing, moved) = await Task.Run(async () =>
            {
                var newCount = await DiscoverNewFilesAsync();
                await BackfillCivitaiModelPageIdAsync();

                using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
                var syncService = scope.ServiceProvider.GetRequiredService<IModelSyncService>();
                var progress = new Progress<SyncProgress>(p =>
                    Dispatcher.UIThread.Post(() =>
                        SyncStatus = p.Phase == "Verification complete" ? null : p.Phase));

                // MissingCount = files gone from disk (now filtered out of LoadCachedFilesAsync);
                // MovedCount = files whose path changed. Either, or a new file, changes the grid.
                var result = await syncService.VerifyAndSyncFilesAsync(progress);
                return (newCount, result.MissingCount, result.MovedCount);
            });

            var changed = added > 0 || missing > 0 || moved > 0;
            _logger?.Info(LogCategory.General, "LoraReconcile",
                $"Reconcile done: {added} new, {missing} missing (deleted from disk), {moved} moved → rebuild={changed}");

            if (changed)
                await RebuildTilesFromDatabaseAsync();
        }
        catch (Exception ex)
        {
            // Background work — a discovery/verify failure must not disrupt the visible grid.
            _logger?.Warn(LogCategory.General, "LoraReconcile", $"Reconcile failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Discover new files and add them to the database. Returns the number of new models
    /// discovered so callers can decide whether the grid needs rebuilding.
    /// Uses a fresh DI scope so the DbContext sees the latest committed data
    /// (avoids duplicates when files were already persisted by other operations).
    /// </summary>
    private async Task<int> DiscoverNewFilesAsync()
    {
        try
        {
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var syncService = scope.ServiceProvider.GetRequiredService<IModelSyncService>();

            var progress = new Progress<SyncProgress>(p =>
            {
                // Update status on UI thread
                Dispatcher.UIThread.Post(() =>
                {
                    SyncStatus = p.CurrentItem is not null
                        ? $"{p.Phase}: {p.CurrentItem}"
                        : p.Phase;
                });
            });

            var newModels = await syncService.DiscoverNewFilesAsync(progress);

            Dispatcher.UIThread.Post(() =>
            {
                SyncStatus = $"Discovered {newModels.Count} new files";
            });

            return newModels.Count;
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                SyncStatus = $"Discovery error: {ex.Message}";
            });

            return 0;
        }
    }

    /// <summary>
    /// Background task to verify file existence.
    /// Uses its own DI scope so the DbContext does not conflict with other
    /// concurrent operations on the shared application scope.
    /// </summary>
    private async Task VerifyFilesInBackgroundAsync()
    {
        try
        {
            // Progress<T> captures the UI SynchronizationContext here (UI thread),
            // so the callback is already marshaled — no nested Post needed.
            var progress = new Progress<SyncProgress>(p =>
            {
                if (p.Phase == "Verification complete")
                {
                    SyncStatus = null; // Clear status when done
                }
            });

            // The verify walks the library and SHA-hashes candidate files; its
            // "async" service continuations otherwise resume on the UI thread
            // (10s dispatcher hog on a cold cache — see 2026-07-15 startup trace).
            // Run the whole thing on the pool with its own scope.
            await Task.Run(async () =>
            {
                using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
                var syncService = scope.ServiceProvider.GetRequiredService<IModelSyncService>();
                await syncService.VerifyAndSyncFilesAsync(progress);
            });
        }
        catch
        {
            // Silently fail - this is background work
        }
    }

    /// <summary>
    /// Groups models so that different local files from the same LoRA appear as a single
    /// tile with multiple version buttons.
    /// Delegates to <see cref="TileGroupingHelper"/> for testability.
    /// </summary>
    private List<ModelTileViewModel> GroupModelsIntoTiles(IReadOnlyList<Model> allModels)
        => TileGroupingHelper.GroupModelsIntoTiles(allModels, BuildTileDependencies());

    /// <summary>
    /// Builds the dependency bundle each freshly-created <see cref="ModelTileViewModel"/>
    /// is constructed with (#438). Resolves from <c>App.Services</c> here — the tile no
    /// longer reaches into the locator itself. Returns an all-null bundle at design time
    /// (no <c>App.Services</c>), which is exactly how the tile behaved before.
    /// </summary>
    private ModelTileDependencies BuildTileDependencies()
    {
        var sp = App.Services;
        return new ModelTileDependencies(
            Logger: _logger,
            ScopeFactory: sp?.GetService<IServiceScopeFactory>(),
            DialogService: TryResolveDialogService(sp),
            VideoThumbnailService: sp?.GetService<IVideoThumbnailService>(),
            Clipboard: sp?.GetService<DiffusionNexus.Installer.SDK.Shared.Services.IClipboardService>(),
            UiScheduler: sp?.GetService<IUiScheduler>());
    }

    /// <summary>
    /// Resolves <see cref="IDialogService"/> defensively: its DI factory throws when the
    /// main window is not available yet. Tiles are normally built after the window exists,
    /// but guarding keeps an early / design-time build from throwing — the tile just gets
    /// a null dialog service (delete no-ops with a logged error) until the next rebuild.
    /// </summary>
    private static IDialogService? TryResolveDialogService(IServiceProvider? sp)
    {
        try { return sp?.GetService<IDialogService>(); }
        catch { return null; }
    }

    /// <summary>
    /// Runs a full library metadata sync through <see cref="ILibrarySyncService"/>: plans the
    /// work (discover new files, identify unknown ones, fetch tags and images), executes the
    /// plan, then rebuilds the grid once from the database.
    /// </summary>
    /// <remarks>
    /// The viewer owns none of the sync logic any more (#521 WP2) — no hashing, no Civitai
    /// calls, no per-tile persistence. Everything the old phases did is a step in the service,
    /// which records per-model state so a second run skips what has already been checked
    /// instead of re-asking Civitai about the whole library. Thumbnails are not a step yet
    /// (Plan B); on-screen tiles keep pulling their own preview on activation.
    /// </remarks>
    [RelayCommand]
    private async Task DownloadMissingMetadataAsync()
    {
        if (_librarySync is null)
        {
            SyncStatus = "Library sync not available.";
            return;
        }

        _metadataSyncCts = new CancellationTokenSource();
        var ct = _metadataSyncCts.Token;
        try
        {
            IsBusy = true;
            IsCancellable = true;
            BusyMessage = "Syncing with Civitai...";
            SyncStatus = "Planning sync...";

            // Task.Run, not a bare await: SQLite has no true async, so the planning pass (the
            // first-run state backfill plus every step's selection query) and the discovery scan
            // both run to completion inline before anything yields — on the UI thread that means a
            // frozen overlay and a dead Cancel button, which is exactly what the old phase code
            // used a background thread to avoid. Every UI touch below the await hops back through
            // PostToUi / InvokeOnUiAsync.
            var plan = await Task.Run(() => _librarySync.PlanAsync(SyncScope.Library, SyncOptions.All, ct), ct);

            // Correct only when the caller excluded discovery: a plan that still carries the
            // Discover step always reports work, because nobody knows what a scan will find until
            // it has run. The honest "nothing to do" verdict is the report's, below.
            if (!plan.HasWork)
            {
                SyncStatus = UpToDateStatus;
                _logger?.Info(LogCategory.Network, "CivitaiSync", "Sync plan is empty — nothing to do");
                return;
            }

            // Plan B replaces this with a confirmation dialog showing the same numbers.
            _logger?.Info(LogCategory.Network, "CivitaiSync",
                $"Starting sync: {string.Join(" · ", plan.Steps.Select(s => $"{SyncReport.Label(s.Kind)} {s.Count}"))}");

            var progress = new UiProgress<LibrarySyncProgress>(this, p =>
                SyncStatus = $"{SyncReport.Label(p.Step)} [{p.Index}/{p.Total}] {p.CurrentItem}");

            var report = await Task.Run(() => _librarySync.ExecuteAsync(plan, progress, ct), ct);

            // One rebuild, at the end: the service wrote straight to the database, so the
            // in-memory tiles are stale until they are re-projected from it.
            await RebuildTilesFromDatabaseAsync();

            var statusText = DescribeOutcome(report);
            _logger?.Info(LogCategory.Network, "CivitaiSync", statusText);
            SyncStatus = statusText;
        }
        catch (OperationCanceledException)
        {
            SyncStatus = "Metadata sync cancelled";
            _logger?.Info(LogCategory.Network, "CivitaiSync", "Metadata sync cancelled by user");
        }
        catch (Exception ex)
        {
            SyncStatus = $"Sync error: {ex.Message}";
            _logger?.Error(LogCategory.Network, "CivitaiSync", $"Sync failed: {ex.Message}", ex);
        }
        finally
        {
            IsBusy = false;
            IsCancellable = false;
            BusyMessage = null;
            _metadataSyncCts?.Dispose();
            _metadataSyncCts = null;
        }
    }

    /// <summary>
    /// Requests cancellation of the in-flight metadata sync. Safe no-op when idle.
    /// The sync stops after the current model finishes (cooperative cancellation).
    /// </summary>
    [RelayCommand]
    private void CancelMetadataDownload()
    {
        if (_metadataSyncCts is null)
        {
            return;
        }

        _metadataSyncCts.Cancel();
        SyncStatus = "Cancelling…";
        // Also update the busy-overlay message (not just the status bar) so the
        // user gets clear in-overlay feedback during the unwind window.
        BusyMessage = "Cancelling…";
        // Hide the Cancel button immediately so the click reads as received; the
        // sync itself unwinds up to one model later, where finally resets the rest.
        IsCancellable = false;
    }

    /// <summary>
    /// Test seam: injects a CTS to simulate an in-flight sync without standing up
    /// the full App.Services/DI graph the real sync requires.
    /// </summary>
    internal void SetActiveMetadataSyncCtsForTest(CancellationTokenSource cts)
        => _metadataSyncCts = cts;

    /// <summary>
    /// Opens the Civitai URL download dialog, lets the user preview the LoRA, and starts the download.
    /// </summary>
    [RelayCommand]
    private async Task DownloadLoraAsync()
    {
        var dialogService = App.Services?.GetService<IDialogService>();
        if (dialogService is null)
        {
            SyncStatus = "Dialog service not available.";
            return;
        }

        IReadOnlyList<string> sourceFolders = [];
        if (_settingsService is not null)
        {
            sourceFolders = await _settingsService.GetEnabledLoraSourcesAsync();
        }

        var result = await dialogService.ShowDownloadLoraDialogAsync(sourceFolders);
        if (!result.Confirmed || result.Version is null || string.IsNullOrWhiteSpace(result.DownloadUrl) || string.IsNullOrWhiteSpace(result.TargetFolder))
            return;

        var fileName = !string.IsNullOrWhiteSpace(result.FileName)
            ? result.FileName
            : $"{result.ModelName}_{result.Version.Name}.safetensors";
        var targetPath = Path.Combine(result.TargetFolder, fileName);

        var downloadService = App.Services?.GetService<LoraDownloadService>()
                              ?? new LoraDownloadService(_civitaiClient, _settingsService, _logger);

        SyncStatus = $"Downloading {fileName}...";
        // Route through IDownloadCoordinator so this download aggregates with any
        // concurrent Civitai-browser-queue downloads in the status bar (otherwise
        // they fight over the single-slot activity-log progress field).
        var coordinator = App.Services?.GetService<IDownloadCoordinator>();
        var taskName = $"Downloading {fileName}";

        _ = Task.Run(async () =>
        {
            var tcs = new TaskCompletionSource<bool>();

            async Task<bool> RunAsync(IProgress<DownloadTaskProgress>? progress, CancellationToken ct)
            {
                await downloadService.DownloadFileAsync(
                    result.DownloadUrl,
                    targetPath,
                    result.Version,
                    taskName,
                    reportProgress: (pct, message) =>
                    {
                        Dispatcher.UIThread.Post(() => SyncStatus = $"Downloading {fileName}: {message}");
                        progress?.Report(new DownloadTaskProgress((int)(pct * 100), message));
                    },
                    completed: () => Dispatcher.UIThread.Post(async () =>
                    {
                        SyncStatus = $"Downloaded {fileName}";
                        await RebuildTilesFromDatabaseAsync();
                        tcs.TrySetResult(true);
                    }),
                    failed: () => Dispatcher.UIThread.Post(() =>
                    {
                        SyncStatus = $"Download failed: {fileName}";
                        tcs.TrySetResult(false);
                    }),
                    externalCancellationToken: ct,
                    reportToActivityLog: coordinator is null);

                return await tcs.Task.ConfigureAwait(false);
            }

            if (coordinator is not null)
                await coordinator.EnqueueAsync(taskName, RunAsync, CancellationToken.None).ConfigureAwait(false);
            else
                await RunAsync(null, CancellationToken.None).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Reloads all models from the database and rebuilds tiles with proper grouping.
    /// Uses a fresh DI scope so the DbContext sees the latest committed data.
    /// Preserves the user's search/filter state.
    /// </summary>
    /// <summary>
    /// Groups installed-file entries by (Model, LoRA-source root) and builds one
    /// <see cref="ModelTileViewModel"/> per group. Each tile carries the (version → file)
    /// map for that location so the version switcher and OpenFolder/Delete work
    /// correctly. Issue #380.
    /// </summary>
    private List<ModelTileViewModel> BuildPerLocationTiles(IReadOnlyList<InstalledModelFile> files)
    {
        var dependencies = BuildTileDependencies();
        return files
            // Group by Civitai page so two Model rows that point at the same page (legacy
            // local-discovery duplicates) collapse into one tile; fall back to Model.Id
            // for rows without a page id, using -Id so it can't collide with a page id.
            .GroupBy(f => (
                PageKey: f.Model.CivitaiModelPageId ?? -f.Model.Id,
                f.SourceRoot))
            .Select(g =>
            {
                var models = g.Select(f => f.Model).DistinctBy(m => m.Id).ToList();
                var versionFiles = g.Select(f => (f.Version, f.File)).ToList();
                return ModelTileViewModel.FromModelInLocation(models, g.Key.SourceRoot, versionFiles, dependencies);
            })
            .ToList();
    }

    /// <summary>
    /// The scope factory for fresh-<c>DbContext</c> reads: the injected one when present,
    /// otherwise the locator — same failure mode as before when neither exists.
    /// </summary>
    private IServiceScopeFactory RequireScopeFactory()
        => _scopeFactory ?? App.Services!.GetRequiredService<IServiceScopeFactory>();

    /// <summary>Status shown when a run had genuinely nothing left to do.</summary>
    private const string UpToDateStatus = "Library is up to date — nothing to do";

    /// <summary>
    /// The status line for a finished run. A run that discovered no new file and had nothing
    /// planned in any step did no work at all, and <see cref="SyncReport.Summary"/> would report
    /// that as "Discovered 0" — technically true, and useless to read.
    /// </summary>
    private static string DescribeOutcome(SyncReport report)
    {
        if (report.NewFilesDiscovered == 0 && report.Steps.All(s => s.Planned == 0))
            return UpToDateStatus;

        return report.Failures.Count > 0
            ? $"{report.Summary} · {report.Failures.Count} failed"
            : report.Summary;
    }

    /// <summary>
    /// Progress sink that marshals through this ViewModel's UI-thread seam — deliberately not
    /// <see cref="Progress{T}"/>, which captures the <c>SynchronizationContext</c> at construction
    /// and would add a second, invisible hop on top of the one the ViewModel already owns (and in
    /// a unit test, with no context to capture, would deliver on the thread pool instead).
    /// </summary>
    private sealed class UiProgress<T>(LoraViewerViewModel owner, Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => owner.PostToUi(() => onReport(value));
    }

    /// <summary>Fire-and-forget hop to the UI thread, through the scheduler seam when injected.</summary>
    private void PostToUi(Action action)
    {
        if (_uiScheduler is not null) _uiScheduler.Post(action);
        else Dispatcher.UIThread.Post(action);
    }

    /// <summary>Awaitable hop to the UI thread, through the scheduler seam when injected.</summary>
    private Task InvokeOnUiAsync(Action action)
        => _uiScheduler is not null
            ? _uiScheduler.InvokeAsync(action)
            : Dispatcher.UIThread.InvokeAsync(action).GetTask();

    private async Task RebuildTilesFromDatabaseAsync()
    {
        using var scope = RequireScopeFactory().CreateScope();
        var syncService = scope.ServiceProvider.GetRequiredService<IModelSyncService>();

        // Issue #380: per-location fan-out — one tile per (Model, LoRA-source).
        var files = await syncService.LoadCachedFilesAsync();
        var tiles = BuildPerLocationTiles(files);

        await InvokeOnUiAsync(() =>
        {
            // Unsubscribe from old tiles
            foreach (var oldTile in AllTiles)
            {
                oldTile.Deleted -= OnTileDeleted;
                oldTile.DetailRequested -= OnTileDetailRequested;
            }

            AllTiles.Clear();

            foreach (var tile in tiles)
            {
                tile.Deleted += OnTileDeleted;
                tile.DetailRequested += OnTileDetailRequested;
                AllTiles.Add(tile);
            }

            TotalModelCount = AllTiles.Sum(t => t.ModelCount);
            RebuildAvailableBaseModels();
            ApplyFilters();
        });
    }

    /// <summary>
    /// Returns true if the base model string is a placeholder value ('???' or null/empty).
    /// </summary>
    private static bool IsPlaceholderBaseModel(string? baseModel)
        => string.IsNullOrWhiteSpace(baseModel) || baseModel == "???";

    /// <summary>
    /// Loads the installed-file rows for the LoRA Sorter from a fresh DI scope, the same pattern
    /// <see cref="LoadCachedTilesAsync"/> uses. <c>IModelSyncService</c> and <c>IUnitOfWork</c> are
    /// transient while this ViewModel is scoped, so <see cref="_syncService"/> is a single
    /// session-long <c>DbContext</c>; the sorter starts preview passes fire-and-forget from three
    /// option hooks with no re-entrancy guard, so handing it that shared instance risked
    /// "A second operation was started on this context instance before a previous operation
    /// completed" on a big library.
    /// </summary>
    private async Task<IReadOnlyList<InstalledModelFile>> LoadCachedFilesForSorterAsync(CancellationToken ct)
    {
        using var scope = RequireScopeFactory().CreateScope();
        var freshSyncService = scope.ServiceProvider.GetRequiredService<IModelSyncService>();
        return await freshSyncService.LoadCachedFilesAsync(ct);
    }

    /// <summary>
    /// Reads the Civitai API key from a fresh DI scope so callers always see the latest
    /// database value rather than a stale EF Core tracked entity. Extracted from the
    /// by-hash metadata lookup (<see cref="DownloadMetadataForTileAsync"/>); also handed to
    /// <see cref="SorterMetadataResolver"/> for the LoRA Sorter's unknown-file resolution.
    /// </summary>
    private async Task<string?> GetApiKeyForSorterAsync()
    {
        using var keyScope = RequireScopeFactory().CreateScope();
        var freshSettings = keyScope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        return await freshSettings.GetCivitaiApiKeyAsync();
    }

    /// <summary>
    /// Backfills <c>CivitaiModelPageId</c> for models that were synced before the field existed.
    /// <para>
    /// Step 1: Any model with <c>CivitaiId</c> set but <c>CivitaiModelPageId</c> null
    ///         gets <c>CivitaiModelPageId = CivitaiId</c>.
    /// </para>
    /// <para>
    /// Step 2: Any model that still has <c>CivitaiModelPageId</c> null but shares the same
    ///         Name (case-insensitive) with a model that now has it → inherits the value.
    /// </para>
    /// Skips quickly when nothing needs updating.
    /// </summary>
    private async Task BackfillCivitaiModelPageIdAsync()
    {
        try
        {
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            // Only needs CivitaiId, CivitaiModelPageId, Name — no need for full includes
            var allModels = await unitOfWork.Models.GetAllAsync();

            var dirty = false;

            // Step 1: CivitaiId → CivitaiModelPageId
            foreach (var model in allModels)
            {
                if (model.CivitaiId.HasValue && !model.CivitaiModelPageId.HasValue)
                {
                    model.CivitaiModelPageId = model.CivitaiId.Value;
                    dirty = true;
                }
            }

            // Step 2: Propagate by Name for models that still lack it
            var byName = allModels
                .Where(m => m.CivitaiModelPageId.HasValue)
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().CivitaiModelPageId!.Value, StringComparer.OrdinalIgnoreCase);

            foreach (var model in allModels)
            {
                if (!model.CivitaiModelPageId.HasValue
                    && byName.TryGetValue(model.Name, out var pageId))
                {
                    model.CivitaiModelPageId = pageId;
                    dirty = true;
                }
            }

            if (dirty)
            {
                await unitOfWork.SaveChangesAsync();
                _logger?.Info(LogCategory.General, "Backfill",
                    "Backfilled CivitaiModelPageId for existing models");
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.General, "Backfill",
                $"CivitaiModelPageId backfill failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Scan all configured LoRA source folders for byte-identical files and open
    /// the duplicate fixer window so the user can pick which copy to keep.
    /// </summary>
    [RelayCommand]
    private async Task ScanDuplicatesAsync()
    {
        var services = App.Services;
        if (services is null)
        {
            SyncStatus = "Services not initialised.";
            return;
        }

        var dialogService = services.GetService<IDialogService>();
        var finder = services.GetService<ILoraDuplicateFinder>();
        if (dialogService is null || finder is null)
        {
            SyncStatus = "Duplicate scanner is unavailable.";
            return;
        }

        IReadOnlyList<LoraDuplicateGroup> groups = Array.Empty<LoraDuplicateGroup>();
        try
        {
            await RunBusyAsync(async () =>
            {
                var progress = new Progress<LoraDuplicateProgress>(p =>
                {
                    BusyMessage = p.Total > 0
                        ? $"{p.Phase}: {p.Processed}/{p.Total}"
                        : p.Phase;
                });

                groups = await finder.FindAsync(progress).ConfigureAwait(false);
            }, "Scanning for duplicates...");
        }
        catch (OperationCanceledException)
        {
            SyncStatus = "Duplicate scan cancelled.";
            return;
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.General, "DuplicateScan", $"Duplicate scan failed: {ex.Message}", ex);
            await dialogService.ShowMessageAsync("Scan failed",
                $"Could not scan for duplicates:\n{ex.Message}");
            return;
        }

        if (groups.Count == 0)
        {
            SyncStatus = "No duplicate LoRAs found.";
            await dialogService.ShowMessageAsync("No duplicates",
                "No duplicate LoRA files were found in your configured source folders.");
            return;
        }

        var deleted = await dialogService.ShowLoraDuplicateFixerAsync(groups);
        if (deleted > 0)
        {
            SyncStatus = $"Removed {deleted} duplicate file(s).";
            await RebuildTilesFromDatabaseAsync();
        }
        else
        {
            SyncStatus = $"Found {groups.Count} duplicate group(s); none deleted.";
        }
    }

    /// <summary>
    /// Opens the detail panel for the given tile, or closes it if the same tile is already shown.
    /// Called by <see cref="ModelTileViewModel"/> when the user clicks a tile.
    /// </summary>
    public async Task OpenDetailAsync(ModelTileViewModel tile)
    {
        // Toggle: close if the same tile is already displayed
        if (IsDetailOpen && DetailViewModel?.SourceTile == tile)
        {
            CloseDetail();
            return;
        }

        // Unsubscribe from previous detail VM
        if (DetailViewModel is not null)
        {
            DetailViewModel.CloseRequested -= OnDetailCloseRequested;
            DetailViewModel.DownloadCompleted -= OnDetailDownloadCompleted;
            DetailViewModel.MetadataDeleted -= OnDetailMetadataDeleted;
            DetailViewModel.MetadataDownloadRequested -= OnDetailMetadataDownloadRequested;
        }

        // #438: the detail VM no longer reaches into App.Services — resolve the
        // dependencies it previously fetched from the locator here and inject them.
        var sp = App.Services;
        var detailVm = new ModelDetailViewModel(
            _civitaiClient,
            _settingsService,
            _secureStorage,
            _logger,
            _baseModelCatalog,
            sp?.GetService<IServiceScopeFactory>(),
            sp?.GetService<IDialogService>(),
            sp?.GetService<LoraDownloadService>(),
            sp?.GetService<IDownloadCoordinator>(),
            sp?.GetService<ITaskTracker>(),
            sp?.GetService<IActivityLogService>(),
            sp?.GetService<DiffusionNexus.Installer.SDK.Shared.Services.IClipboardService>(),
            sp?.GetService<IUiScheduler>());

        detailVm.CloseRequested += OnDetailCloseRequested;
        detailVm.DownloadCompleted += OnDetailDownloadCompleted;
        detailVm.MetadataDeleted += OnDetailMetadataDeleted;
        detailVm.MetadataDownloadRequested += OnDetailMetadataDownloadRequested;
        DetailViewModel = detailVm;
        IsDetailOpen = true;

        await detailVm.LoadAsync(tile);
    }

    /// <summary>
    /// Closes the detail panel.
    /// </summary>
    [RelayCommand]
    private void CloseDetail()
    {
        if (DetailViewModel is not null)
        {
            DetailViewModel.CloseRequested -= OnDetailCloseRequested;
            DetailViewModel.DownloadCompleted -= OnDetailDownloadCompleted;
            DetailViewModel.MetadataDeleted -= OnDetailMetadataDeleted;
            DetailViewModel.MetadataDownloadRequested -= OnDetailMetadataDownloadRequested;
        }

        IsDetailOpen = false;
        DetailViewModel = null;
    }

    private void OnDetailCloseRequested(object? sender, EventArgs e)
    {
        CloseDetail();
    }

    private async void OnDetailDownloadCompleted(object? sender, EventArgs e)
    {
        await RebuildTilesFromDatabaseAsync();
    }

    /// <summary>
    /// Handles <see cref="ModelDetailViewModel.MetadataDownloadRequested"/> (the
    /// detail-view "Download Metadata" button) by fetching Civitai metadata for that
    /// single LoRA and then reloading the detail view so the freshly fetched data
    /// (description, tags, images, full version list) is shown. The loading spinner
    /// and status text are driven via the detail VM's <c>IsLoading</c>/<c>StatusMessage</c>.
    /// </summary>
    private async void OnDetailMetadataDownloadRequested(object? sender, EventArgs e)
    {
        if (sender is not ModelDetailViewModel detail || detail.SourceTile is null)
            return;

        var tile = detail.SourceTile;

        detail.IsLoading = true;
        detail.StatusMessage = "Downloading metadata from Civitai...";

        try
        {
            var applied = await DownloadMetadataForTileAsync(tile);

            if (applied)
            {
                // DownloadMetadataForTileAsync already refreshed the in-memory tile (it now
                // carries a CivitaiId), so reloading the detail re-fetches the full version
                // list and repaints description/tags/images.
                await detail.LoadAsync(tile);
            }
            else
            {
                detail.StatusMessage = "No metadata found on Civitai for this file.";
            }
        }
        catch (Exception ex)
        {
            detail.StatusMessage = $"Metadata download failed: {ex.Message}";
            _logger?.Error(LogCategory.Network, "CivitaiSync",
                $"Single-LoRA metadata download failed for '{tile.DisplayName}': {ex.Message}", ex);
        }
        finally
        {
            // LoadAsync clears IsLoading on success; ensure it's reset on every other path.
            detail.IsLoading = false;
        }
    }

    /// <summary>
    /// Downloads metadata for a single LoRA (the detail-view "Download Metadata" button) by
    /// running the same <see cref="ILibrarySyncService"/> pipeline as the bulk flow, scoped to
    /// this one model: identify (hash → Civitai by hash → sidecar), then tags and images.
    /// Discovery is not part of it — the file is already in the library.
    /// </summary>
    /// <remarks>
    /// <c>ForceIdentify</c> is set because pressing the button IS the user asking for another
    /// look: a stored "checked, not on Civitai" verdict would otherwise skip the model and the
    /// button would appear to do nothing.
    /// </remarks>
    /// <returns><c>true</c> when any step applied something, which is what tells the detail view to reload.</returns>
    public async Task<bool> DownloadMetadataForTileAsync(ModelTileViewModel tile)
    {
        if (_librarySync is null)
        {
            SyncStatus = "Library sync not available.";
            return false;
        }

        var modelId = tile.ModelEntity?.Id ?? 0;
        if (modelId == 0)
        {
            _logger?.Warn(LogCategory.Network, "CivitaiSync",
                $"Cannot download metadata for '{tile.DisplayName}': the tile has no persisted model.");
            return false;
        }

        var options = new SyncOptions(
            new HashSet<SyncStepKind> { SyncStepKind.IdentifyModel, SyncStepKind.FetchTags, SyncStepKind.FetchImages },
            ForceIdentify: true);

        // Off the UI thread for the same reason the bulk run is (see DownloadMissingMetadataAsync):
        // the selection queries and the file hash both run inline until the first real yield.
        var plan = await Task.Run(() => _librarySync.PlanAsync(SyncScope.ForModels(modelId), options));
        var report = await Task.Run(() => _librarySync.ExecuteAsync(plan));

        var applied = report.Steps.Any(s => s.Succeeded > 0);
        if (!applied)
        {
            _logger?.Info(LogCategory.Network, "CivitaiSync",
                $"No metadata applied for '{tile.DisplayName}': {report.Summary}");
            return false;
        }

        // Re-read the one model the run touched so the tile (and the detail view behind it)
        // shows what was just written, without rebuilding the whole grid.
        using (var scope = RequireScopeFactory().CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var refreshedModel = await unitOfWork.Models.GetByIdWithIncludesAsync(modelId);
            if (refreshedModel is not null)
                await InvokeOnUiAsync(() => tile.RefreshModelData(refreshedModel));
        }

        // Pull a preview thumbnail if the tile still lacks one after the metadata sync.
        if (tile.IsThumbnailMissing)
        {
            try
            {
                await tile.TryDownloadMissingThumbnailAsync();
            }
            catch (Exception ex)
            {
                _logger?.Debug(LogCategory.General, "CivitaiSync",
                    $"Thumbnail download after metadata fetch failed for '{tile.DisplayName}': {ex.Message}");
            }
        }

        return true;
    }

    /// <summary>
    /// Handles <see cref="ModelDetailViewModel.MetadataDeleted"/> by running a full
    /// refresh: the .safetensors file is still on disk, so file discovery will
    /// re-create a bare-metadata <see cref="Model"/> row and the tile reappears
    /// immediately instead of vanishing until the next manual refresh.
    /// </summary>
    private async void OnDetailMetadataDeleted(object? sender, EventArgs e)
    {
        if (RefreshCommand.CanExecute(null))
        {
            await RefreshCommand.ExecuteAsync(null);
        }
        else
        {
            await RebuildTilesFromDatabaseAsync();
        }
    }

    /// <summary>
    /// Clears only the base model filter selections without touching other filters.
    /// </summary>
    [RelayCommand]
    private void ClearBaseModelFilters()
    {
        ClearBaseModelSelectionsCore();
        ApplyFilters();
        RebuildFlyoutBaseModels();
    }

    /// <summary>
    /// Reset all filters.
    /// </summary>
    [RelayCommand]
    private void ResetFilters()
    {
        SearchText = null;
        BaseModelFilterSearchText = null;
        OnlyInstalledBaseModels = false;

        ClearBaseModelSelectionsCore();
        ApplyFilters();
        RebuildFlyoutBaseModels();
    }

    /// <summary>
    /// Deselects every base-model item (including the Unknown sentinel) in one batch —
    /// a single indicator update and one filter pass at the caller instead of one full
    /// pass per item — and drops any not-yet-materialized saved-filter names (the user
    /// explicitly cleared, so the pending intent is void).
    /// </summary>
    private void ClearBaseModelSelectionsCore()
    {
        _suppressBaseModelFilterEvents = true;
        try
        {
            UnknownBaseModelItem.IsSelected = false;
            foreach (var item in AvailableBaseModels)
            {
                item.IsSelected = false;
            }
        }
        finally
        {
            _suppressBaseModelFilterEvents = false;
        }

        _pendingRestoredSelections = null;
        OnPropertyChanged(nameof(IsBaseModelFilterActive));
        OnPropertyChanged(nameof(ActiveBaseModelFilterCount));
    }

    /// <summary>
    /// Persists the current base-model filter (selections + Unknown + only-installed)
    /// to AppSettings. Restored automatically the next time the viewer opens.
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
                await s.SetLoraViewerFilterJsonAsync(json).ConfigureAwait(false);
                return true;
            });
            SyncStatus = "Base-model filter saved.";
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.General, "LoraViewer",
                $"Could not save base-model filter: {ex.Message}");
            SyncStatus = "Could not save the filter.";
        }
    }

    /// <summary>
    /// Runs a settings operation on a fresh scoped <see cref="IAppSettingsService"/> when
    /// DI is available. The constructor-injected instance is shared with other startup
    /// tasks (destination folders, the Civitai browser) over a single non-thread-safe
    /// DbContext, so the save/restore paths — which run concurrently with them — must not
    /// reuse it. Falls back to the injected instance without DI (design-time/tests).
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

    #endregion

    #region Property Changed Handlers

    partial void OnSearchTextChanged(string? value)
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        SearchDebounceTask = ApplyFiltersDebouncedAsync(cts.Token);
    }

    /// <summary>
    /// Runs <see cref="ApplyFilters"/> after <see cref="SearchDebounceInterval"/>
    /// unless a newer keystroke (or a direct <see cref="ApplyFilters"/> call)
    /// cancels it first.
    /// </summary>
    private async Task ApplyFiltersDebouncedAsync(CancellationToken token)
    {
        try
        {
            // No ConfigureAwait(false): must resume on the UI thread's sync
            // context because ApplyFilters mutates UI-bound collections.
            await Task.Delay(SearchDebounceInterval, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ApplyFilters();
    }

    partial void OnShowNsfwChanged(bool value)
    {
        ApplyFilters();
    }

    partial void OnBaseModelFilterSearchTextChanged(string? value) => RebuildFlyoutBaseModels();

    partial void OnOnlyInstalledBaseModelsChanged(bool value) => RebuildFlyoutBaseModels();

    #endregion

    #region Private Methods

    /// <summary>
    /// Handles the <see cref="ModelTileViewModel.Deleted"/> event by removing the tile
    /// from both <see cref="AllTiles"/> and <see cref="FilteredTiles"/>, then updating counts.
    /// </summary>
    private void OnTileDeleted(object? sender, EventArgs e)
    {
        if (sender is not ModelTileViewModel tile) return;

        tile.Deleted -= OnTileDeleted;

        Dispatcher.UIThread.Post(() =>
        {
            AllTiles.Remove(tile);
            // Removing from the bound collection detaches the tile's container (if it
            // was realized); the virtualizing grid reflows the rest automatically.
            FilteredTiles.Remove(tile);
            TotalModelCount = AllTiles.Count;
            FilteredModelCount = FilteredTiles.Sum(t => t.ModelCount);
            RebuildAvailableBaseModels();
        });
    }

    /// <summary>
    /// Handles the <see cref="ModelTileViewModel.DetailRequested"/> event by opening the detail panel.
    /// </summary>
    private async void OnTileDetailRequested(object? sender, EventArgs e)
    {
        if (sender is not ModelTileViewModel tile) return;
        await OpenDetailAsync(tile);
    }

    /// <summary>
    /// Applies a fresh catalog label list and rebuilds the filter options. Single entry
    /// point for catalog updates (startup load and forced refresh) — and the test seam
    /// for catalog-mode behavior.
    /// </summary>
    internal void ApplyCatalogBaseModels(IReadOnlyList<string> labels)
    {
        _catalogBaseModels = labels;
        RebuildAvailableBaseModels();
    }

    /// <summary>
    /// Rebuilds <see cref="AvailableBaseModels"/>. Primary source is the full Civitai
    /// catalog (<see cref="ICivitaiBaseModelCatalog"/>) so users can filter for any
    /// base model Civitai supports — UNIONED with the distinct <c>BaseModelRaw</c>
    /// values across installed tiles, because the scraped catalog can lack labels the
    /// Civitai API stamped on installed files (real case: "Krea 2" disappeared from
    /// Civitai's constants while 99 installed versions carried it — an installed base
    /// model must never become unfilterable). Falls back to installed values alone when
    /// the catalog is unavailable. Preserves existing selections where the value still
    /// exists and consumes pending saved-filter names as they materialize.
    /// </summary>
    private void RebuildAvailableBaseModels()
    {
        // Refresh the installed-set cache first — every tile-change path funnels through
        // this method, the source composition below needs it, and the flyout narrowing
        // must not rescan tiles per keystroke.
        _installedBaseModels.Clear();
        _hasUnknownInstalled = false;
        foreach (var version in AllTiles.SelectMany(t => t.Versions))
        {
            if (IsPlaceholderBaseModel(version.BaseModelRaw))
                _hasUnknownInstalled = true;
            else
                _installedBaseModels.Add(version.BaseModelRaw!);
        }

        List<string> source;
        if (_catalogBaseModels is { Count: > 0 } catalog)
        {
            // Catalog first, in catalog order (Civitai's natural ordering — alphabetizing
            // would split related entries like "SDXL 1.0" / "SDXL Turbo")...
            source = new List<string>(catalog);

            // ...then installed base models the catalog doesn't know, appended
            // alphabetically so they are always filterable. The browser mirrors this
            // full list too — single source of truth; Civitai's API tolerates unknown
            // baseModels values (200 + zero items, verified live 2026-08).
            var known = catalog.ToHashSet(StringComparer.OrdinalIgnoreCase);
            source.AddRange(_installedBaseModels
                .Where(raw => !known.Contains(raw))
                .OrderBy(raw => raw, StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            // Fallback: distinct values from installed tiles, alphabetical.
            source = _installedBaseModels
                .OrderBy(raw => raw, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Snapshot currently selected values so we can restore them
        var previouslySelected = AvailableBaseModels
            .Where(f => f.IsSelected)
            .Select(f => f.BaseModelRaw)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Unsubscribe from old items
        foreach (var item in AvailableBaseModels)
        {
            item.SelectionChanged -= OnBaseModelFilterChanged;
        }

        AvailableBaseModels.Clear();

        var consumedPending = false;
        foreach (var raw in source)
        {
            var restorePending = _pendingRestoredSelections?.Remove(raw) == true;
            consumedPending |= restorePending;
            var item = new BaseModelFilterItem(raw)
            {
                IsSelected = previouslySelected.Remove(raw) || restorePending,
            };
            item.SelectionChanged += OnBaseModelFilterChanged;
            AvailableBaseModels.Add(item);
        }

        // Selected names the new source doesn't contain must SURVIVE the rebuild.
        // Dropping them permanently lost saved-filter selections whenever the source
        // transiently shrank (catalog swap-in before tiles loaded, tile reloads on
        // tab switches/refreshes) — the classic victim being an installed-only label
        // like "Krea 2" that the Civitai catalog doesn't list. Parking them in
        // _pendingRestoredSelections re-selects them the moment they rematerialize.
        if (previouslySelected.Count > 0)
        {
            (_pendingRestoredSelections ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                .UnionWith(previouslySelected);
        }
        if (_pendingRestoredSelections is { Count: 0 })
            _pendingRestoredSelections = null;

        RebuildFlyoutBaseModels();

        // A pending saved-filter name materialized: the effective selection changed, so
        // the indicator and the grid must catch up (normal rebuilds preserve the selection
        // set exactly, and their callers re-apply afterwards where needed).
        if (consumedPending)
        {
            OnPropertyChanged(nameof(IsBaseModelFilterActive));
            OnPropertyChanged(nameof(ActiveBaseModelFilterCount));
            ApplyFilters();
        }
    }

    /// <summary>
    /// Whether the flyout's option list is currently narrowed (search text or the
    /// only-installed toggle). While narrowed, selection toggles also refresh the
    /// composed view because selected items are pinned visible.
    /// </summary>
    private bool IsFlyoutNarrowingActive
        => OnlyInstalledBaseModels || !string.IsNullOrWhiteSpace(BaseModelFilterSearchText);

    /// <summary>
    /// Recomputes <see cref="FlyoutBaseModels"/>: "Unknown" first (hidden when
    /// "only installed" is on and no placeholder tiles exist), then the shared items,
    /// filtered by the flyout search text and the only-installed toggle. Selected items
    /// always stay visible — otherwise a narrowed list would leave an active filter with
    /// no way to untoggle it. Reuses the shared item instances — never copies — so
    /// selection state stays single-sourced.
    /// </summary>
    private void RebuildFlyoutBaseModels()
    {
        var search = BaseModelFilterSearchText?.Trim();
        var hasSearch = !string.IsNullOrWhiteSpace(search);

        var items = new List<BaseModelFilterItem>();

        var showUnknown = UnknownBaseModelItem.IsSelected
            || ((!OnlyInstalledBaseModels || _hasUnknownInstalled)
                && (!hasSearch || UnknownBaseModelLabel.Contains(search!, StringComparison.OrdinalIgnoreCase)));
        if (showUnknown)
            items.Add(UnknownBaseModelItem);

        foreach (var item in AvailableBaseModels)
        {
            if (!item.IsSelected)
            {
                if (OnlyInstalledBaseModels && !_installedBaseModels.Contains(item.BaseModelRaw))
                    continue;
                if (hasSearch && !item.BaseModelRaw.Contains(search!, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            items.Add(item);
        }

        // Single Reset notification — the flyout's ItemsControl is not virtualized, so
        // per-item Adds would trigger one layout pass per entry.
        FlyoutBaseModels.ReplaceAll(items);
    }

    /// <summary>
    /// Snapshots the current base-model filter state for persistence. Includes any
    /// saved names still pending a list rebuild, so re-saving while the catalog is
    /// stale/offline never truncates the previously saved intent.
    /// </summary>
    internal LoraViewerFilterData CaptureFilter() => new()
    {
        SelectedBaseModels = AvailableBaseModels
            .Where(f => f.IsSelected)
            .Select(f => f.BaseModelRaw)
            .Concat(_pendingRestoredSelections as IEnumerable<string> ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
        IncludeUnknown = UnknownBaseModelItem.IsSelected,
        OnlyInstalled = OnlyInstalledBaseModels,
        SortField = SelectedSortOption.Field.ToString(),
        SortDescending = SortDescending,
    };

    /// <summary>
    /// Applies a saved filter, REPLACING the current selection (case-insensitive name
    /// match), the Unknown sentinel, and the only-installed toggle — in one batch with a
    /// single filter pass. Saved names the current list doesn't contain yet (stale or
    /// offline catalog) are kept in <see cref="_pendingRestoredSelections"/> for the next
    /// list rebuild. Must run on the UI thread (mutates bound state).
    /// </summary>
    internal void ApplySavedFilter(LoraViewerFilterData data)
    {
        var wanted = (data.SelectedBaseModels ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        _suppressBaseModelFilterEvents = true;
        try
        {
            foreach (var item in AvailableBaseModels)
            {
                item.IsSelected = wanted.Remove(item.BaseModelRaw);
            }

            UnknownBaseModelItem.IsSelected = data.IncludeUnknown;
            OnlyInstalledBaseModels = data.OnlyInstalled;

            // Sort is part of the saved filter since it gained SortField/SortDescending;
            // older saved JSON carries neither — leave the current sort untouched then.
            if (!string.IsNullOrWhiteSpace(data.SortField)
                && Enum.TryParse<LoraSortField>(data.SortField, out var field)
                && SortOptions.FirstOrDefault(o => o.Field == field) is { } option)
            {
                SelectedSortOption = option;
            }
            if (data.SortDescending is { } descending)
            {
                SortDescending = descending;
            }
        }
        finally
        {
            _suppressBaseModelFilterEvents = false;
        }

        _pendingRestoredSelections = wanted.Count > 0 ? wanted : null;

        OnPropertyChanged(nameof(IsBaseModelFilterActive));
        OnPropertyChanged(nameof(ActiveBaseModelFilterCount));
        ApplyFilters();
        RebuildFlyoutBaseModels();
    }

    /// <summary>
    /// Loads the saved filter from AppSettings and applies it on the UI thread.
    /// Runs once at startup, after the catalog load so the full option list exists.
    /// Corrupt or missing data degrades silently to the unfiltered default.
    /// </summary>
    private async Task RestoreSavedFilterAsync()
    {
        if (_settingsService is null) return;
        try
        {
            var json = await UseSettingsServiceAsync(s => s.GetLoraViewerFilterJsonAsync())
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return;

            var data = JsonSerializer.Deserialize<LoraViewerFilterData>(json);
            if (data is null) return;

            await Dispatcher.UIThread.InvokeAsync(() => ApplySavedFilter(data));
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.General, "LoraViewer",
                $"Could not restore saved base-model filter: {ex.Message}");
        }
    }

    /// <summary>
    /// Startup sequence for the base-model filter: catalog first (builds the full
    /// option list), then the saved-filter restore (selection by name needs the
    /// list to exist). A later catalog refresh preserves selections by name.
    /// </summary>
    private async Task InitializeBaseModelFilterAsync()
    {
        await LoadBaseModelCatalogAsync().ConfigureAwait(false);
        await RestoreSavedFilterAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Populates the shared destination picker with the user's configured LoRA source
    /// folders so the browser's queue panel can pick one immediately.
    /// </summary>
    private async Task LoadDestinationFoldersAsync(DownloadDestinationViewModel destination)
    {
        if (_settingsService is null) return;
        try
        {
            var folders = await _settingsService.GetEnabledLoraSourcesAsync();
            var favorite = await _settingsService.GetFavoriteLoraSourceAsync();
            await destination.InitializeAsync(folders, favorite);
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.Network, "LoraViewer",
                $"Failed to load LoRA source folders for download destination: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches the Civitai base-model catalog once at startup and rebuilds the filter
    /// list. The catalog itself has built-in fallbacks (disk cache → live fetch → bundled
    /// snapshot), so this almost always yields a list; the only no-op path is when
    /// <see cref="_baseModelCatalog"/> is null (design-time).
    /// </summary>
    private Task LoadBaseModelCatalogAsync() => ReloadBaseModelFilterAsync();

    /// <summary>
    /// Pulls the current catalog labels (memory/disk cache or the freshly-fetched
    /// list after a forced refresh) and rebuilds <see cref="AvailableBaseModels"/>
    /// on the UI thread. Used both for the one-time startup load and for the live
    /// refresh triggered by <see cref="OnBaseModelCatalogStatusChanged"/>.
    /// </summary>
    private async Task ReloadBaseModelFilterAsync()
    {
        if (_baseModelCatalog is null) return;

        try
        {
            var labels = await _baseModelCatalog.GetBaseModelsAsync().ConfigureAwait(false);
            if (labels is null || labels.Count == 0) return;

            await Dispatcher.UIThread.InvokeAsync(() => ApplyCatalogBaseModels(labels));
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.Network, "LoraViewer",
                $"Civitai base-model catalog load failed; falling back to distinct-from-installed: {ex.Message}");
        }
    }

    /// <summary>
    /// Live-updates the base-model filter shared by the Installed and Browse Civitai
    /// tabs whenever the catalog completes a refresh that produced a list (a forced
    /// refresh from Settings, or its bundled fallback). Without this the filter would
    /// only reflect a refreshed catalog after an app restart. Normal startup cache
    /// hits are already handled by the initial <see cref="LoadBaseModelCatalogAsync"/>.
    /// </summary>
    private void OnBaseModelCatalogStatusChanged(object? sender, CivitaiBaseModelCatalogEventArgs e)
    {
        if (e.Kind is not (CivitaiBaseModelCatalogEventKind.FetchSucceeded
                           or CivitaiBaseModelCatalogEventKind.UsedBundledFallback))
        {
            return;
        }

        _ = ReloadBaseModelFilterAsync();
    }

    /// <summary>
    /// Called when any base model filter item's selection changes.
    /// </summary>
    private void OnBaseModelFilterChanged(object? sender, EventArgs e)
    {
        if (_suppressBaseModelFilterEvents) return;

        OnPropertyChanged(nameof(IsBaseModelFilterActive));
        OnPropertyChanged(nameof(ActiveBaseModelFilterCount));
        ApplyFilters();

        // A toggle can change which items the narrowed flyout shows (selected items are
        // pinned visible), so refresh the composed view while narrowing is active.
        if (IsFlyoutNarrowingActive)
            RebuildFlyoutBaseModels();
    }

    /// <summary>
    /// Re-applies filters (and thus the sort) when the Installed-tab sort field changes.
    /// </summary>
    partial void OnSelectedSortOptionChanged(LoraSortOption value) => ApplyFilters();

    /// <summary>
    /// Re-applies filters (and thus the sort) when the Installed-tab sort direction changes.
    /// </summary>
    partial void OnSortDescendingChanged(bool value) => ApplyFilters();

    /// <summary>
    /// Orders the filtered tiles by the selected <see cref="SelectedSortOption"/> field and
    /// <see cref="SortDescending"/> direction. Name uses a case-insensitive comparison;
    /// Date added uses <see cref="Model.CreatedAt"/> (when the LoRA was first discovered /
    /// imported into the database). Name is the tiebreaker for stable ordering.
    /// </summary>
    private List<ModelTileViewModel> SortTiles(List<ModelTileViewModel> tiles)
    {
        var field = SelectedSortOption?.Field ?? LoraSortField.Name;

        IOrderedEnumerable<ModelTileViewModel> ordered = field switch
        {
            LoraSortField.DateAdded => SortDescending
                ? tiles.OrderByDescending(t => t.ModelEntity?.CreatedAt ?? DateTimeOffset.MinValue)
                : tiles.OrderBy(t => t.ModelEntity?.CreatedAt ?? DateTimeOffset.MinValue),
            _ => SortDescending
                ? tiles.OrderByDescending(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                : tiles.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase),
        };

        // Stable tiebreaker so equal dates / names keep a deterministic order.
        ordered = ordered.ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase);

        return ordered.ToList();
    }

    private void ApplyFilters()
    {
        // A direct apply supersedes any pending debounced search pass.
        _searchDebounceCts?.Cancel();

        // 1. Build the FULL filtered set (used for count + windowing).
        //    Search / NSFW / base-model filters all run against AllTiles, not against
        //    the window — so 4K LoRAs stay searchable.
        var query = AllTiles.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            query = query.Where(t =>
                t.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                t.FileName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                t.CreatorName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                t.TagNames.Any(tag => tag.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        if (!ShowNsfw)
        {
            query = query.Where(t => !t.IsNsfw);
        }

        var activeBaseModels = AvailableBaseModels
            .Where(f => f.IsSelected)
            .Select(f => f.BaseModelRaw)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var includeUnknown = UnknownBaseModelItem.IsSelected;

        if (activeBaseModels.Count > 0 || includeUnknown)
        {
            query = query.Where(t =>
                t.Versions.Any(v =>
                    (includeUnknown && IsPlaceholderBaseModel(v.BaseModelRaw)) ||
                    (!string.IsNullOrEmpty(v.BaseModelRaw) &&
                     activeBaseModels.Contains(v.BaseModelRaw))));
        }

        var filtered = SortTiles(query.ToList());

        // Unchanged result set AND order (e.g. the extra keystroke matched the same
        // tiles): keep the current tiles and scroll position, skip the rebuild. A sort
        // change reorders the list, so SequenceEqual is false and the rebuild runs.
        // Refresh paths always create new tile instances, so they never hit this.
        if (filtered.SequenceEqual(FilteredTiles))
        {
            return;
        }

        // Swap the whole set in one shot: BatchObservableCollection.ReplaceAll fires a
        // single CollectionChanged.Reset, so the ItemsRepeater does ONE realization pass
        // (of the ~visible tiles) instead of processing N per-item Add notifications — the
        // difference between a smooth filter and a multi-tens-of-ms UI-thread stall per
        // keystroke at thousands of tiles.
        FilteredTiles.ReplaceAll(filtered);

        // Status / count reflects the full filtered set.
        FilteredModelCount = FilteredTiles.Sum(t => t.ModelCount);

        TriggerVisibleUpdateCheck();
    }

    /// <summary>
    /// Kicks off <see cref="ILoraUpdateChecker.CheckVisibleAsync"/> for the
    /// current <see cref="FilteredTiles"/>. Cancels any previous batch first so
    /// rapid filter / pagination changes don't pile up duplicate API calls.
    /// </summary>
    private void TriggerVisibleUpdateCheck()
    {
        if (_updateChecker is null || _settingsService is null)
        {
            return;
        }

        if (FilteredTiles.Count == 0)
        {
            return;
        }

        // Cancel any previous in-flight batch for the prior page.
        var previousCts = Interlocked.Exchange(ref _updateCheckCts, new CancellationTokenSource());
        previousCts?.Cancel();
        previousCts?.Dispose();
        var cts = _updateCheckCts;
        if (cts is null)
        {
            return;
        }

        // Snapshot the tiles (Take also snapshots) so concurrent collection edits don't
        // surface as InvalidOperationException inside the checker. Capped at
        // PassiveUpdateCheckLimit — see the field's remarks — so a multi-thousand-LoRA
        // library doesn't fire one Civitai request per tile on every filter pass.
        var snapshot = FilteredTiles.Take(PassiveUpdateCheckLimit).ToArray();

        _ = Task.Run(async () =>
        {
            try
            {
                var settings = await _settingsService.GetSettingsAsync(cts.Token).ConfigureAwait(false);
                var stalenessDays = settings.LoraUpdateCheckStalenessDays;
                if (stalenessDays <= 0)
                {
                    return; // feature disabled by user
                }

                await _updateChecker
                    .CheckVisibleAsync(
                        snapshot,
                        TimeSpan.FromDays(stalenessDays),
                        LoraUpdateTriggerSource.Stale,
                        cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected — the user paginated/filtered to a new page.
            }
            catch (Exception ex)
            {
                _logger?.Debug(LogCategory.Network, "LoraViewer",
                    $"Visible update check failed: {ex.Message}");
            }
        }, cts.Token);
    }

    private void LoadDemoData()
    {
        AllTiles.Clear();

        // All demo models — single and grouped — go through the same grouping pipeline
        var allDemoModels = new List<Model>
        {
            // Single-version models
            CreateDemoModel("Anime Character LoRA", "AIArtist", "Pony", 25000),
            CreateDemoModel("Realistic Portrait", "PhotoMaster", "SDXL 1.0", 45000),
            CreateDemoModel("Cyberpunk Aesthetic", "NeonCreator", "Illustrious", 8500),
            CreateDemoModel("Vintage Film Look", "RetroVision", "SD 1.5", 3200),
            CreateDemoModel("Landscape Enhancer", "NatureAI", "SDXL 1.0", 15000),
            CreateDemoModel("Comic Book Style", "ComicFan", "SD 1.5", 9800),
            CreateDemoModel("Sci-Fi Concepts", "FutureTech", "Flux.1 D", 4500),
            CreateDemoModel("Video Enhancer", "VideoMaster", "Wan Video 14B t2v", 2100),
            CreateDemoModel("Turbo Generator", "SpeedyAI", "Z-Image-Turbo", 11000),
            // A local file discovered without metadata — exercises the "Unknown" filter.
            CreateDemoModel("Legacy Style", "OldTimer", "???", 100),
        };

        // Add grouped demo models (separate entities sharing the same Name)
        allDemoModels.AddRange(CreateGroupedDemoModels());

        // Use the same grouping pipeline as real data
        var tiles = GroupModelsIntoTiles(allDemoModels);
        foreach (var tile in tiles)
        {
            tile.Deleted += OnTileDeleted;
            tile.DetailRequested += OnTileDetailRequested;
            AllTiles.Add(tile);
        }

        TotalModelCount = AllTiles.Count;
        RebuildAvailableBaseModels();
        ApplyFilters();
    }

    private static Model CreateDemoModel(string name, string creator, string baseModel, int downloads)
    {
        return CreateDemoModel(name, creator, new[] { baseModel }, downloads);
    }

    private static Model CreateDemoModel(string name, string creator, string[] baseModels, int downloads)
    {
        var creatorEntity = new Creator
        {
            Username = creator,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(30, 365))
        };

        var model = new Model
        {
            CivitaiId = Random.Shared.Next(10000, 999999),
            Name = name,
            Type = ModelType.LORA,
            Creator = creatorEntity,
            Source = DataSource.CivitaiApi,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(1, 180)),
            IsNsfw = Random.Shared.Next(10) < 2 // 20% chance of NSFW
        };

        // Add versions for each base model
        var versionNum = 1;
        foreach (var baseModel in baseModels)
        {
            var version = new ModelVersion
            {
                CivitaiId = Random.Shared.Next(100000, 9999999),
                Name = baseModels.Length > 1 ? $"{name} - {baseModel}" : $"{name} v{versionNum}.0",
                BaseModelRaw = baseModel,
                BaseModel = ParseBaseModel(baseModel),
                DownloadCount = downloads / baseModels.Length + Random.Shared.Next(-1000, 1000),
                Rating = 4.0 + Random.Shared.NextDouble(),
                RatingCount = Random.Shared.Next(10, 500),
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(1, 90)),
                Model = model
            };

            // Add a file
            version.Files.Add(new ModelFile
            {
                FileName = $"{name.Replace(" ", "_").ToLowerInvariant()}.safetensors",
                SizeKB = Random.Shared.Next(50000, 500000),
                Format = FileFormat.SafeTensor,
                IsPrimary = true,
                ModelVersion = version
            });

            // Add a placeholder image (no actual thumbnail data for demo)
            version.Images.Add(new ModelImage
            {
                Url = $"https://example.com/images/{Random.Shared.Next(1000, 9999)}.jpg",
                Width = 512,
                Height = 768,
                SortOrder = 0,
                ModelVersion = version
            });

            // Add trigger words
            version.TriggerWords.Add(new TriggerWord
            {
                Word = name.Split(' ')[0].ToLowerInvariant(),
                Order = 0,
                ModelVersion = version
            });

            model.Versions.Add(version);
            versionNum++;
        }

        return model;
    }

    /// <summary>
    /// Creates demo models that share the same Name to demonstrate grouped cards.
    /// Each model is a separate entity (different local file) belonging to the same LoRA,
    /// mirroring how Civitai models with multiple base-model versions appear after discovery.
    /// </summary>
    private static List<Model> CreateGroupedDemoModels()
    {
        var models = new List<Model>();

        // Group 1: "Fantasy Style" exists as both SD 1.5 and SDXL versions
        var fantasyCreator = new Creator { Username = "DreamWeaver" };
        models.Add(CreateGroupedModel("Fantasy Style", fantasyCreator, "SD 1.5", "fantasy_style_sd15.safetensors", 6000));
        models.Add(CreateGroupedModel("Fantasy Style", fantasyCreator, "SDXL 1.0", "fantasy_style_sdxl.safetensors", 6000));

        // Group 2: "Anime Eyes Detail" exists as Pony and Illustrious versions
        var animeEyesCreator = new Creator { Username = "MangaKing" };
        models.Add(CreateGroupedModel("Anime Eyes Detail", animeEyesCreator, "Pony", "anime_eyes_pony.safetensors", 33000));
        models.Add(CreateGroupedModel("Anime Eyes Detail", animeEyesCreator, "Illustrious", "anime_eyes_illustrious.safetensors", 34000));

        // Group 3: "Oil Painting Effect" exists as SDXL and SD 1.5 versions
        var oilPaintCreator = new Creator { Username = "ClassicArt" };
        models.Add(CreateGroupedModel("Oil Painting Effect", oilPaintCreator, "SDXL 1.0", "oil_painting_sdxl.safetensors", 11000));
        models.Add(CreateGroupedModel("Oil Painting Effect", oilPaintCreator, "SD 1.5", "oil_painting_sd15.safetensors", 10000));

        return models;
    }

    /// <summary>
    /// Creates a single model entity for use in grouped demo scenarios.
    /// </summary>
    private static Model CreateGroupedModel(
        string name, Creator creator,
        string baseModel, string fileName, int downloads)
    {
        var model = new Model
        {
            CivitaiId = Random.Shared.Next(10000, 999999),
            Name = name,
            Type = ModelType.LORA,
            Creator = creator,
            Source = DataSource.CivitaiApi,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(1, 180)),
        };

        var version = new ModelVersion
        {
            CivitaiId = Random.Shared.Next(100000, 9999999),
            Name = $"{name} - {baseModel}",
            BaseModelRaw = baseModel,
            BaseModel = ParseBaseModel(baseModel),
            DownloadCount = downloads + Random.Shared.Next(-1000, 1000),
            Rating = 4.0 + Random.Shared.NextDouble(),
            RatingCount = Random.Shared.Next(10, 500),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(1, 90)),
            Model = model,
        };

        version.Files.Add(new ModelFile
        {
            FileName = fileName,
            SizeKB = Random.Shared.Next(50000, 500000),
            Format = FileFormat.SafeTensor,
            IsPrimary = true,
            ModelVersion = version,
        });

        version.Images.Add(new ModelImage
        {
            Url = $"https://example.com/images/{Random.Shared.Next(1000, 9999)}.jpg",
            Width = 512,
            Height = 768,
            SortOrder = 0,
            ModelVersion = version,
        });

        version.TriggerWords.Add(new TriggerWord
        {
            Word = name.Split(' ')[0].ToLowerInvariant(),
            Order = 0,
            ModelVersion = version,
        });

        model.Versions.Add(version);
        return model;
    }

    private static BaseModelType ParseBaseModel(string baseModelRaw)
        => BaseModelTypeExtensions.ParseCivitai(baseModelRaw);

    #endregion
}

/// <summary>
/// Field the Installed-tab tile grid is sorted by.
/// </summary>
public enum LoraSortField
{
    /// <summary>Sort by the model's display name.</summary>
    Name,

    /// <summary>Sort by when the LoRA was first added (Model.CreatedAt).</summary>
    DateAdded,
}

/// <summary>
/// One entry in the Installed-tab "Sort by" dropdown. <see cref="ToString"/> returns the
/// label so the ComboBox can render it without an item template.
/// </summary>
public sealed record LoraSortOption(string Label, LoraSortField Field)
{
    /// <inheritdoc/>
    public override string ToString() => Label;
}
