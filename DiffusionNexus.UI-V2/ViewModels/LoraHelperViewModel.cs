using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the LoRA Helper view displaying model tiles.
/// </summary>
public partial class LoraHelperViewModel : BusyViewModelBase
{
    private readonly IAppSettingsService? _settingsService;
    private readonly IModelSyncService? _syncService;

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
    public LoraHelperViewModel()
    {
        _settingsService = null;
        _syncService = null;
        // Load demo data for design-time preview
        LoadDemoData();
    }

    /// <summary>
    /// Runtime constructor with DI.
    /// </summary>
    public LoraHelperViewModel(IAppSettingsService settingsService, IModelSyncService syncService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
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
    /// Download missing metadata for all models.
    /// </summary>
    [RelayCommand]
    private async Task DownloadMissingMetadataAsync()
    {
        await RunBusyAsync(async () =>
        {
            // TODO: Implement metadata download using Civitai API
            await Task.Delay(1000); // Simulate work
        }, "Downloading metadata...");
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
