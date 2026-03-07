using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
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
        ISecureStorage? secureStorage = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _civitaiClient = civitaiClient;
        _secureStorage = secureStorage;
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
            SyncStatus = "Starting refresh...";

            // Phase 1: Discover new files first (so they get saved to DB)
            await DiscoverNewFilesAsync();

            // Phase 2: Load all from database (includes newly discovered)
            await RunBusyAsync(async () =>
            {
                SyncStatus = "Loading models from database...";
                var allModels = await _syncService.LoadCachedModelsAsync();

                // Update UI on dispatcher thread
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    AllTiles.Clear();
                    foreach (var model in allModels)
                    {
                        AllTiles.Add(ModelTileViewModel.FromModel(model));
                    }
                    TotalModelCount = AllTiles.Count;
                    ApplyFilters();
                });

                SyncStatus = $"Loaded {allModels.Count} models";

            }, "Loading models...");

            // Phase 3: Verify existing files in background (low priority)
            _ = VerifyFilesInBackgroundAsync();
        }
        catch (Exception ex)
        {
            SyncStatus = $"Error: {ex.Message}";
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
    /// </summary>
    private async Task VerifyFilesInBackgroundAsync()
    {
        if (_syncService is null)
        {
            return;
        }

        try
        {
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

            await _syncService.VerifyAndSyncFilesAsync(progress);
        }
        catch
        {
            // Silently fail - this is background work
        }
    }

    /// <summary>
    /// Download missing metadata from Civitai for models that were discovered locally.
    /// Uses file hash to find the matching Civitai model version.
    /// </summary>
    [RelayCommand]
    private async Task DownloadMissingMetadataAsync()
    {
        if (_civitaiClient is null || _settingsService is null)
        {
            SyncStatus = "Civitai client not available.";
            return;
        }

        // Get API key for authenticated requests
        var settings = await _settingsService.GetSettingsAsync();
        var apiKey = _secureStorage?.Decrypt(settings.EncryptedCivitaiApiKey);

        await RunBusyAsync(async () =>
        {
            // Find tiles that are missing Civitai metadata (no CivitaiId on model)
            var tilesNeedingMetadata = AllTiles
                .Where(t => t.ModelEntity?.CivitaiId is null)
                .ToList();

            if (tilesNeedingMetadata.Count == 0)
            {
                Dispatcher.UIThread.Post(() => SyncStatus = "All models already have metadata.");
                return;
            }

            var updated = 0;
            var failed = 0;

            for (var i = 0; i < tilesNeedingMetadata.Count; i++)
            {
                var tile = tilesNeedingMetadata[i];
                var file = tile.SelectedVersion?.PrimaryFile;
                if (file is null) continue;

                Dispatcher.UIThread.Post(() =>
                    SyncStatus = $"Fetching metadata {i + 1}/{tilesNeedingMetadata.Count}: {tile.DisplayName}");

                try
                {
                    // Try hash lookup first (most reliable)
                    var hash = file.HashSHA256;
                    if (string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(file.LocalPath))
                    {
                        // Compute hash if missing
                        hash = await _syncService!.ComputeFileHashAsync(file.LocalPath);
                        file.HashSHA256 = hash;
                    }

                    if (string.IsNullOrEmpty(hash)) { failed++; continue; }

                    var civitaiVersion = await _civitaiClient.GetModelVersionByHashAsync(hash, apiKey);
                    if (civitaiVersion is null) { failed++; continue; }

                    // Update model entity with Civitai data using a fresh scope
                    await UpdateModelFromCivitaiAsync(tile, civitaiVersion, apiKey);
                    updated++;
                }
                catch (HttpRequestException)
                {
                    failed++;
                    // Rate limit or network error - wait and continue
                    await Task.Delay(2000);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Failed to fetch metadata for {ModelName}", tile.DisplayName);
                    failed++;
                }

                // Small delay to avoid rate limiting
                await Task.Delay(500);
            }

            Dispatcher.UIThread.Post(() =>
                SyncStatus = $"Metadata sync complete: {updated} updated, {failed} failed.");

        }, "Downloading metadata from Civitai...");
    }

    /// <summary>
    /// Updates a model entity from Civitai API response data.
    /// </summary>
    private async Task UpdateModelFromCivitaiAsync(
        ModelTileViewModel tile,
        CivitaiModelVersion civitaiVersion,
        string? apiKey)
    {
        // Fetch the full model to get all versions
        CivitaiModel? civitaiModel = null;
        if (civitaiVersion.ModelId > 0)
        {
            civitaiModel = await _civitaiClient!.GetModelAsync(civitaiVersion.ModelId, apiKey);
        }

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
            dbModel.CivitaiId = civitaiModel.Id;
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
            dbVersion.CivitaiId = civitaiVersion.Id;
            dbVersion.Name = civitaiVersion.Name;
            dbVersion.Description = civitaiVersion.Description;
            dbVersion.BaseModelRaw = civitaiVersion.BaseModel;
            dbVersion.DownloadUrl = civitaiVersion.DownloadUrl;
            dbVersion.DownloadCount = civitaiVersion.Stats?.DownloadCount ?? 0;
            dbVersion.PublishedAt = civitaiVersion.PublishedAt;
            dbVersion.EarlyAccessDays = civitaiVersion.EarlyAccessTimeFrame;

            // Update trigger words
            dbVersion.TriggerWords.Clear();
            var order = 0;
            foreach (var word in civitaiVersion.TrainedWords)
            {
                dbVersion.TriggerWords.Add(new TriggerWord
                {
                    Word = word,
                    Order = order++
                });
            }

            // Update images (add missing ones)
            var existingImageIds = dbVersion.Images.Select(i => i.CivitaiId).ToHashSet();
            var sortOrder = dbVersion.Images.Count;
            foreach (var civImage in civitaiVersion.Images)
            {
                if (civImage.Id.HasValue && existingImageIds.Contains(civImage.Id.Value))
                    continue;

                dbVersion.Images.Add(new ModelImage
                {
                    CivitaiId = civImage.Id,
                    Url = civImage.Url,
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
            var civFile = civitaiVersion.Files.FirstOrDefault(f => f.Primary == true);
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

        // Refresh tile on UI thread with updated data
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            tile.ModelEntity = dbModel;
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
    /// Reset all filters.
    /// </summary>
    [RelayCommand]
    private void ResetFilters()
    {
        SearchText = null;
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

        foreach (var tile in query)
        {
            FilteredTiles.Add(tile);
        }

        FilteredModelCount = FilteredTiles.Count;
    }

    private void LoadDemoData()
    {
        AllTiles.Clear();

        // Create demo models with various base models
        var demoModels = new[]
        {
            CreateDemoModel("Anime Character LoRA", "AIArtist", "Pony", 25000),
            CreateDemoModel("Realistic Portrait", "PhotoMaster", "SDXL 1.0", 45000),
            CreateDemoModel("Fantasy Style", "DreamWeaver", "SD 1.5", "SDXL 1.0", 12000),
            CreateDemoModel("Cyberpunk Aesthetic", "NeonCreator", "Illustrious", 8500),
            CreateDemoModel("Vintage Film Look", "RetroVision", "SD 1.5", 3200),
            CreateDemoModel("Anime Eyes Detail", "MangaKing", "Pony", "Illustrious", 67000),
            CreateDemoModel("Landscape Enhancer", "NatureAI", "SDXL 1.0", 15000),
            CreateDemoModel("Comic Book Style", "ComicFan", "SD 1.5", 9800),
            CreateDemoModel("Oil Painting Effect", "ClassicArt", "SDXL 1.0", "SD 1.5", 21000),
            CreateDemoModel("Sci-Fi Concepts", "FutureTech", "Flux.1 D", 4500),
            CreateDemoModel("Video Enhancer", "VideoMaster", "Wan Video 14B t2v", 2100),
            CreateDemoModel("Turbo Generator", "SpeedyAI", "Z-Image-Turbo", 11000),
        };

        foreach (var model in demoModels)
        {
            AllTiles.Add(ModelTileViewModel.FromModel(model));
        }

        TotalModelCount = AllTiles.Count;
        ApplyFilters();
    }

    private static Model CreateDemoModel(string name, string creator, string baseModel, int downloads)
    {
        return CreateDemoModel(name, creator, new[] { baseModel }, downloads);
    }

    private static Model CreateDemoModel(string name, string creator, string baseModel1, string baseModel2, int downloads)
    {
        return CreateDemoModel(name, creator, new[] { baseModel1, baseModel2 }, downloads);
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
