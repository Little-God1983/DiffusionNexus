using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Infrastructure;
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
    private readonly ISecureStorage? _secureStorage;
    private readonly IUnifiedLogger? _logger;

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
    private bool _showNsfw;

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
    /// Whether any base model filter is currently active (for visual indicator on the filter button).
    /// </summary>
    public bool IsBaseModelFilterActive => AvailableBaseModels.Any(f => f.IsSelected);

    /// <summary>
    /// Count of currently active base model filters.
    /// </summary>
    public int ActiveBaseModelFilterCount => AvailableBaseModels.Count(f => f.IsSelected);

    #endregion

    #region Collections

    /// <summary>
    /// All model tiles.
    /// </summary>
    public ObservableCollection<ModelTileViewModel> AllTiles { get; } = [];

    /// <summary>
    /// Filtered model tiles for display.
    /// </summary>
    public ObservableCollection<ModelTileViewModel> FilteredTiles { get; } = [];

    /// <summary>
    /// Distinct base model names available for filtering, built from all tiles.
    /// </summary>
    public ObservableCollection<BaseModelFilterItem> AvailableBaseModels { get; } = [];

    #endregion

    #region Constructors

    /// <summary>
    /// Design-time constructor with demo data.
    /// </summary>
    public LoraViewerViewModel()
    {
        _settingsService = null;
        _syncService = null;
        _civitaiClient = null;
        _secureStorage = null;
        _logger = null;
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
        IUnifiedLogger? logger = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _civitaiClient = civitaiClient;
        _secureStorage = secureStorage;
        _logger = logger;
    }

    #endregion

    #region Commands

    /// <summary>
    /// Refresh the model list.
    /// Uses database-first approach: load cached data immediately, then discover new files.
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

        try
        {
            IsBusy = true;
            BusyMessage = "Loading models...";
            SyncStatus = "Starting refresh...";

            // Offload all heavy I/O (file discovery + DB reads + grouping) to a
            // single background task so the UI thread stays responsive.
            // DiscoverNewFilesAsync already marshals progress to the UI via Dispatcher.Post.
            var (allModels, tiles) = await Task.Run(async () =>
            {
                await DiscoverNewFilesAsync();
                await BackfillCivitaiModelPageIdAsync();

                Dispatcher.UIThread.Post(() => SyncStatus = "Loading models from database...");

                var models = await _syncService.LoadCachedModelsAsync();
                var grouped = GroupModelsIntoTiles(models);
                return (models, grouped);
            });

            // Back on UI thread after await — update observable collections
            AllTiles.Clear();
            foreach (var tile in tiles)
            {
                tile.Deleted += OnTileDeleted;
                AllTiles.Add(tile);
            }
            TotalModelCount = AllTiles.Count;
            RebuildAvailableBaseModels();
            ApplyFilters();
            SyncStatus = $"Loaded {allModels.Count} models ({AllTiles.Count} tiles)";

            // Phase 3: Verify existing files in background (low priority)
            _ = VerifyFilesInBackgroundAsync();
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
    }

    /// <summary>
    /// Discover new files and add them to the database.
    /// </summary>
    private async Task DiscoverNewFilesAsync()
    {
        if (_syncService is null)
        {
            return;
        }

        try
        {
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

            var newModels = await _syncService.DiscoverNewFilesAsync(progress);

            Dispatcher.UIThread.Post(() =>
            {
                SyncStatus = $"Discovered {newModels.Count} new files";
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                SyncStatus = $"Discovery error: {ex.Message}";
            });
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
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var syncService = scope.ServiceProvider.GetRequiredService<IModelSyncService>();

            var progress = new Progress<SyncProgress>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (p.Phase == "Verification complete")
                    {
                        SyncStatus = null; // Clear status when done
                    }
                });
            });

            await syncService.VerifyAndSyncFilesAsync(progress);
        }
        catch
        {
            // Silently fail - this is background work
        }
    }

    /// <summary>
    /// Groups models so that different local files from the same LoRA appear as a single
    /// tile with multiple version buttons.
    /// <para>
    /// Grouping strategy (in priority order):
    /// <list type="number">
    ///   <item>By <c>CivitaiModelPageId</c> when set (preferred, future-proof).</item>
    ///   <item>By <c>Model.Name</c> as fallback (covers existing data where page ID is null).</item>
    /// </list>
    /// </para>
    /// Within each group, re-discovery duplicates (same filename) are collapsed — only the
    /// model with the richest metadata is kept per unique filename.
    /// </summary>
    private static List<ModelTileViewModel> GroupModelsIntoTiles(IReadOnlyList<Model> allModels)
    {
        var tiles = new List<ModelTileViewModel>();

        // Phase 1: Group by CivitaiModelPageId (preferred key)
        var byPageId = allModels
            .Where(m => m.CivitaiModelPageId is not null)
            .GroupBy(m => m.CivitaiModelPageId!.Value);

        var consumed = new HashSet<int>(); // track model Ids already placed in a tile

        foreach (var group in byPageId)
        {
            var deduped = DeduplicateModels(group.ToList());
            foreach (var m in deduped)
                consumed.Add(m.Id);

            tiles.Add(deduped.Count == 1
                ? ModelTileViewModel.FromModel(deduped[0])
                : ModelTileViewModel.FromModelGroup(deduped));
        }

        // Phase 2: Group remaining models by Name (case-insensitive)
        var remaining = allModels
            .Where(m => !consumed.Contains(m.Id))
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var group in remaining)
        {
            var deduped = DeduplicateModels(group.ToList());
            foreach (var m in deduped)
                consumed.Add(m.Id);

            tiles.Add(deduped.Count == 1
                ? ModelTileViewModel.FromModel(deduped[0])
                : ModelTileViewModel.FromModelGroup(deduped));
        }

        return tiles;
    }

    /// <summary>
    /// Collapses re-discovery duplicates within a group. Models whose primary file has the
    /// same filename are considered duplicates — only the model with the richest metadata is
    /// kept per unique filename.
    /// </summary>
    private static List<Model> DeduplicateModels(List<Model> models)
    {
        if (models.Count <= 1)
            return models;

        // Key: primary filename (lowered). Value: best model for that file.
        var byFile = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in models)
        {
            var fileName = model.Versions
                .SelectMany(v => v.Files)
                .Where(f => f.IsPrimary)
                .Select(f => f.FileName)
                .FirstOrDefault()
                ?? model.Versions
                    .SelectMany(v => v.Files)
                    .Select(f => f.FileName)
                    .FirstOrDefault()
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                // No file info — keep it as-is (unique key)
                byFile[model.Id.ToString()] = model;
                continue;
            }

            if (byFile.TryGetValue(fileName, out var existing))
            {
                // Keep the one with richer data
                if (IsBetterModel(model, existing))
                    byFile[fileName] = model;
            }
            else
            {
                byFile[fileName] = model;
            }
        }

        return byFile.Values.ToList();
    }

    /// <summary>
    /// Returns true if <paramref name="candidate"/> has richer metadata than <paramref name="current"/>.
    /// </summary>
    private static bool IsBetterModel(Model candidate, Model current)
    {
        // Prefer the one with CivitaiId
        if (candidate.CivitaiId.HasValue && !current.CivitaiId.HasValue) return true;
        if (!candidate.CivitaiId.HasValue && current.CivitaiId.HasValue) return false;

        // Prefer more images
        var candidateImages = candidate.Versions.Sum(v => v.Images.Count);
        var currentImages = current.Versions.Sum(v => v.Images.Count);
        if (candidateImages != currentImages) return candidateImages > currentImages;

        // Prefer the one that was synced
        if (candidate.LastSyncedAt.HasValue && !current.LastSyncedAt.HasValue) return true;

        return false;
    }

    /// <summary>
    /// Download missing metadata from Civitai for models that were discovered locally.
    /// Uses full-file SHA256 hash to find the matching Civitai model version.
    /// Automatically re-fetches missing image data and downloads thumbnails afterward.
    /// </summary>
    [RelayCommand]
    private async Task DownloadMissingMetadataAsync()
    {
        if (_civitaiClient is null || _settingsService is null)
        {
            SyncStatus = "Civitai client not available.";
            return;
        }

        // Get API key — authenticated requests get higher rate limits on Civitai
        var settings = await _settingsService.GetSettingsAsync();
        var apiKey = _secureStorage?.Decrypt(settings.EncryptedCivitaiApiKey);

        _logger?.Info(LogCategory.Network, "CivitaiSync",
            $"Starting metadata sync (API key: {(string.IsNullOrEmpty(apiKey) ? "NOT SET" : "configured")})");

        await RunBusyAsync(async () =>
        {
            var statusParts = new List<string>();

            // ── Phase 1: Sync metadata for models that have never been synced ──
            await SyncMetadataPhaseAsync(apiKey, statusParts);

            // ── Phase 2: Re-fetch images for synced models that have no preview images ──
            await RefetchMissingImagesPhaseAsync(apiKey, statusParts);

            // ── Phase 3: Download thumbnails for tiles still showing "No Preview" ──
            // Brief pause to let fire-and-forget downloads from Phase 2 tile refresh settle
            await Task.Delay(2000);
            await DownloadMissingThumbnailsPhaseAsync(statusParts);

            // Final status
            if (statusParts.Count == 0)
                statusParts.Add("Everything up to date");

            var statusText = string.Join(" · ", statusParts);
            _logger?.Info(LogCategory.Network, "CivitaiSync", statusText);
            Dispatcher.UIThread.Post(() => SyncStatus = statusText);

        }, "Syncing with Civitai...");
    }

    /// <summary>
    /// Phase 1: Sync metadata for models that have never been synced.
    /// </summary>
    private async Task SyncMetadataPhaseAsync(string? apiKey, List<string> statusParts)
    {
        var tilesNeedingMetadata = AllTiles
            .Where(t => t.ModelEntity is { CivitaiId: null, LastSyncedAt: null })
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
        var skipped = 0;
        var errors = 0;

        for (var i = 0; i < tilesNeedingMetadata.Count; i++)
        {
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
            await Task.Delay(1500);
        }

        var parts = new List<string>();
        if (updated > 0) parts.Add($"{updated} updated");
        if (notFound > 0) parts.Add($"{notFound} not on Civitai");
        if (errors > 0) parts.Add($"{errors} errors");
        if (skipped > 0) parts.Add($"{skipped} skipped");
        statusParts.Add($"Metadata: {string.Join(", ", parts)}");
    }

    /// <summary>
    /// Phase 2: Re-fetch image data from Civitai for models that were synced but have no preview images.
    /// This happens when the initial sync didn't receive image data (empty images array from the API).
    /// </summary>
    private async Task RefetchMissingImagesPhaseAsync(string? apiKey, List<string> statusParts)
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

            await Task.Delay(1500);
        }

        if (fetched > 0 || fetchErrors > 0)
        {
            var part = $"Images re-fetched: {fetched}";
            if (fetchErrors > 0) part += $", {fetchErrors} errors";
            statusParts.Add(part);
        }
    }

    /// <summary>
    /// Phase 3: Download thumbnails for tiles still showing "No Preview" that have image URLs.
    /// </summary>
    private async Task DownloadMissingThumbnailsPhaseAsync(List<string> statusParts)
    {
        var tilesNeedingThumbs = AllTiles
            .Where(t => t.IsThumbnailMissing)
            .ToList();

        if (tilesNeedingThumbs.Count == 0)
        {
            _logger?.Debug(LogCategory.General, "CivitaiSync", "No tiles with downloadable but missing thumbnails — skipping Phase 3");
            return;
        }

        _logger?.Info(LogCategory.General, "CivitaiSync",
            $"Phase 3: {tilesNeedingThumbs.Count} tiles need thumbnail download");

        var thumbSuccess = 0;
        var thumbFailed = 0;

        for (var i = 0; i < tilesNeedingThumbs.Count; i++)
        {
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

            await Task.Delay(500);
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
            var allModels = await unitOfWork.Models.GetAllWithIncludesAsync();

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

        // Re-attach by loading from the scoped context
        var dbModels = await unitOfWork.Models.GetAllWithIncludesAsync();
        var dbModel = dbModels.FirstOrDefault(m => m.Id == model.Id);
        if (dbModel is null) return;

        // Update model-level fields from Civitai
        if (civitaiModel is not null)
        {
            // Always set the grouping key — not unique, safe for all models sharing the same Civitai page
            dbModel.CivitaiModelPageId = civitaiModel.Id;

            // Only assign CivitaiId if no other model already owns it (prevents UNIQUE constraint violation)
            var civitaiIdOwner = dbModels.FirstOrDefault(m => m.CivitaiId == civitaiModel.Id);
            if (civitaiIdOwner is null || civitaiIdOwner.Id == dbModel.Id)
            {
                dbModel.CivitaiId = civitaiModel.Id;
            }
            else
            {
                _logger?.Warn(LogCategory.Network, "CivitaiSync",
                    $"Skipping CivitaiId {civitaiModel.Id} for model '{dbModel.Name}' (Id={dbModel.Id}): " +
                    $"already assigned to model '{civitaiIdOwner.Name}' (Id={civitaiIdOwner.Id})");
            }

            dbModel.Name = civitaiModel.Name;
            dbModel.Description = civitaiModel.Description;
            dbModel.IsNsfw = civitaiModel.Nsfw;
            dbModel.IsPoi = civitaiModel.Poi;
            dbModel.Source = DataSource.CivitaiApi;
            dbModel.LastSyncedAt = DateTimeOffset.UtcNow;
            dbModel.AllowNoCredit = civitaiModel.AllowNoCredit;
            dbModel.AllowDerivatives = civitaiModel.AllowDerivatives;
            dbModel.AllowDifferentLicense = civitaiModel.AllowDifferentLicense;

            // Update creator
            if (civitaiModel.Creator is not null && dbModel.Creator is not null)
            {
                dbModel.Creator.Username = civitaiModel.Creator.Username;
            }
        }

        // Update version-level fields for the matched version
        var dbVersion = dbModel.Versions.FirstOrDefault(v =>
            v.Files.Any(f => f.Id == tile.SelectedVersion?.PrimaryFile?.Id));

        if (dbVersion is not null)
        {
            // Only assign CivitaiId if no other version already owns it (prevents UNIQUE constraint violation)
            var versionCivitaiIdOwner = dbModels
                .SelectMany(m => m.Versions)
                .FirstOrDefault(v => v.CivitaiId == bestCivitaiVersion.Id);
            if (versionCivitaiIdOwner is null || versionCivitaiIdOwner.Id == dbVersion.Id)
            {
                dbVersion.CivitaiId = bestCivitaiVersion.Id;
            }
            else
            {
                _logger?.Warn(LogCategory.Network, "CivitaiSync",
                    $"Skipping CivitaiId {bestCivitaiVersion.Id} for version '{dbVersion.Name}' (Id={dbVersion.Id}): " +
                    $"already assigned to version '{versionCivitaiIdOwner.Name}' (Id={versionCivitaiIdOwner.Id})");
            }

            dbVersion.Name = bestCivitaiVersion.Name;
            dbVersion.Description = bestCivitaiVersion.Description;
            dbVersion.BaseModelRaw = bestCivitaiVersion.BaseModel;
            dbVersion.DownloadUrl = bestCivitaiVersion.DownloadUrl;
            dbVersion.DownloadCount = bestCivitaiVersion.Stats?.DownloadCount ?? 0;
            dbVersion.PublishedAt = bestCivitaiVersion.PublishedAt;
            dbVersion.EarlyAccessDays = bestCivitaiVersion.EarlyAccessTimeFrame;

            // Update trigger words
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
                    Width = civImage.Width,
                    Height = civImage.Height,
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

        await unitOfWork.SaveChangesAsync();

        // Reload model from DB after save to get generated IDs on new images
        var refreshedModels = await unitOfWork.Models.GetAllWithIncludesAsync();
        var refreshedModel = refreshedModels.FirstOrDefault(m => m.Id == model.Id);

        // Refresh tile on UI thread with updated data
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            tile.ModelEntity = refreshedModel ?? dbModel;
        });
    }

    /// <summary>
    /// Scan for duplicate files.
    /// </summary>
    [RelayCommand]
    private async Task ScanDuplicatesAsync()
    {
        await RunBusyAsync(async () =>
        {
            // TODO: Implement duplicate scanning
            await Task.Delay(1000); // Simulate work
        }, "Scanning for duplicates...");
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
            FilteredTiles.Remove(tile);
            TotalModelCount = AllTiles.Count;
            FilteredModelCount = FilteredTiles.Count;
            RebuildAvailableBaseModels();
        });
    }

    /// <summary>
    /// Rebuilds <see cref="AvailableBaseModels"/> from the distinct <c>BaseModelRaw</c>
    /// values across all tile versions. Preserves existing selections where the value still exists.
    /// </summary>
    private void RebuildAvailableBaseModels()
    {
        // Collect distinct BaseModelRaw values from all versions across all tiles
        var distinctBaseModels = AllTiles
            .SelectMany(t => t.Versions)
            .Select(v => v.BaseModelRaw)
            .Where(raw => !string.IsNullOrWhiteSpace(raw))
            .Select(raw => raw!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(raw => raw, StringComparer.OrdinalIgnoreCase)
            .ToList();

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

        foreach (var raw in distinctBaseModels)
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
    /// Called when any base model filter item's selection changes.
    /// </summary>
    private void OnBaseModelFilterChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsBaseModelFilterActive));
        OnPropertyChanged(nameof(ActiveBaseModelFilterCount));
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        FilteredTiles.Clear();

        var query = AllTiles.AsEnumerable();

        // Filter by search text
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            query = query.Where(t =>
                t.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                t.FileName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                t.CreatorName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        // Filter by NSFW
        if (!ShowNsfw)
        {
            query = query.Where(t => !t.IsNsfw);
        }

        // Filter by selected base models (multi-select, OR logic)
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

        foreach (var tile in query)
        {
            FilteredTiles.Add(tile);
        }

        FilteredModelCount = FilteredTiles.Count;
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
    {
        return baseModelRaw switch
        {
            "SD 1.5" => BaseModelType.SD15,
            "SDXL 1.0" => BaseModelType.SDXL10,
            "Pony" => BaseModelType.Pony,
            "Illustrious" => BaseModelType.Illustrious,
            "Flux.1 D" => BaseModelType.Flux1D,
            _ => BaseModelType.Other
        };
    }

    #endregion
}
