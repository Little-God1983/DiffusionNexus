using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Infrastructure;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

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
    /// </summary>
    public bool IsBaseModelFilterActive => AvailableBaseModels.Any(f => f.IsSelected);

    /// <summary>
    /// Count of currently active base model filters.
    /// </summary>
    public int ActiveBaseModelFilterCount => AvailableBaseModels.Count(f => f.IsSelected);

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

    /// <summary>
    /// Cached catalog labels (full Civitai base-model list). When non-empty, drives
    /// <see cref="RebuildAvailableBaseModels"/> instead of the distinct-from-installed
    /// fallback. Populated by <see cref="LoadBaseModelCatalogAsync"/>.
    /// </summary>
    private IReadOnlyList<string>? _catalogBaseModels;

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
        BrowserViewModel = new CivitaiBrowserViewModel();
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
        ILoraUpdateChecker? updateChecker = null)
    {
        _selectedSortOption = SortOptions[0];
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _civitaiClient = civitaiClient;
        _secureStorage = secureStorage;
        _logger = logger;
        _baseModelCatalog = baseModelCatalog;
        _updateChecker = updateChecker;

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
        BrowserViewModel = new CivitaiBrowserViewModel(_civitaiClient, _settingsService, _logger, queue, AvailableBaseModels);

        _ = LoadBaseModelCatalogAsync();
        _ = LoadDestinationFoldersAsync(destination);
    }

    /// <summary>
    /// View model for the "Browse Civitai" sub-tab.
    /// </summary>
    public CivitaiBrowserViewModel BrowserViewModel { get; }

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
    /// Download missing metadata from Civitai for models that were discovered locally.
    /// Discovers new files first, then uses full-file SHA256 hash to find matching Civitai model versions.
    /// Automatically re-fetches missing image data and downloads thumbnails afterward.
    /// Heavy I/O runs on a background thread so the UI stays responsive.
    /// </summary>
    [RelayCommand]
    private async Task DownloadMissingMetadataAsync()
    {
        if (_civitaiClient is null || _settingsService is null)
        {
            SyncStatus = "Civitai client not available.";
            return;
        }

        _metadataSyncCts = new CancellationTokenSource();
        var ct = _metadataSyncCts.Token;
        try
        {
            IsBusy = true;
            IsCancellable = true;
            BusyMessage = "Syncing with Civitai...";

            // Get API key — use a fresh DI scope so we read the latest value
            // from the database, avoiding stale EF Core tracked entities.
            string? apiKey;
            {
                using var keyScope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
                var freshSettings = keyScope.ServiceProvider.GetRequiredService<IAppSettingsService>();
                apiKey = await freshSettings.GetCivitaiApiKeyAsync();
            }

            _logger?.Info(LogCategory.Network, "CivitaiSync",
                $"Starting metadata sync (API key: {(string.IsNullOrEmpty(apiKey) ? "NOT SET" : "configured")})");

            // Per-LoRA trigger logging is intentionally suppressed for this path —
            // a single batch entry is logged at the start so the log file isn't
            // flooded with 10K identical "trigger=DownloadMetadataButton" lines.
            _logger?.Debug(LogCategory.Network, "LoraUpdateChecker",
                $"Update batch started (trigger={LoraUpdateTriggerSource.DownloadMetadataButton})");

            // ── Phase 0: Discover new files and rebuild tiles so all models are visible ──
            // Without this, only previously loaded tiles are processed.
            var tiles = await Task.Run(async () =>
            {
                Dispatcher.UIThread.Post(() => SyncStatus = "Discovering new files...");
                await DiscoverNewFilesAsync();
                await BackfillCivitaiModelPageIdAsync();

                Dispatcher.UIThread.Post(() => SyncStatus = "Loading models from database...");

                using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
                var freshSyncService = scope.ServiceProvider.GetRequiredService<IModelSyncService>();
                // Issue #380: per-location fan-out (see RefreshAsync for the rationale).
                var files = await freshSyncService.LoadCachedFilesAsync();
                return BuildPerLocationTiles(files);
            });

            // Update UI with discovered tiles
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

            // ── Run sync phases (network + file I/O — awaits release the UI thread) ──
            var statusParts = new List<string>();

            // ── Phase 1: Sync metadata via Civitai API hash lookup (freshly discovered models) ──
            // Must run before Phase 1b so the API gets first chance at providing rich metadata.
            // Phase 1 already falls back to local sidecar files when the API returns 404.
            await SyncMetadataPhaseAsync(apiKey, statusParts, ct);

            // ── Phase 1b: Re-process historical LocalFile models with sidecar fallback ──
            // Targets models synced before sidecar parsing was added (already have LastSyncedAt
            // but still have placeholder BaseModelRaw). Phase 1 handles fresh models.
            await ReprocessLocalFileModelsPhaseAsync(statusParts, ct);

            // ── Phase 2: Re-fetch images for synced models that have no preview images ──
            await RefetchMissingImagesPhaseAsync(apiKey, statusParts, ct);

            // ── Phase 3: Backfill tags for models synced before tag persistence was added ──
            await BackfillMissingTagsPhaseAsync(apiKey, statusParts, ct);

            // ── Rebuild tiles so Phase 4 operates on fresh DB-backed tiles ──
            await RebuildTilesFromDatabaseAsync();

            // ── Phase 4: Download thumbnails for tiles still showing "No Preview" ──
            await DownloadMissingThumbnailsPhaseAsync(statusParts, ct);

            // Final status
            if (statusParts.Count == 0)
                statusParts.Add("Everything up to date");

            var statusText = string.Join(" · ", statusParts);
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

    private async Task RebuildTilesFromDatabaseAsync()
    {
        using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var syncService = scope.ServiceProvider.GetRequiredService<IModelSyncService>();

        // Issue #380: per-location fan-out — one tile per (Model, LoRA-source).
        var files = await syncService.LoadCachedFilesAsync();
        var tiles = BuildPerLocationTiles(files);

        await Dispatcher.UIThread.InvokeAsync(() =>
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
    /// Phase 1b: Re-process historical LocalFile models that were synced before sidecar parsing was added.
    /// Targets models with Source=LocalFile, no CivitaiId, placeholder BaseModelRaw, AND LastSyncedAt
    /// already set (meaning Phase 1 already ran on them in a previous session but found no API match).
    /// Fresh models (LastSyncedAt=null) are handled by Phase 1 which includes its own sidecar fallback.
    /// </summary>
    private async Task ReprocessLocalFileModelsPhaseAsync(List<string> statusParts, CancellationToken ct)
    {
        var tilesNeedingReprocess = AllTiles
            .Where(t => t.ModelEntity is { CivitaiId: null, Source: DataSource.LocalFile, LastSyncedAt: not null }
                        && IsPlaceholderBaseModel(t.SelectedVersion?.BaseModelRaw))
            .ToList();

        if (tilesNeedingReprocess.Count == 0)
        {
            _logger?.Debug(LogCategory.General, "CivitaiSync",
                "No historical LocalFile models with placeholder metadata — skipping Phase 1b");
            return;
        }

        _logger?.Info(LogCategory.General, "CivitaiSync",
            $"Phase 1b: {tilesNeedingReprocess.Count} historical LocalFile models need sidecar re-processing");

        var reprocessed = 0;
        var skipped = 0;

        for (var i = 0; i < tilesNeedingReprocess.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tile = tilesNeedingReprocess[i];
            var file = tile.SelectedVersion?.PrimaryFile;
            var localPath = file?.LocalPath;

            if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
            {
                skipped++;
                continue;
            }

            Dispatcher.UIThread.Post(() =>
                SyncStatus = $"[{i + 1}/{tilesNeedingReprocess.Count}] Re-reading sidecar for {tile.DisplayName}...");

            try
            {
                var applied = await TryApplyLocalMetadataFallbackAsync(tile, localPath);
                if (applied) reprocessed++;
                else skipped++;
            }
            catch (Exception ex)
            {
                _logger?.Warn(LogCategory.General, "CivitaiSync",
                    $"Sidecar re-process failed for '{tile.DisplayName}': {ex.Message}");
                skipped++;
            }
        }

        if (reprocessed > 0)
            statusParts.Add($"Sidecar re-processed: {reprocessed}");
    }

    /// <summary>
    /// Returns true if the base model string is a placeholder value ('???' or null/empty).
    /// </summary>
    private static bool IsPlaceholderBaseModel(string? baseModel)
        => string.IsNullOrWhiteSpace(baseModel) || baseModel == "???";

    /// <summary>
    /// Phase 1: Sync metadata for models that have never been synced.
    /// </summary>
    private async Task SyncMetadataPhaseAsync(string? apiKey, List<string> statusParts, CancellationToken ct)
    {
        var tilesNeedingMetadata = AllTiles
            .Where(t => t.ModelEntity is { CivitaiId: null, LastSyncedAt: null }
                        && !(t.ModelEntity?.IsUserEdited ?? false))
            .ToList();

        if (tilesNeedingMetadata.Count == 0)
        {
            _logger?.Debug(LogCategory.Network, "CivitaiSync", "All models already have metadata — skipping Phase 1");
            return;
        }

        _logger?.Info(LogCategory.Network, "CivitaiSync",
            $"Phase 1: {tilesNeedingMetadata.Count} models need metadata");

        var updated = 0;
        var notFound = 0;
        var localFallback = 0;
        var skipped = 0;
        var errors = 0;

        for (var i = 0; i < tilesNeedingMetadata.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tile = tilesNeedingMetadata[i];
            var file = tile.SelectedVersion?.PrimaryFile;
            if (file is null)
            {
                _logger?.Debug(LogCategory.Network, "CivitaiSync",
                    $"SKIP [{i + 1}/{tilesNeedingMetadata.Count}] {tile.DisplayName}: no primary file");
                skipped++;
                continue;
            }

            var localPath = file.LocalPath;
            if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
            {
                _logger?.Debug(LogCategory.Network, "CivitaiSync",
                    $"SKIP [{i + 1}/{tilesNeedingMetadata.Count}] {tile.DisplayName}: file not found at {localPath}");
                skipped++;
                continue;
            }

            Dispatcher.UIThread.Post(() =>
                SyncStatus = $"[{i + 1}/{tilesNeedingMetadata.Count}] Hashing {tile.DisplayName}...");

            try
            {
                // Compute full-file SHA256 — same method as the proven old ComputeSHA256
                var hash = await Task.Run(() => ComputeFullSha256(localPath));

                _logger?.Info(LogCategory.Network, "CivitaiSync",
                    $"[{i + 1}/{tilesNeedingMetadata.Count}] {file.FileName}",
                    $"SHA256: {hash}\nPath: {localPath}\nURL: https://civitai.com/api/v1/model-versions/by-hash/{hash}");

                Dispatcher.UIThread.Post(() =>
                    SyncStatus = $"[{i + 1}/{tilesNeedingMetadata.Count}] Looking up {tile.DisplayName}...");

                // Send API key — authenticated requests get higher rate limits
                var civitaiVersion = await _civitaiClient!.GetModelVersionByHashAsync(hash, apiKey);
                if (civitaiVersion is null)
                {
                    _logger?.Warn(LogCategory.Network, "CivitaiSync",
                        $"NOT FOUND [{i + 1}/{tilesNeedingMetadata.Count}] {file.FileName}",
                        $"Hash {hash} returned 404 from Civitai");
                    notFound++;

                    // Fallback: try to read local .civitai.info / .json metadata files
                    var localFallbackApplied = await TryApplyLocalMetadataFallbackAsync(tile, localPath);
                    if (localFallbackApplied)
                    {
                        _logger?.Info(LogCategory.Network, "CivitaiSync",
                            $"LOCAL FALLBACK [{i + 1}/{tilesNeedingMetadata.Count}] {file.FileName}",
                            "Applied metadata from local .civitai.info / .json file");
                        localFallback++;
                    }

                    // Mark as synced so this model is not retried on every run
                    await MarkModelSyncedAsync(tile.ModelEntity);
                    continue;
                }

                _logger?.Info(LogCategory.Network, "CivitaiSync",
                    $"MATCHED [{i + 1}/{tilesNeedingMetadata.Count}] {file.FileName}",
                    $"→ Model {civitaiVersion.ModelId}, Version {civitaiVersion.Id} ({civitaiVersion.Name})\n" +
                    $"  Base: {civitaiVersion.BaseModel}, Images: {civitaiVersion.Images.Count}, Files: {civitaiVersion.Files.Count}");

                // Update model entity with Civitai data
                await UpdateModelFromCivitaiAsync(tile, civitaiVersion, apiKey);
                updated++;

                _logger?.Info(LogCategory.Network, "CivitaiSync",
                    $"SAVED [{i + 1}/{tilesNeedingMetadata.Count}] {tile.DisplayName} → DB updated");

                Dispatcher.UIThread.Post(() =>
                    SyncStatus = $"[{i + 1}/{tilesNeedingMetadata.Count}] ✓ {tile.DisplayName} ({updated} updated)");
            }
            catch (HttpRequestException ex)
            {
                _logger?.Error(LogCategory.Network, "CivitaiSync",
                    $"HTTP ERROR [{i + 1}/{tilesNeedingMetadata.Count}] {tile.DisplayName}: " +
                    $"Status={ex.StatusCode}, {ex.Message}", ex);
                errors++;
            }
            catch (Exception ex)
            {
                _logger?.Error(LogCategory.Network, "CivitaiSync",
                    $"ERROR [{i + 1}/{tilesNeedingMetadata.Count}] {tile.DisplayName}: {ex.Message}", ex);
                errors++;
            }

            // Civitai rate limit: ~2 requests/sec for authenticated, less for anonymous.
            // The CivitaiClient handles 429 retries internally, but we still pace requests.
            await Task.Delay(1500, ct);
        }

        var parts = new List<string>();
        if (updated > 0) parts.Add($"{updated} updated");
        if (localFallback > 0) parts.Add($"{localFallback} from local files");
        if (notFound > 0) parts.Add($"{notFound} not on Civitai");
        if (errors > 0) parts.Add($"{errors} errors");
        if (skipped > 0) parts.Add($"{skipped} skipped");
        statusParts.Add($"Metadata: {string.Join(", ", parts)}");
    }

    /// <summary>
    /// Phase 2: Re-fetch image data from Civitai for models that were synced but have no preview images.
    /// This happens when the initial sync didn't receive image data (empty images array from the API).
    /// </summary>
    private async Task RefetchMissingImagesPhaseAsync(string? apiKey, List<string> statusParts, CancellationToken ct)
    {
        var tilesNeedingImages = AllTiles
            .Where(t => t.IsImageDataMissing)
            .ToList();

        if (tilesNeedingImages.Count == 0)
        {
            _logger?.Debug(LogCategory.Network, "CivitaiSync", "No synced models missing images — skipping Phase 2");
            return;
        }

        _logger?.Info(LogCategory.Network, "CivitaiSync",
            $"Phase 2: {tilesNeedingImages.Count} synced models have no preview images, re-fetching from Civitai");

        var fetched = 0;
        var fetchErrors = 0;

        for (var i = 0; i < tilesNeedingImages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tile = tilesNeedingImages[i];
            var versionCivitaiId = tile.SelectedVersion?.CivitaiId;
            if (versionCivitaiId is null) continue;

            Dispatcher.UIThread.Post(() =>
                SyncStatus = $"Re-fetching images [{i + 1}/{tilesNeedingImages.Count}] {tile.DisplayName}...");

            try
            {
                var civitaiVersion = await _civitaiClient!.GetModelVersionAsync(versionCivitaiId.Value, apiKey);
                if (civitaiVersion is not null && civitaiVersion.Images.Count > 0)
                {
                    _logger?.Info(LogCategory.Network, "CivitaiSync",
                        $"Re-fetched {civitaiVersion.Images.Count} images for '{tile.DisplayName}'");

                    await UpdateModelFromCivitaiAsync(tile, civitaiVersion, apiKey);
                    fetched++;
                }
                else
                {
                    _logger?.Debug(LogCategory.Network, "CivitaiSync",
                        $"No images available for '{tile.DisplayName}' on Civitai");
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(LogCategory.Network, "CivitaiSync",
                    $"Failed to re-fetch images for '{tile.DisplayName}': {ex.Message}", ex);
                fetchErrors++;
            }

            await Task.Delay(1500, ct);
        }

        if (fetched > 0 || fetchErrors > 0)
        {
            var part = $"Images re-fetched: {fetched}";
            if (fetchErrors > 0) part += $", {fetchErrors} errors";
            statusParts.Add(part);
        }
    }

    /// <summary>
    /// Phase 3: Backfill tags for models that were synced before tag persistence was added.
    /// Uses the existing CivitaiId to fetch from Civitai — no file hashing needed.
    /// Once all models have tags this phase becomes a no-op.
    /// </summary>
    private async Task BackfillMissingTagsPhaseAsync(string? apiKey, List<string> statusParts, CancellationToken ct)
    {
        var tilesNeedingTags = AllTiles
            .Where(t => t.ModelEntity?.CivitaiId is not null and not 0
                        && t.TagNames.Count == 0
                        && t.SelectedVersion?.CivitaiId is not null
                        && !(t.ModelEntity?.IsUserEdited ?? false))
            .ToList();

        if (tilesNeedingTags.Count == 0)
        {
            _logger?.Debug(LogCategory.Network, "CivitaiSync",
                "All synced models already have tags — skipping Phase 3");
            return;
        }

        _logger?.Info(LogCategory.Network, "CivitaiSync",
            $"Phase 3: {tilesNeedingTags.Count} synced models need tag backfill");

        var backfilled = 0;
        var errors = 0;

        for (var i = 0; i < tilesNeedingTags.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tile = tilesNeedingTags[i];
            var versionCivitaiId = tile.SelectedVersion!.CivitaiId!.Value;

            Dispatcher.UIThread.Post(() =>
                SyncStatus = $"Backfilling tags [{i + 1}/{tilesNeedingTags.Count}] {tile.DisplayName}...");

            try
            {
                var civitaiVersion = await _civitaiClient!.GetModelVersionAsync(versionCivitaiId, apiKey);
                if (civitaiVersion is not null)
                {
                    await UpdateModelFromCivitaiAsync(tile, civitaiVersion, apiKey);
                    backfilled++;
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(LogCategory.Network, "CivitaiSync",
                    $"Failed to backfill tags for '{tile.DisplayName}': {ex.Message}", ex);
                errors++;
            }

            // Civitai rate limit pacing
            await Task.Delay(1500, ct);
        }

        if (backfilled > 0 || errors > 0)
        {
            var part = $"Tags backfilled: {backfilled}";
            if (errors > 0) part += $", {errors} errors";
            statusParts.Add(part);
        }
    }

    /// <summary>
    /// Phase 4: Download thumbnails for tiles still showing "No Preview" that have image URLs.
    /// </summary>
    private async Task DownloadMissingThumbnailsPhaseAsync(List<string> statusParts, CancellationToken ct)
    {
        var tilesNeedingThumbs = AllTiles
            .Where(t => t.IsThumbnailMissing)
            .ToList();

        if (tilesNeedingThumbs.Count == 0)
        {
            _logger?.Debug(LogCategory.General, "CivitaiSync", "No tiles with downloadable but missing thumbnails — skipping Phase 4");
            return;
        }

        _logger?.Info(LogCategory.General, "CivitaiSync",
            $"Phase 4: {tilesNeedingThumbs.Count} tiles need thumbnail download");

        var thumbSuccess = 0;
        var thumbFailed = 0;

        for (var i = 0; i < tilesNeedingThumbs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tile = tilesNeedingThumbs[i];
            Dispatcher.UIThread.Post(() =>
                SyncStatus = $"Downloading thumbnail [{i + 1}/{tilesNeedingThumbs.Count}] {tile.DisplayName}...");

            try
            {
                await tile.TryDownloadMissingThumbnailAsync();

                if (!tile.IsThumbnailMissing)
                    thumbSuccess++;
                else
                    thumbFailed++;
            }
            catch (Exception ex)
            {
                _logger?.Error(LogCategory.General, "CivitaiSync",
                    $"Failed thumbnail for '{tile.DisplayName}': {ex.Message}", ex);
                thumbFailed++;
            }

            await Task.Delay(500, ct);
        }

        var thumbPart = $"Thumbnails: {thumbSuccess} downloaded";
        if (thumbFailed > 0) thumbPart += $", {thumbFailed} failed";
        statusParts.Add(thumbPart);
    }

    /// <summary>
    /// Computes the full-file SHA256 hash for Civitai API lookup.
    /// Uses the exact same approach as the proven old LoraMetadataDownloadService.ComputeSHA256.
    /// </summary>
    private static string ComputeFullSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Marks a model as synced (sets LastSyncedAt) so it is not retried on subsequent runs.
    /// Used when the hash lookup returns 404 or the CivitaiId cannot be assigned.
    /// </summary>
    private static async Task MarkModelSyncedAsync(Model? model)
    {
        if (model is null) return;

        try
        {
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var dbModel = await unitOfWork.Models.GetByIdAsync(model.Id);
            if (dbModel is not null)
            {
                dbModel.LastSyncedAt = DateTimeOffset.UtcNow;
                await unitOfWork.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to mark model {Id} as synced", model.Id);
        }
    }

    /// <summary>
    /// Fallback: reads local .civitai.info or .json metadata files next to the safetensors file
    /// and applies the discovered metadata to the model entity in the database.
    /// Also discovers local preview images (same base name, image extension) and stores
    /// them as thumbnail BLOBs so tiles show a preview without Civitai.
    /// <para>
    /// Two sidecar formats are supported:
    /// <list type="bullet">
    ///   <item><c>.civitai.info</c> — version-level Civitai response (top-level: id, modelId, baseModel, trainedWords, model{}, images[], files[])</item>
    ///   <item><c>.json</c> — model-level Civitai response (top-level: id, name, type, nsfw, modelVersions[]) OR simple metadata (sd version, tags)</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <returns>True if any local metadata was found and applied.</returns>
    private async Task<bool> TryApplyLocalMetadataFallbackAsync(ModelTileViewModel tile, string localPath)
    {
        try
        {
            var fileInfo = new FileInfo(localPath);
            var directory = fileInfo.Directory;
            if (directory is null || !directory.Exists) return false;

            var baseName = Path.GetFileNameWithoutExtension(fileInfo.Name);

            // Look for .civitai.info or .json sidecar files
            var civitaiInfoFile = directory.GetFiles($"{baseName}.civitai.info").FirstOrDefault();
            var jsonFile = directory.GetFiles($"{baseName}.json").FirstOrDefault();

            if (civitaiInfoFile is null && jsonFile is null)
            {
                // No sidecar metadata — still try local thumbnail
                var thumbnailApplied = await TryApplyLocalThumbnailAsync(tile, directory, baseName);
                return thumbnailApplied;
            }

            // Parse the sidecar file — prefer .civitai.info (richer version-level data)
            var metadataFile = civitaiInfoFile ?? jsonFile!;
            var isCivitaiInfoFormat = civitaiInfoFile is not null;
            var json = await File.ReadAllTextAsync(metadataFile.FullName);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Use a fresh DI scope for DB writes
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var model = tile.ModelEntity;
            if (model is null) return false;

            // Load ONLY the target model — not the entire database
            var dbModel = await unitOfWork.Models.GetByIdWithIncludesAsync(model.Id);
            if (dbModel is null) return false;

            var dirty = false;

            // Locate the DB version that owns the file at localPath
            var dbVersion = dbModel.Versions.FirstOrDefault(v =>
                v.Files.Any(f => string.Equals(f.LocalPath, localPath, StringComparison.OrdinalIgnoreCase)));

            if (isCivitaiInfoFormat)
            {
                dirty = await ApplyCivitaiInfoFormatAsync(root, dbModel, dbVersion, unitOfWork.Models, localPath);
            }
            else
            {
                // .json may be model-level (has "modelVersions") or simple metadata (has "sd version")
                dirty = root.TryGetProperty("modelVersions", out _)
                    ? await ApplyModelLevelJsonFormatAsync(root, dbModel, dbVersion, unitOfWork.Models, localPath)
                    : ApplySimpleJsonFormat(root, dbModel, dbVersion);
            }

            // Always mark source and sync time
            dbModel.Source = DataSource.LocalFile;
            dbModel.LastSyncedAt = DateTimeOffset.UtcNow;
            dirty = true;

            if (dirty)
            {
                await unitOfWork.SaveChangesAsync();

                // Reload ONLY this model after save
                var refreshedModel = await unitOfWork.Models.GetByIdWithIncludesAsync(model.Id);
                if (refreshedModel is not null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => tile.RefreshModelData(refreshedModel));
                }
            }

            // Try to find and apply a local thumbnail image
            await TryApplyLocalThumbnailAsync(tile, directory, baseName);

            return dirty;
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.General, "LocalFallback",
                $"Failed to read local metadata for '{tile.DisplayName}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Applies metadata from a .civitai.info sidecar (version-level Civitai response).
    /// Extracts: model name, NSFW, modelId → CivitaiId/CivitaiModelPageId,
    /// version id → CivitaiId, baseModel, trainedWords, image URLs, file hashes.
    /// </summary>
    private async Task<bool> ApplyCivitaiInfoFormatAsync(
        System.Text.Json.JsonElement root,
        Model dbModel,
        ModelVersion? dbVersion,
        IModelRepository modelRepo,
        string localPath)
    {
        var dirty = false;

        // ── Model-level fields from nested "model" object ──
        if (root.TryGetProperty("model", out var modelProp))
        {
            if (modelProp.TryGetProperty("name", out var name) && name.GetString() is { } nameStr)
            {
                dbModel.Name = nameStr;
                dirty = true;
            }

            if (modelProp.TryGetProperty("nsfw", out var nsfw)
                && nsfw.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            {
                dbModel.IsNsfw = nsfw.GetBoolean();
                dirty = true;
            }
        }

        // ── CivitaiId / CivitaiModelPageId from "modelId" ──
        if (root.TryGetProperty("modelId", out var modelIdProp) && modelIdProp.TryGetInt32(out var civitaiModelId))
        {
            dbModel.CivitaiModelPageId = civitaiModelId;

            var civitaiIdTaken = await modelRepo.IsCivitaiIdTakenAsync(civitaiModelId, dbModel.Id);
            if (!civitaiIdTaken)
            {
                dbModel.CivitaiId = civitaiModelId;
            }

            dirty = true;
        }

        // ── Version-level fields ──
        if (dbVersion is not null)
        {
            // Version CivitaiId from top-level "id"
            if (root.TryGetProperty("id", out var versionIdProp) && versionIdProp.TryGetInt32(out var civitaiVersionId))
            {
                var versionIdTaken = await modelRepo.IsVersionCivitaiIdTakenAsync(civitaiVersionId, dbVersion.Id);
                if (!versionIdTaken)
                {
                    dbVersion.CivitaiId = civitaiVersionId;
                    dirty = true;
                }
            }

            // Version name
            if (root.TryGetProperty("name", out var vName) && vName.GetString() is { } vNameStr)
            {
                dbVersion.Name = vNameStr;
                dirty = true;
            }

            // Base model
            if (root.TryGetProperty("baseModel", out var baseModel) && baseModel.GetString() is { } baseModelStr)
            {
                dbVersion.BaseModelRaw = baseModelStr;
                dirty = true;
            }

            // Trained words / trigger words
            if (root.TryGetProperty("trainedWords", out var trained) && trained.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                dbVersion.TriggerWords.Clear();
                var order = 0;
                foreach (var wordElement in trained.EnumerateArray())
                {
                    if (wordElement.ValueKind == System.Text.Json.JsonValueKind.String && wordElement.GetString() is { } word)
                    {
                        dbVersion.TriggerWords.Add(new TriggerWord { Word = word, Order = order++ });
                        dirty = true;
                    }
                }
            }

            // Download URL
            if (root.TryGetProperty("downloadUrl", out var dlUrl) && dlUrl.GetString() is { } dlUrlStr)
            {
                dbVersion.DownloadUrl = dlUrlStr;
                dirty = true;
            }

            // ── Image URLs from "images" array ──
            if (root.TryGetProperty("images", out var images) && images.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                dirty |= ApplyImagesFromJson(dbVersion, images);
            }

            // ── File hashes from "files" array ──
            if (root.TryGetProperty("files", out var files) && files.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                dirty |= ApplyFileHashesFromJson(dbVersion, files, localPath);
            }
        }

        return dirty;
    }

    /// <summary>
    /// Applies metadata from a .json sidecar that is a model-level Civitai response
    /// (has top-level id, name, type, nsfw, modelVersions[]).
    /// Finds the matching version by filename in the modelVersions array.
    /// </summary>
    private async Task<bool> ApplyModelLevelJsonFormatAsync(
        System.Text.Json.JsonElement root,
        Model dbModel,
        ModelVersion? dbVersion,
        IModelRepository modelRepo,
        string localPath)
    {
        var dirty = false;
        var localFileName = Path.GetFileName(localPath);

        // ── Model-level fields ──
        if (root.TryGetProperty("name", out var name) && name.GetString() is { } nameStr)
        {
            dbModel.Name = nameStr;
            dirty = true;
        }

        if (root.TryGetProperty("nsfw", out var nsfw)
            && nsfw.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
        {
            dbModel.IsNsfw = nsfw.GetBoolean();
            dirty = true;
        }

        // CivitaiId / CivitaiModelPageId from top-level "id"
        if (root.TryGetProperty("id", out var modelIdProp) && modelIdProp.TryGetInt32(out var civitaiModelId))
        {
            dbModel.CivitaiModelPageId = civitaiModelId;

            var civitaiIdTaken = await modelRepo.IsCivitaiIdTakenAsync(civitaiModelId, dbModel.Id);
            if (!civitaiIdTaken)
            {
                dbModel.CivitaiId = civitaiModelId;
            }

            dirty = true;
        }

        // ── Find the matching version in modelVersions[] by filename ──
        if (dbVersion is not null
            && root.TryGetProperty("modelVersions", out var versions)
            && versions.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var versionElement in versions.EnumerateArray())
            {
                // Match by filename in the "files" array
                var matchedByFile = false;
                if (versionElement.TryGetProperty("files", out var vFiles) && vFiles.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var fileElement in vFiles.EnumerateArray())
                    {
                        if (fileElement.TryGetProperty("name", out var fName)
                            && string.Equals(fName.GetString(), localFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedByFile = true;
                            break;
                        }
                    }
                }

                if (!matchedByFile) continue;

                // Found the matching version — extract all data
                if (versionElement.TryGetProperty("id", out var vIdProp) && vIdProp.TryGetInt32(out var civitaiVersionId))
                {
                    var versionIdTaken = await modelRepo.IsVersionCivitaiIdTakenAsync(civitaiVersionId, dbVersion.Id);
                    if (!versionIdTaken)
                    {
                        dbVersion.CivitaiId = civitaiVersionId;
                        dirty = true;
                    }
                }

                if (versionElement.TryGetProperty("name", out var vName) && vName.GetString() is { } vNameStr)
                {
                    dbVersion.Name = vNameStr;
                    dirty = true;
                }

                if (versionElement.TryGetProperty("baseModel", out var baseModel) && baseModel.GetString() is { } baseModelStr)
                {
                    dbVersion.BaseModelRaw = baseModelStr;
                    dirty = true;
                }

                if (versionElement.TryGetProperty("trainedWords", out var trained) && trained.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    dbVersion.TriggerWords.Clear();
                    var order = 0;
                    foreach (var wordElement in trained.EnumerateArray())
                    {
                        if (wordElement.ValueKind == System.Text.Json.JsonValueKind.String && wordElement.GetString() is { } word)
                        {
                            dbVersion.TriggerWords.Add(new TriggerWord { Word = word, Order = order++ });
                            dirty = true;
                        }
                    }
                }

                if (versionElement.TryGetProperty("downloadUrl", out var dlUrl) && dlUrl.GetString() is { } dlUrlStr)
                {
                    dbVersion.DownloadUrl = dlUrlStr;
                    dirty = true;
                }

                // Images from this version
                if (versionElement.TryGetProperty("images", out var images) && images.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    dirty |= ApplyImagesFromJson(dbVersion, images);
                }

                // File hashes from this version
                if (versionElement.TryGetProperty("files", out var filesArr) && filesArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    dirty |= ApplyFileHashesFromJson(dbVersion, filesArr, localPath);
                }

                break; // Only match one version per file
            }
        }

        return dirty;
    }

    /// <summary>
    /// Applies metadata from a simple .json sidecar (has "sd version", "type", "tags" — not a full Civitai response).
    /// </summary>
    private static bool ApplySimpleJsonFormat(
        System.Text.Json.JsonElement root,
        Model dbModel,
        ModelVersion? dbVersion)
    {
        var dirty = false;

        if (root.TryGetProperty("model", out var modelProp))
        {
            if (modelProp.TryGetProperty("name", out var name) && name.GetString() is { } nameStr)
            {
                dbModel.Name = nameStr;
                dirty = true;
            }

            if (modelProp.TryGetProperty("nsfw", out var nsfw)
                && nsfw.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            {
                dbModel.IsNsfw = nsfw.GetBoolean();
                dirty = true;
            }
        }

        if (dbVersion is not null)
        {
            if (root.TryGetProperty("baseModel", out var baseModel) && baseModel.GetString() is { } baseModelStr)
            {
                dbVersion.BaseModelRaw = baseModelStr;
                dirty = true;
            }

            // Fallback: "sd version" for very old sidecar format
            if (dbVersion.BaseModelRaw is null or "???"
                && root.TryGetProperty("sd version", out var sdVer) && sdVer.GetString() is { } sdVerStr)
            {
                dbVersion.BaseModelRaw = sdVerStr;
                dirty = true;
            }

            if (root.TryGetProperty("trainedWords", out var trained) && trained.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                dbVersion.TriggerWords.Clear();
                var order = 0;
                foreach (var wordElement in trained.EnumerateArray())
                {
                    if (wordElement.ValueKind == System.Text.Json.JsonValueKind.String && wordElement.GetString() is { } word)
                    {
                        dbVersion.TriggerWords.Add(new TriggerWord { Word = word, Order = order++ });
                        dirty = true;
                    }
                }
            }
        }

        return dirty;
    }

    /// <summary>
    /// Creates <see cref="ModelImage"/> entities from a JSON "images" array found in sidecar files.
    /// Skips images that already exist by CivitaiId or URL.
    /// </summary>
    private static bool ApplyImagesFromJson(ModelVersion dbVersion, System.Text.Json.JsonElement imagesArray)
    {
        var dirty = false;
        var existingUrls = dbVersion.Images
            .Select(i => i.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sortOrder = dbVersion.Images.Count;

        foreach (var imgEl in imagesArray.EnumerateArray())
        {
            var url = imgEl.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
            if (string.IsNullOrEmpty(url)) continue;

            // Skip if we already have this URL
            if (existingUrls.Contains(url)) continue;

            var image = new ModelImage
            {
                ModelVersionId = dbVersion.Id,
                Url = url,
                SortOrder = sortOrder++,
            };

            if (imgEl.TryGetProperty("width", out var w) && w.TryGetInt32(out var width)) image.Width = width;
            if (imgEl.TryGetProperty("height", out var h) && h.TryGetInt32(out var height)) image.Height = height;
            if (imgEl.TryGetProperty("hash", out var hash) && hash.GetString() is { } hashStr) image.BlurHash = hashStr;
            if (imgEl.TryGetProperty("type", out var type) && type.GetString() is { } typeStr) image.MediaType = typeStr;

            if (imgEl.TryGetProperty("nsfwLevel", out var nsfwLvl) && nsfwLvl.TryGetInt32(out var nsfwLevel))
            {
                image.IsNsfw = nsfwLevel > 1;
            }

            dbVersion.Images.Add(image);
            existingUrls.Add(url);
            dirty = true;
        }

        return dirty;
    }

    /// <summary>
    /// Applies file hashes from a JSON "files" array to the matching <see cref="ModelFile"/> entity.
    /// Matches by filename comparison with the local file.
    /// </summary>
    private static bool ApplyFileHashesFromJson(
        ModelVersion dbVersion,
        System.Text.Json.JsonElement filesArray,
        string localPath)
    {
        var dirty = false;
        var localFileName = Path.GetFileName(localPath);

        foreach (var fileEl in filesArray.EnumerateArray())
        {
            var fileName = fileEl.TryGetProperty("name", out var fName) ? fName.GetString() : null;
            if (!string.Equals(fileName, localFileName, StringComparison.OrdinalIgnoreCase)) continue;

            var dbFile = dbVersion.Files.FirstOrDefault(f =>
                string.Equals(f.FileName, localFileName, StringComparison.OrdinalIgnoreCase))
                ?? dbVersion.PrimaryFile;

            if (dbFile is null) break;

            // File CivitaiId
            if (fileEl.TryGetProperty("id", out var fId) && fId.TryGetInt32(out var fileId))
            {
                dbFile.CivitaiId = fileId;
                dirty = true;
            }

            // File size
            if (fileEl.TryGetProperty("sizeKB", out var sizeKb) && sizeKb.TryGetDouble(out var sizeVal))
            {
                dbFile.SizeKB = sizeVal;
                dirty = true;
            }

            // Hashes
            if (fileEl.TryGetProperty("hashes", out var hashes))
            {
                if (hashes.TryGetProperty("SHA256", out var sha256) && sha256.GetString() is { } sha256Str)
                { dbFile.HashSHA256 ??= sha256Str; dirty = true; }

                if (hashes.TryGetProperty("AutoV2", out var autoV2) && autoV2.GetString() is { } autoV2Str)
                { dbFile.HashAutoV2 ??= autoV2Str; dirty = true; }

                if (hashes.TryGetProperty("CRC32", out var crc32) && crc32.GetString() is { } crc32Str)
                { dbFile.HashCRC32 ??= crc32Str; dirty = true; }

                if (hashes.TryGetProperty("BLAKE3", out var blake3) && blake3.GetString() is { } blake3Str)
                { dbFile.HashBLAKE3 ??= blake3Str; dirty = true; }

                if (hashes.TryGetProperty("AutoV1", out var autoV1) && autoV1.GetString() is { } autoV1Str)
                { dbFile.HashAutoV1 ??= autoV1Str; dirty = true; }
            }

            // Download URL
            if (fileEl.TryGetProperty("downloadUrl", out var dlUrl) && dlUrl.GetString() is { } dlUrlStr)
            {
                dbFile.DownloadUrl ??= dlUrlStr;
                dirty = true;
            }

            break; // Only match one file entry per local file
        }

        return dirty;
    }

    /// <summary>
    /// Image extensions to search for when looking for local preview images alongside model files.
    /// Ordered by preference (preview-specific suffixes first, then common formats).
    /// </summary>
    private static readonly string[] LocalPreviewExtensions =
    [
        ".preview.png", ".preview.jpg", ".preview.jpeg", ".preview.webp",
        ".thumb.jpg",
        ".png", ".jpg", ".jpeg", ".webp"
    ];

    /// <summary>
    /// Searches for a local preview image next to the model file (same base name, image extension)
    /// and stores it as a thumbnail BLOB on the primary image entity.
    /// </summary>
    /// <returns>True if a local thumbnail was found and applied.</returns>
    private async Task<bool> TryApplyLocalThumbnailAsync(ModelTileViewModel tile, DirectoryInfo directory, string baseName)
    {
        try
        {
            // Search for local preview images by base name + known image extensions
            string? localImagePath = null;
            foreach (var ext in LocalPreviewExtensions)
            {
                var candidate = Path.Combine(directory.FullName, baseName + ext);
                if (File.Exists(candidate))
                {
                    localImagePath = candidate;
                    break;
                }
            }

            if (localImagePath is null) return false;

            _logger?.Debug(LogCategory.General, "LocalFallback",
                $"Found local preview for '{tile.DisplayName}': {Path.GetFileName(localImagePath)}");

            // Read and transcode the local image to JPEG for BLOB storage
            var imageBytes = await File.ReadAllBytesAsync(localImagePath);
            if (imageBytes.Length == 0) return false;

            byte[] thumbnailBytes;
            using (var skBitmap = SKBitmap.Decode(imageBytes))
            {
                if (skBitmap is null) return false;

                // Resize to thumbnail width (340px) to keep BLOB small
                var targetWidth = 340;
                var scale = (float)targetWidth / skBitmap.Width;
                var targetHeight = (int)(skBitmap.Height * scale);
                using var resized = skBitmap.Resize(new SKImageInfo(targetWidth, targetHeight), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (resized is null) return false;

                using var skImage = SKImage.FromBitmap(resized);
                using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, 85);
                thumbnailBytes = encoded.ToArray();
            }

            if (thumbnailBytes.Length == 0) return false;

            // Store in DB on the primary image (create one if needed)
            var version = tile.SelectedVersion;
            if (version is null) return false;

            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var modelId = tile.ModelEntity?.Id;
            if (modelId is null or 0) return false;
            var dbModel = await unitOfWork.Models.GetByIdWithIncludesAsync(modelId.Value);
            var dbVersion = dbModel?.Versions.FirstOrDefault(v =>
                v.Files.Any(f => string.Equals(f.LocalPath, tile.SelectedVersion?.PrimaryFile?.LocalPath, StringComparison.OrdinalIgnoreCase)));

            if (dbVersion is null) return false;

            var primaryImage = dbVersion.Images.FirstOrDefault();
            if (primaryImage is null)
            {
                // Create a new image entity for the local thumbnail
                primaryImage = new ModelImage
                {
                    ModelVersionId = dbVersion.Id,
                    Url = $"file://{localImagePath}",
                    SortOrder = 0,
                };
                dbVersion.Images.Add(primaryImage);
            }

            primaryImage.ThumbnailData = thumbnailBytes;
            primaryImage.ThumbnailMimeType = "image/jpeg";

            await unitOfWork.SaveChangesAsync();

            // Display the thumbnail immediately
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    using var stream = new MemoryStream(thumbnailBytes);
                    tile.ThumbnailImage = new Avalonia.Media.Imaging.Bitmap(stream);
                }
                catch
                {
                    // Decode failure — not critical
                }
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.General, "LocalFallback",
                $"Failed to apply local thumbnail for '{tile.DisplayName}': {ex.Message}");
            return false;
        }
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
    /// Updates a model entity from Civitai API response data.
    /// </summary>
    private async Task UpdateModelFromCivitaiAsync(
        ModelTileViewModel tile,
        CivitaiModelVersion civitaiVersion,
        string? apiKey)
    {
        // Fetch the full model to get all versions (richer data including images)
        CivitaiModel? civitaiModel = null;
        if (civitaiVersion.ModelId > 0)
        {
            civitaiModel = await _civitaiClient!.GetModelAsync(civitaiVersion.ModelId, apiKey);
        }

        // Resolve the best image source: the full model response is more reliable than
        // the hash-lookup response which often returns an empty images array.
        var bestCivitaiVersion = civitaiModel?.ModelVersions
            .FirstOrDefault(v => v.Id == civitaiVersion.Id) ?? civitaiVersion;

        // Use a fresh DI scope for DB writes to avoid concurrent DbContext access
        using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var model = tile.ModelEntity;
        if (model is null) return;

        // Load ONLY the target model — not the entire database (was the #1 cause of OOM)
        var dbModel = await unitOfWork.Models.GetByIdWithIncludesAsync(model.Id);
        if (dbModel is null) return;

        // Update model-level fields from Civitai
        // Skip overwriting user-edited fields (description, tags, etc.) — IsUserEdited is sticky.
        var preserveModelEdits = dbModel.IsUserEdited;
        if (civitaiModel is not null)
        {
            // Always set the grouping key — not unique, safe for all models sharing the same Civitai page
            dbModel.CivitaiModelPageId = civitaiModel.Id;

            // Only assign CivitaiId if no other model already owns it (prevents UNIQUE constraint violation)
            var civitaiIdTaken = await unitOfWork.Models.IsCivitaiIdTakenAsync(civitaiModel.Id, dbModel.Id);
            if (!civitaiIdTaken)
            {
                dbModel.CivitaiId = civitaiModel.Id;
            }
            else
            {
                _logger?.Warn(LogCategory.Network, "CivitaiSync",
                    $"Skipping CivitaiId {civitaiModel.Id} for model '{dbModel.Name}' (Id={dbModel.Id}): " +
                    "already assigned to another model");
            }

            if (!preserveModelEdits)
            {
                dbModel.Name = civitaiModel.Name;
                dbModel.Description = civitaiModel.Description;
            }
            dbModel.IsNsfw = civitaiModel.Nsfw;
            dbModel.IsPoi = civitaiModel.Poi;
            dbModel.Source = DataSource.CivitaiApi;
            dbModel.LastSyncedAt = DateTimeOffset.UtcNow;
            // Capture version-availability data from the same response so the
            // "+N more versions" badge can be shown without an additional HTTP call.
            dbModel.TotalVersionCount = civitaiModel.ModelVersions?.Count ?? 0;
            dbModel.LastCheckedForUpdatesUtc = DateTime.UtcNow;
            dbModel.AllowNoCredit = civitaiModel.AllowNoCredit;
            dbModel.AllowDerivatives = civitaiModel.AllowDerivatives;
            dbModel.AllowDifferentLicense = civitaiModel.AllowDifferentLicense;

            // Update or create creator — reuse existing Creator entity by
            // Username to avoid UNIQUE constraint violations.
            if (civitaiModel.Creator is not null)
            {
                if (dbModel.Creator is not null)
                {
                    dbModel.Creator.Username = civitaiModel.Creator.Username;
                    dbModel.Creator.AvatarUrl ??= civitaiModel.Creator.Image;
                }
                else
                {
                    var existingCreator = await unitOfWork.Models
                        .FindCreatorByUsernameAsync(civitaiModel.Creator.Username);

                    dbModel.Creator = existingCreator ?? new Creator
                    {
                        Username = civitaiModel.Creator.Username,
                        AvatarUrl = civitaiModel.Creator.Image,
                    };
                }
            }
        }

        // Update version-level fields for the matched version
        var dbVersion = dbModel.Versions.FirstOrDefault(v =>
            v.Files.Any(f => f.Id == tile.SelectedVersion?.PrimaryFile?.Id));

        if (dbVersion is not null)
        {
            // Only assign CivitaiId if no other version already owns it (prevents UNIQUE constraint violation)
            var versionIdTaken = await unitOfWork.Models
                .IsVersionCivitaiIdTakenAsync(bestCivitaiVersion.Id, dbVersion.Id);
            if (!versionIdTaken)
            {
                dbVersion.CivitaiId = bestCivitaiVersion.Id;
            }
            else
            {
                _logger?.Warn(LogCategory.Network, "CivitaiSync",
                    $"Skipping CivitaiId {bestCivitaiVersion.Id} for version '{dbVersion.Name}' (Id={dbVersion.Id}): " +
                    "already assigned to another version");
            }

            dbVersion.Name = bestCivitaiVersion.Name;
            dbVersion.Description = bestCivitaiVersion.Description;
            dbVersion.BaseModelRaw = bestCivitaiVersion.BaseModel;
            dbVersion.DownloadUrl = bestCivitaiVersion.DownloadUrl;
            dbVersion.DownloadCount = bestCivitaiVersion.Stats?.DownloadCount ?? 0;
            dbVersion.PublishedAt = bestCivitaiVersion.PublishedAt;
            dbVersion.EarlyAccessDays = bestCivitaiVersion.EarlyAccessTimeFrame;

            // Update trigger words — skip when this version was user-edited
            if (!dbVersion.IsUserEdited)
            {
                dbVersion.TriggerWords.Clear();
                var order = 0;
                foreach (var word in bestCivitaiVersion.TrainedWords)
                {
                    dbVersion.TriggerWords.Add(new TriggerWord
                    {
                        Word = word,
                        Order = order++
                    });
                }
            }

            // Add images from Civitai (use the version with the richest data)
            var existingImageIds = dbVersion.Images
                .Where(i => i.CivitaiId.HasValue)
                .Select(i => i.CivitaiId!.Value)
                .ToHashSet();
            var sortOrder = dbVersion.Images.Count;
            foreach (var civImage in bestCivitaiVersion.Images)
            {
                if (string.IsNullOrEmpty(civImage.Url))
                    continue;

                if (civImage.Id.HasValue && existingImageIds.Contains(civImage.Id.Value))
                    continue;

                dbVersion.Images.Add(new ModelImage
                {
                    ModelVersionId = dbVersion.Id,
                    CivitaiId = civImage.Id,
                    Url = civImage.Url,
                    MediaType = civImage.Type,
                    IsNsfw = civImage.Nsfw,
                    Width = civImage.Width ?? 0,
                    Height = civImage.Height ?? 0,
                    BlurHash = civImage.Hash,
                    SortOrder = sortOrder++,
                    CreatedAt = civImage.CreatedAt,
                    PostId = civImage.PostId,
                    Username = civImage.Username,
                    Prompt = civImage.Meta?.Prompt,
                    NegativePrompt = civImage.Meta?.NegativePrompt,
                    Seed = civImage.Meta?.Seed,
                    Steps = civImage.Meta?.Steps,
                    Sampler = civImage.Meta?.Sampler,
                    CfgScale = civImage.Meta?.CfgScale,
                });
            }

            // Update file hashes from Civitai data
            var civFile = bestCivitaiVersion.Files.FirstOrDefault(f => f.Primary == true);
            if (civFile?.Hashes is not null)
            {
                var dbFile = dbVersion.PrimaryFile;
                if (dbFile is not null)
                {
                    dbFile.CivitaiId = civFile.Id;
                    dbFile.HashSHA256 ??= civFile.Hashes.SHA256;
                    dbFile.HashAutoV2 ??= civFile.Hashes.AutoV2;
                    dbFile.HashCRC32 ??= civFile.Hashes.CRC32;
                    dbFile.HashBLAKE3 ??= civFile.Hashes.BLAKE3;
                }
            }
        }

        // Sync tags from Civitai model response (skip when user has edited tags)
        if (!preserveModelEdits && civitaiModel?.Tags is { Count: > 0 } civitaiTags)
        {
            var tagLookup = await unitOfWork.Models.GetAllTagsLookupAsync();
            SyncTagsFromCivitai(dbModel, civitaiTags, tagLookup);
        }

        await unitOfWork.SaveChangesAsync();

        // Reload ONLY this model after save to get generated IDs on new images
        var refreshedModel = await unitOfWork.Models.GetByIdWithIncludesAsync(model.Id);

        // Refresh tile on UI thread with updated data — use RefreshModelData to
        // properly update _allGroupedModels and re-pick the primary entity.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            tile.RefreshModelData(refreshedModel ?? dbModel);
        });
    }

    /// <summary>
    /// Replaces a model's tags with the Civitai tag list, reusing existing <see cref="Tag"/>
    /// rows by <see cref="Tag.NormalizedName"/> to avoid duplicates in the Tags table.
    /// </summary>
    private static void SyncTagsFromCivitai(
        Model dbModel,
        IReadOnlyList<string> civitaiTags,
        Dictionary<string, Tag> knownTags)
    {
        dbModel.Tags.Clear();

        foreach (var tagName in civitaiTags)
        {
            if (string.IsNullOrWhiteSpace(tagName)) continue;

            var normalized = tagName.Trim().ToLowerInvariant();

            if (!knownTags.TryGetValue(normalized, out var tag))
            {
                tag = new Tag { Name = tagName, NormalizedName = normalized };
                knownTags[normalized] = tag;
            }

            dbModel.Tags.Add(new ModelTag { Tag = tag });
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
                // UpdateModelFromCivitaiAsync already refreshed the in-memory tile (it now
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
    /// Downloads Civitai metadata for a single LoRA (the detail-view "Download Metadata"
    /// button). Mirrors Phase 1 of <see cref="DownloadMissingMetadataAsync"/> scoped to one
    /// tile: hashes the primary file, looks it up on Civitai by hash, and persists the
    /// returned metadata via <see cref="UpdateModelFromCivitaiAsync"/> (which also refreshes
    /// the in-memory tile). Falls back to local <c>.civitai.info</c>/<c>.json</c> sidecars on
    /// a 404. Unlike the bulk flow this runs even for already-synced models, so the user can
    /// force a re-fetch. Returns <c>true</c> when metadata (remote or local) was applied.
    /// </summary>
    public async Task<bool> DownloadMetadataForTileAsync(ModelTileViewModel tile)
    {
        if (_civitaiClient is null)
        {
            SyncStatus = "Civitai client not available.";
            return false;
        }

        var file = tile.SelectedVersion?.PrimaryFile;
        var localPath = file?.LocalPath;
        if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
        {
            _logger?.Warn(LogCategory.Network, "CivitaiSync",
                $"Cannot download metadata for '{tile.DisplayName}': no local file on disk.");
            return false;
        }

        // Fresh DI scope so we read the latest API key from the database, not a stale
        // tracked entity (same approach as the bulk DownloadMissingMetadataAsync).
        string? apiKey;
        using (var keyScope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope())
        {
            var freshSettings = keyScope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            apiKey = await freshSettings.GetCivitaiApiKeyAsync();
        }

        var hash = await Task.Run(() => ComputeFullSha256(localPath));
        _logger?.Info(LogCategory.Network, "CivitaiSync",
            $"Metadata download for '{tile.DisplayName}'",
            $"SHA256: {hash}\nPath: {localPath}\nURL: https://civitai.com/api/v1/model-versions/by-hash/{hash}");

        var civitaiVersion = await _civitaiClient.GetModelVersionByHashAsync(hash, apiKey);
        if (civitaiVersion is null)
        {
            // 404 on Civitai — try the local .civitai.info / .json sidecar, then mark
            // synced so the bulk flow doesn't keep retrying it.
            var localApplied = await TryApplyLocalMetadataFallbackAsync(tile, localPath);
            await MarkModelSyncedAsync(tile.ModelEntity);
            return localApplied;
        }

        await UpdateModelFromCivitaiAsync(tile, civitaiVersion, apiKey);

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
        foreach (var item in AvailableBaseModels)
        {
            item.IsSelected = false;
        }
    }

    /// <summary>
    /// Reset all filters.
    /// </summary>
    [RelayCommand]
    private void ResetFilters()
    {
        SearchText = null;

        foreach (var item in AvailableBaseModels)
        {
            item.IsSelected = false;
        }

        ApplyFilters();
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
    /// Rebuilds <see cref="AvailableBaseModels"/>. Primary source is the full Civitai
    /// catalog (<see cref="ICivitaiBaseModelCatalog"/>) so users can filter for any
    /// base model Civitai supports, not just ones installed locally. Falls back to
    /// distinct <c>BaseModelRaw</c> values across installed tiles when the catalog
    /// is unavailable (design-time, missing DI, or before initial load completes).
    /// Preserves existing selections where the value still exists.
    /// </summary>
    private void RebuildAvailableBaseModels()
    {
        IReadOnlyList<string> source;
        if (_catalogBaseModels is { Count: > 0 } catalog)
        {
            // Catalog is the canonical source. Preserve catalog order (Civitai's natural
            // ordering — alphabetizing would split related entries like "SDXL 1.0" / "SDXL Turbo").
            source = catalog;
        }
        else
        {
            // Fallback: distinct values from installed tiles, alphabetical.
            source = AllTiles
                .SelectMany(t => t.Versions)
                .Select(v => v.BaseModelRaw)
                .Where(raw => !string.IsNullOrWhiteSpace(raw))
                .Select(raw => raw!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
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

        foreach (var raw in source)
        {
            var item = new BaseModelFilterItem(raw)
            {
                IsSelected = previouslySelected.Contains(raw)
            };
            item.SelectionChanged += OnBaseModelFilterChanged;
            AvailableBaseModels.Add(item);
        }
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

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _catalogBaseModels = labels;
                RebuildAvailableBaseModels();
            });
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
        OnPropertyChanged(nameof(IsBaseModelFilterActive));
        OnPropertyChanged(nameof(ActiveBaseModelFilterCount));
        ApplyFilters();
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

        if (activeBaseModels.Count > 0)
        {
            query = query.Where(t =>
                t.Versions.Any(v =>
                    !string.IsNullOrEmpty(v.BaseModelRaw) &&
                    activeBaseModels.Contains(v.BaseModelRaw)));
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
