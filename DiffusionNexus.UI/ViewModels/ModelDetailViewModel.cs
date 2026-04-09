using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
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
using DiffusionNexus.UI.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the model detail panel shown when a tile is clicked.
/// Fetches all versions from the Civitai API and shows which are downloaded (blue) vs available (yellow).
/// </summary>
public partial class ModelDetailViewModel : ViewModelBase
{
    private readonly ICivitaiClient? _civitaiClient;
    private readonly IAppSettingsService? _settingsService;
    private readonly ISecureStorage? _secureStorage;
    private readonly IUnifiedLogger? _logger;

    /// <summary>
    /// Cached Civitai model data from the initial API fetch.
    /// Reused after download to rebuild version tabs without an extra API call.
    /// </summary>
    private CivitaiModel? _cachedCivitaiModel;

    /// <summary>
    /// Cancels any in-flight Civitai thumbnail download when the selected version tab changes.
    /// </summary>
    private CancellationTokenSource? _detailThumbnailCts;

    #region Observable Properties

    /// <summary>
    /// The source tile that opened this detail view.
    /// </summary>
    [ObservableProperty]
    private ModelTileViewModel? _sourceTile;

    /// <summary>
    /// Model name.
    /// </summary>
    [ObservableProperty]
    private string _modelName = string.Empty;

    /// <summary>
    /// The Civitai model ID.
    /// </summary>
    [ObservableProperty]
    private string _modelIdDisplay = string.Empty;

    /// <summary>
    /// Base model of the currently selected version.
    /// </summary>
    [ObservableProperty]
    private string _baseModelDisplay = string.Empty;

    /// <summary>
    /// Model type display (e.g., "LORA").
    /// </summary>
    [ObservableProperty]
    private string _modelTypeDisplay = string.Empty;

    /// <summary>
    /// Creator name.
    /// </summary>
    [ObservableProperty]
    private string _creatorDisplay = string.Empty;

    /// <summary>
    /// The description converted to readable plain text.
    /// </summary>
    [ObservableProperty]
    private string _descriptionText = string.Empty;

    /// <summary>
    /// Trigger words for the currently selected version.
    /// </summary>
    [ObservableProperty]
    private string _triggerWordsDisplay = string.Empty;

    /// <summary>
    /// Whether trigger words are available.
    /// </summary>
    [ObservableProperty]
    private bool _hasTriggerWords;

    /// <summary>
    /// Tags for the model.
    /// </summary>
    [ObservableProperty]
    private string _tagsDisplay = string.Empty;

    /// <summary>
    /// Whether tags are available.
    /// </summary>
    [ObservableProperty]
    private bool _hasTags;

    /// <summary>
    /// The inferred category (e.g., Character, Style, Concept) derived from the model's tags.
    /// </summary>
    [ObservableProperty]
    private string _categoryDisplay = string.Empty;

    /// <summary>
    /// Whether a category could be inferred from the model's tags.
    /// </summary>
    [ObservableProperty]
    private bool _hasCategory;

    /// <summary>
    /// The currently selected version tab.
    /// </summary>
    [ObservableProperty]
    private CivitaiVersionTabItem? _selectedVersionTab;

    /// <summary>
    /// The thumbnail image.
    /// </summary>
    [ObservableProperty]
    private Bitmap? _thumbnailImage;

    /// <summary>
    /// Whether data is loading.
    /// </summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Status/error message.
    /// </summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// File name display for the selected version.
    /// </summary>
    [ObservableProperty]
    private string _fileNameDisplay = string.Empty;

    /// <summary>
    /// Version ID display for the selected version.
    /// </summary>
    [ObservableProperty]
    private string _versionIdDisplay = string.Empty;

    #endregion

    #region Collections

    /// <summary>
    /// All version tabs (blue = downloaded, yellow = not downloaded).
    /// </summary>
    public ObservableCollection<CivitaiVersionTabItem> VersionTabs { get; } = [];

    /// <summary>
    /// Tags as individual items for display in a wrap panel.
    /// </summary>
    public ObservableCollection<string> TagItems { get; } = [];

    #endregion

    #region Constructors

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public ModelDetailViewModel()
    {
        ModelName = "Semi-Fortnite 3D Style - Flux Kontext";
        ModelIdDisplay = "1843355";
        VersionIdDisplay = "2086052";
        BaseModelDisplay = "Flux.1 Kontext";
        ModelTypeDisplay = "LORA";
        FileNameDisplay = "40fy_v1.safetensors";
        CreatorDisplay = "ExampleCreator";
        DescriptionText = "Transform persons into a vibrant semi-transparent 3D style with this LoRA for Flux Kontext!";
        TriggerWordsDisplay = "40fy, 3d style, fortnite";
        HasTriggerWords = true;
        TagsDisplay = "3d, fortnite, style, character";
        HasTags = true;
        CategoryDisplay = "Style";
        HasCategory = true;
    }

    /// <summary>
    /// Runtime constructor with DI.
    /// </summary>
    public ModelDetailViewModel(
        ICivitaiClient? civitaiClient,
        IAppSettingsService? settingsService,
        ISecureStorage? secureStorage,
        IUnifiedLogger? logger)
    {
        _civitaiClient = civitaiClient;
        _settingsService = settingsService;
        _secureStorage = secureStorage;
        _logger = logger;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Loads detail data for the given tile. Fetches all versions from Civitai API.
    /// </summary>
    public async Task LoadAsync(ModelTileViewModel tile)
    {
        _cachedCivitaiModel = null;
        SourceTile = tile;

        // Populate from local data immediately
        ModelName = tile.DisplayName;
        ModelTypeDisplay = tile.ModelTypeDisplay;
        CreatorDisplay = tile.CreatorName;
        ThumbnailImage = tile.ThumbnailImage;

        PopulateFromLocalVersion(tile);

        // Try to fetch from Civitai API for the full version list
        await FetchCivitaiDataAsync(tile);
    }

    #endregion

    #region Commands

    /// <summary>
    /// Closes the detail panel.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Opens the model page on Civitai in the default browser.
    /// </summary>
    [RelayCommand]
    private void OpenOnCivitai()
    {
        SourceTile?.OpenOnCivitaiCommand.Execute(null);
    }

    /// <summary>
    /// Copies trigger words to clipboard.
    /// </summary>
    [RelayCommand]
    private async Task CopyTriggerWordsAsync()
    {
        if (string.IsNullOrWhiteSpace(TriggerWordsDisplay)) return;
        await CopyToClipboardAsync(TriggerWordsDisplay);
    }

    /// <summary>
    /// Downloads the currently selected version if it's not locally available.
    /// Shows a dialog for destination selection, then streams the download with progress tracking.
    /// </summary>
    [RelayCommand]
    private async Task DownloadSelectedVersionAsync()
    {
        var tab = SelectedVersionTab;
        if (tab is null || tab.IsDownloaded) return;

        // Ensure a Civitai API token is configured before downloading.
        // If missing, show the token dialog so the user can paste one.
        if (!await EnsureCivitaiTokenAsync())
            return;

        // Resolve download URL
        var primaryFile = tab.CivitaiVersion.Files.FirstOrDefault(f => f.Primary == true)
                          ?? tab.CivitaiVersion.Files.FirstOrDefault();
        var downloadUrl = primaryFile?.DownloadUrl ?? tab.DownloadUrl;
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            _logger?.Warn(LogCategory.Download, "LoraDownload",
                $"No download URL available for '{ModelName}' version '{tab.Label}'");
            return;
        }

        // Get source folders for the dialog
        IReadOnlyList<string> sourceFolders = [];
        if (_settingsService is not null)
        {
            sourceFolders = await _settingsService.GetEnabledLoraSourcesAsync();
        }

        // Show download destination dialog
        var dialogService = App.Services?.GetService<IDialogService>();
        if (dialogService is null) return;

        var result = await dialogService.ShowDownloadLoraVersionDialogAsync(
            ModelName, tab.CivitaiVersion, sourceFolders, CategoryDisplay);

        if (!result.Confirmed || string.IsNullOrWhiteSpace(result.TargetFolder))
            return;

        // Start the download in the background with progress tracking
        var fileName = primaryFile?.Name ?? $"{ModelName}_{tab.Label}.safetensors";
        var targetPath = Path.Combine(result.TargetFolder, fileName);

        // Mark as downloading
        tab.IsDownloading = true;

        _ = Task.Run(() => DownloadFileAsync(downloadUrl, targetPath, tab));
    }

    /// <summary>
    /// Streams the file download with progress tracking via the unified logger.
    /// </summary>
    // TODO: Linux Implementation for download task
    private async Task DownloadFileAsync(string downloadUrl, string targetPath, CivitaiVersionTabItem tab)
    {
        var taskTracker = App.Services?.GetService<ITaskTracker>();
        var activityLog = App.Services?.GetService<IActivityLogService>();
        var taskName = $"Downloading {Path.GetFileName(targetPath)}";
        using var taskHandle = taskTracker?.BeginTask(taskName, LogCategory.Download);

        activityLog?.StartDownloadProgress(taskName);

        try
        {
            taskHandle?.ReportIndeterminate("Connecting...");

            // Ensure target directory exists
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var httpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true
            });
            httpClient.Timeout = TimeSpan.FromHours(2);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DiffusionNexus/1.0");

            // Try without auth first (works for all public models).
            // If Civitai returns 401/403 (early access), retry with the API key as a query param.
            var ct = taskHandle?.CancellationToken ?? CancellationToken.None;
            var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                response.Dispose();
                var apiKey = await GetApiKeyAsync();
                if (!string.IsNullOrEmpty(apiKey))
                {
                    taskHandle?.ReportIndeterminate("Retrying with API key...");
                    var separator = downloadUrl.Contains('?') ? "&" : "?";
                    var authedUrl = $"{downloadUrl}{separator}token={apiKey}";
                    response = await httpClient.GetAsync(authedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                }
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;
                long totalRead = 0;
                var tempPath = targetPath + ".tmp";

                // Use explicit using blocks so streams close before File.Move
                await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    int bytesRead;
                    var lastProgressReport = Environment.TickCount64;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        totalRead += bytesRead;

                        // Throttle progress updates to ~4/sec so the UI thread
                        // is not starved when the unified log is open.
                        var now = Environment.TickCount64;
                        if (now - lastProgressReport < 250) continue;
                        lastProgressReport = now;

                        if (totalBytes.HasValue && totalBytes.Value > 0)
                        {
                            var progress = (double)totalRead / totalBytes.Value;
                            var mbDownloaded = totalRead / (1024.0 * 1024.0);
                            var mbTotal = totalBytes.Value / (1024.0 * 1024.0);
                            taskHandle?.ReportProgress(progress, $"{mbDownloaded:F1} / {mbTotal:F1} MB");
                            activityLog?.ReportDownloadProgress((int)(progress * 100),
                                $"{mbDownloaded:F1} / {mbTotal:F1} MB");
                        }
                        else
                        {
                            var mbDownloaded = totalRead / (1024.0 * 1024.0);
                            taskHandle?.ReportIndeterminate($"{mbDownloaded:F1} MB downloaded");
                        }
                    }
                }

                // Rename temp to final
                if (File.Exists(targetPath))
                    File.Delete(targetPath);
                File.Move(tempPath, targetPath);

                // Build a human-readable size summary for completion messages
                var finalMb = totalRead / (1024.0 * 1024.0);
                var sizeText = totalBytes.HasValue && totalBytes.Value > 0
                    ? $"{finalMb:F1} / {totalBytes.Value / (1024.0 * 1024.0):F1} MB"
                    : $"{finalMb:F1} MB";

                // Persist Model/ModelVersion/ModelFile to Diffusion_Nexus-core.db
                // with full Civitai metadata — no legacy .civitai.info sidecar files.
                await PersistDownloadedModelAsync(targetPath, tab.CivitaiVersion, SourceTile?.ModelEntity?.Id);

                // Refresh the tile and detail panel so the downloaded version shows as blue
                await RefreshAfterDownloadAsync(targetPath);

                taskHandle?.Complete($"{Path.GetFileName(targetPath)} downloaded complete — {sizeText}");
                activityLog?.CompleteDownloadProgress(true,
                    $"{Path.GetFileName(targetPath)} downloaded complete — {sizeText}");
                _logger?.Info(LogCategory.Download, "LoraDownload",
                    $"Downloaded '{Path.GetFileName(targetPath)}' successfully — {sizeText}",
                    $"Path: {targetPath}");
            }
        }
        catch (OperationCanceledException)
        {
            activityLog?.CompleteDownloadProgress(false, $"Download cancelled: {Path.GetFileName(targetPath)}");
            _logger?.Info(LogCategory.Download, "LoraDownload",
                $"Download cancelled: {Path.GetFileName(targetPath)}");
            CleanupTempFile(targetPath);
        }
        catch (Exception ex)
        {
            taskHandle?.Fail(ex, $"Failed to download {Path.GetFileName(targetPath)}");
            activityLog?.CompleteDownloadProgress(false, $"Download failed: {Path.GetFileName(targetPath)}");
            _logger?.Error(LogCategory.Download, "LoraDownload",
                $"Download failed: {ex.Message}", ex);
            CleanupTempFile(targetPath);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => tab.IsDownloading = false);
        }
    }

    /// <summary>
    /// Reloads the model from the database and refreshes the source tile and detail panel
    /// so the newly downloaded version appears as "downloaded" (blue tab).
    /// Uses <see cref="_cachedCivitaiModel"/> to rebuild tabs without an extra API call.
    /// </summary>
    private async Task RefreshAfterDownloadAsync(string downloadedFilePath)
    {
        try
        {
            var sourceTile = SourceTile;
            if (sourceTile is null) return;

            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            // Find the model that owns the file we just downloaded (targeted query, not full DB load)
            var refreshedModel = await unitOfWork.Models.FindByLocalFilePathAsync(downloadedFilePath);

            // Fallback: match by existing tile model ID
            refreshedModel ??= sourceTile.ModelEntity?.Id is > 0
                ? await unitOfWork.Models.GetByIdWithIncludesAsync(sourceTile.ModelEntity.Id)
                : null;

            if (refreshedModel is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    sourceTile.RefreshModelData(refreshedModel);

                    ModelName = refreshedModel.Name;
                    ModelIdDisplay = refreshedModel.CivitaiId?.ToString()
                                     ?? refreshedModel.CivitaiModelPageId?.ToString()
                                     ?? "\u2014";
                    CreatorDisplay = refreshedModel.Creator?.Username ?? "Unknown";

                    if (_cachedCivitaiModel is not null)
                    {
                        BuildCivitaiVersionTabs(_cachedCivitaiModel, sourceTile);
                    }
                    else
                    {
                        BuildLocalVersionTabs(sourceTile);
                    }
                });
            }
            else
            {
                _logger?.Debug(LogCategory.Download, "LoraDownload",
                    "Could not find model in DB after download \u2014 UI not refreshed");
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.Download, "LoraDownload",
                $"Failed to refresh UI after download: {ex.Message}");
        }
        finally
        {
            // Always notify parent to rebuild tiles with proper grouping,
            // even if the tile-level refresh above failed.
            DownloadCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void CleanupTempFile(string targetPath)
    {
        try
        {
            var tempPath = targetPath + ".tmp";
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    /// <summary>
    /// Persists the downloaded model to <c>Diffusion_Nexus-core.db</c> with full Civitai metadata.
    /// <para>
    /// Creates <see cref="Model"/> → <see cref="ModelVersion"/> → <see cref="ModelFile"/> entities,
    /// enriched with trigger words, images, tags, and file hashes from the Civitai version data
    /// we already have in memory (no extra API call needed for the version).
    /// </para>
    /// <para>
    /// Only fetches the full <see cref="CivitaiModel"/> when a modelId is available, to get
    /// model-level fields (description, tags, license) — same call the "Download Metadata"
    /// button uses via <c>UpdateModelFromCivitaiAsync</c> in <c>LoraViewerViewModel</c>.
    /// </para>
    /// Uses a scoped <see cref="IUnitOfWork"/> to avoid DbContext conflicts.
    /// </summary>
    private async Task PersistDownloadedModelAsync(string filePath, CivitaiModelVersion civitaiVersion, int? existingModelId = null)
    {
        try
        {
            var scopeFactory = App.Services?.GetService<IServiceScopeFactory>();
            if (scopeFactory is null)
            {
                _logger?.Warn(LogCategory.Download, "LoraDownload",
                    "Cannot persist to database: IServiceScopeFactory not available");
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            // Skip if file is already tracked (e.g., from a prior DiscoverNewFilesAsync)
            var existingPaths = await unitOfWork.ModelFiles.GetExistingLocalPathsAsync();
            if (existingPaths.Contains(filePath))
            {
                _logger?.Debug(LogCategory.Download, "LoraDownload",
                    $"File already in database: {filePath}");
                return;
            }

            var fileInfo = new FileInfo(filePath);

            // When ModelId is 0 (local-only tab) but we have a version CivitaiId,
            // fetch the version first to discover the parent model ID.
            var effectiveVersion = civitaiVersion;
            if (civitaiVersion.ModelId <= 0 && civitaiVersion.Id > 0 && _civitaiClient is not null)
            {
                var apiKey = await GetApiKeyAsync();
                var fetched = await _civitaiClient.GetModelVersionAsync(civitaiVersion.Id, apiKey);
                if (fetched is not null)
                {
                    effectiveVersion = fetched;
                    _logger?.Debug(LogCategory.Download, "LoraDownload",
                        $"Resolved ModelId={fetched.ModelId} from version {fetched.Id}");
                }
            }

            // Fetch full model for richer data (description, tags, license).
            // This is the same GetModelAsync call that "Download Metadata" uses.
            CivitaiModel? civitaiModel = null;
            if (effectiveVersion.ModelId > 0 && _civitaiClient is not null)
            {
                var apiKey = await GetApiKeyAsync();
                civitaiModel = await _civitaiClient.GetModelAsync(effectiveVersion.ModelId, apiKey);
            }

            // Resolve the Civitai model page ID. civitaiModel.Id is authoritative when
            // available; effectiveVersion.ModelId is a fallback (may be 0 for nested versions).
            var modelPageId = civitaiModel?.Id ?? (effectiveVersion.ModelId > 0 ? effectiveVersion.ModelId : (int?)null);

            // If the full model has a richer version (with images), prefer it
            var bestVersion = civitaiModel?.ModelVersions
                .FirstOrDefault(v => v.Id == effectiveVersion.Id) ?? effectiveVersion;

            // Resolve primary file from best available data
            var civFile = bestVersion.Files.FirstOrDefault(f => f.Primary == true)
                          ?? bestVersion.Files.FirstOrDefault()
                          ?? civitaiVersion.Files.FirstOrDefault(f => f.Primary == true)
                          ?? civitaiVersion.Files.FirstOrDefault();

            // --- Check if a model already exists (grouping) ---
            // Targeted query — avoids loading ALL 11K models just to find one.
            var model = await unitOfWork.Models.FindByModelPageIdOrIdAsync(modelPageId, existingModelId);
            bool isExistingModel = false;

            if (model is not null)
            {
                isExistingModel = true;
                _logger?.Debug(LogCategory.Download, "LoraDownload",
                    $"Adding version to existing model '{model.Name}' (Id={model.Id}, PageId={modelPageId})");

                // Enrich existing model with Civitai metadata it was missing
                if (civitaiModel is not null)
                {
                    model.CivitaiId ??= modelPageId;
                    model.CivitaiModelPageId ??= modelPageId;
                    model.Name = civitaiModel.Name;
                    model.Description ??= civitaiModel.Description;
                    model.IsNsfw = civitaiModel.Nsfw;
                    model.IsPoi = civitaiModel.Poi;
                    model.Source = DataSource.CivitaiApi;
                    model.LastSyncedAt = DateTimeOffset.UtcNow;
                    model.AllowNoCredit = civitaiModel.AllowNoCredit;
                    model.AllowDerivatives = civitaiModel.AllowDerivatives;
                    model.AllowDifferentLicense = civitaiModel.AllowDifferentLicense;

                    // Update or create creator — reuse existing Creator entity by
                    // Username to avoid UNIQUE constraint violations.
                    if (civitaiModel.Creator is not null)
                    {
                        if (model.Creator is not null)
                        {
                            model.Creator.Username = civitaiModel.Creator.Username;
                            model.Creator.AvatarUrl ??= civitaiModel.Creator.Image;
                        }
                        else
                        {
                            var existingCreator = await unitOfWork.Models
                                .FindCreatorByUsernameAsync(civitaiModel.Creator.Username);

                            model.Creator = existingCreator ?? new Creator
                            {
                                Username = civitaiModel.Creator.Username,
                                AvatarUrl = civitaiModel.Creator.Image,
                            };
                        }
                    }
                }
                else if (modelPageId.HasValue)
                {
                    model.CivitaiModelPageId ??= modelPageId;
                }
            }
            else
            {
                model = new Model
                {
                    CivitaiId = modelPageId,
                    CivitaiModelPageId = modelPageId,
                    Name = civitaiModel?.Name ?? civitaiVersion.Model?.Name
                           ?? Path.GetFileNameWithoutExtension(filePath),
                    Description = civitaiModel?.Description,
                    Type = ModelType.LORA,
                    IsNsfw = civitaiModel?.Nsfw ?? civitaiVersion.Model?.Nsfw ?? false,
                    Source = DataSource.CivitaiApi,
                    LastSyncedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                if (civitaiModel is not null)
                {
                    model.AllowNoCredit = civitaiModel.AllowNoCredit;
                    model.AllowDerivatives = civitaiModel.AllowDerivatives;
                    model.AllowDifferentLicense = civitaiModel.AllowDifferentLicense;
                    model.IsPoi = civitaiModel.Poi;

                    if (civitaiModel.Creator is not null)
                    {
                        var existingCreator = await unitOfWork.Models
                            .FindCreatorByUsernameAsync(civitaiModel.Creator.Username);

                        model.Creator = existingCreator ?? new Creator
                        {
                            Username = civitaiModel.Creator.Username,
                            AvatarUrl = civitaiModel.Creator.Image,
                        };
                    }
                }
            }

            // --- Version (same fields as UpdateModelFromCivitaiAsync) ---

            var version = new ModelVersion
            {
                CivitaiId = bestVersion.Id > 0 ? bestVersion.Id : null,
                Name = bestVersion.Name,
                Description = bestVersion.Description,
                BaseModel = ParseBaseModel(bestVersion.BaseModel),
                BaseModelRaw = bestVersion.BaseModel,
                DownloadUrl = bestVersion.DownloadUrl,
                PublishedAt = bestVersion.PublishedAt,
                EarlyAccessDays = bestVersion.EarlyAccessTimeFrame,
                DownloadCount = bestVersion.Stats?.DownloadCount ?? 0,
                CreatedAt = bestVersion.CreatedAt != default
                    ? bestVersion.CreatedAt
                    : DateTimeOffset.UtcNow,
                Model = model
            };

            // Trigger words
            var order = 0;
            foreach (var word in bestVersion.TrainedWords)
            {
                version.TriggerWords.Add(new TriggerWord { Word = word, Order = order++ });
            }

            // Images (same structure as UpdateModelFromCivitaiAsync)
            var sortOrder = 0;
            foreach (var civImage in bestVersion.Images)
            {
                if (string.IsNullOrEmpty(civImage.Url)) continue;
                version.Images.Add(new ModelImage
                {
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

            // --- File (same pattern as ModelFileSyncService.CreateModelFromFile) ---

            var modelFile = new ModelFile
            {
                CivitaiId = civFile?.Id,
                FileName = fileInfo.Name,
                LocalPath = filePath,
                SizeKB = fileInfo.Length / 1024.0,
                FileSizeBytes = fileInfo.Length,
                Format = GetFileFormat(fileInfo.Extension),
                IsPrimary = true,
                IsLocalFileValid = true,
                LocalFileVerifiedAt = DateTimeOffset.UtcNow,
                DownloadUrl = civFile?.DownloadUrl,
                HashSHA256 = civFile?.Hashes?.SHA256,
                HashAutoV2 = civFile?.Hashes?.AutoV2,
                HashCRC32 = civFile?.Hashes?.CRC32,
                HashBLAKE3 = civFile?.Hashes?.BLAKE3,
                ModelVersion = version
            };

            version.Files.Add(modelFile);

            // Guard: don't add a duplicate version if one with the same CivitaiId
            // already exists on this model (e.g., re-download of an already-tracked version).
            var duplicateVersion = version.CivitaiId.HasValue
                ? model.Versions.FirstOrDefault(v => v.CivitaiId == version.CivitaiId)
                : null;

            if (duplicateVersion is not null)
            {
                _logger?.Debug(LogCategory.Download, "LoraDownload",
                    $"Version CivitaiId={version.CivitaiId} already exists on model '{model.Name}' — skipping add");
            }
            else
            {
                model.Versions.Add(version);
            }

            // Tags from full model response — sync for both new and existing models.
            // Must reuse existing Tag entities to avoid UNIQUE constraint violations
            // on Tags.NormalizedName (same approach as SyncTagsFromCivitai).
            if (civitaiModel?.Tags is { Count: > 0 } tags)
            {
                // Load tag lookup from DB — avoids loading all models just for tag deduplication
                var knownTags = await unitOfWork.Models.GetAllTagsLookupAsync();

                model.Tags.Clear();

                foreach (var tagName in tags)
                {
                    if (string.IsNullOrWhiteSpace(tagName)) continue;

                    var normalized = tagName.Trim().ToLowerInvariant();

                    if (!knownTags.TryGetValue(normalized, out var tag))
                    {
                        tag = new Tag { Name = tagName, NormalizedName = normalized };
                        knownTags[normalized] = tag;
                    }

                    model.Tags.Add(new ModelTag { Tag = tag });
                }
            }

            if (!isExistingModel)
            {
                await unitOfWork.Models.AddAsync(model);
            }

            await unitOfWork.SaveChangesAsync();

            _logger?.Info(LogCategory.Download, "LoraDownload",
                $"Persisted '{model.Name}' v'{version.Name}' to database ({(isExistingModel ? "added to existing" : "new model")})",
                $"ModelId={model.Id}, VersionId={version.Id}, CivitaiPageId={modelPageId}");
        }
        catch (Exception ex)
        {
            // Non-critical — the file was downloaded; DB entry can be created later
            // by the normal DiscoverNewFilesAsync + "Download Metadata" flow.
            _logger?.Warn(LogCategory.Download, "LoraDownload",
                $"Failed to persist model to database: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses a Civitai base model string to the domain enum.
    /// Delegates to <see cref="BaseModelTypeExtensions.ParseCivitai"/> which uses convention-based
    /// Enum.TryParse — no hardcoded list to maintain.
    /// </summary>
    private static BaseModelType ParseBaseModel(string? baseModelRaw)
        => BaseModelTypeExtensions.ParseCivitai(baseModelRaw);

    /// <summary>
    /// Maps a file extension to the domain FileFormat enum.
    /// Same mapping as <c>ModelFileSyncService.GetFileFormat</c>.
    /// </summary>
    private static FileFormat GetFileFormat(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".safetensors" => FileFormat.SafeTensor,
            ".pt" => FileFormat.PickleTensor,
            ".ckpt" => FileFormat.Other,
            ".pth" => FileFormat.PickleTensor,
            _ => FileFormat.Unknown
        };
    }

    #endregion

    #region Events

    /// <summary>
    /// Raised when the user requests to close the detail panel.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Raised after a version download completes and is persisted to the database.
    /// The parent LoraViewerViewModel subscribes to rebuild tiles with proper grouping.
    /// </summary>
    public event EventHandler? DownloadCompleted;

    #endregion

    #region Private Methods

    private void PopulateFromLocalVersion(ModelTileViewModel tile)
    {
        var model = tile.ModelEntity;
        var version = tile.SelectedVersion;

        ModelIdDisplay = model?.CivitaiId?.ToString() ?? model?.CivitaiModelPageId?.ToString() ?? "\u2014";
        VersionIdDisplay = version?.CivitaiId?.ToString() ?? "\u2014";
        BaseModelDisplay = version?.BaseModelRaw ?? "Unknown";

        // File name
        var primaryFile = version?.PrimaryFile;
        FileNameDisplay = primaryFile?.FileName ?? "\u2014";

        // Description
        DescriptionText = HtmlTextHelper.HtmlToPlainText(model?.Description);

        // Trigger words
        var triggerWords = version?.TriggerWordsText ?? string.Empty;
        TriggerWordsDisplay = triggerWords;
        HasTriggerWords = !string.IsNullOrWhiteSpace(triggerWords);

        // Tags
        PopulateTags(model);

        // Build version tabs from local data only (Civitai fetch will enhance this)
        BuildLocalVersionTabs(tile);
    }

    private void PopulateTags(Model? model)
    {
        TagItems.Clear();
        if (model?.Tags is { Count: > 0 } tags)
        {
            var tagNames = tags
                .Where(t => t.Tag is not null)
                .Select(t => t.Tag!.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            foreach (var tag in tagNames)
            {
                TagItems.Add(tag);
            }

            TagsDisplay = string.Join(", ", tagNames);
            HasTags = tagNames.Count > 0;
        }
        else
        {
            TagsDisplay = string.Empty;
            HasTags = false;
        }

        // Infer category from the first tag that matches a known CivitaiCategory enum value
        // (same logic as MetaDataUtilService.GetCategoryFromTags)
        var category = InferCategoryFromTags(model);
        CategoryDisplay = category ?? string.Empty;
        HasCategory = category is not null;
    }

    /// <summary>
    /// Infers a human-readable category (e.g., "Character", "Style") from the model's tags.
    /// Returns null when no tag matches a known <see cref="Domain.Enums.CivitaiCategory"/> value.
    /// </summary>
    private static string? InferCategoryFromTags(Model? model)
    {
        if (model?.Tags is not { Count: > 0 } tags) return null;

        foreach (var mt in tags)
        {
            var tagName = mt.Tag?.Name;
            if (string.IsNullOrWhiteSpace(tagName)) continue;

            var normalized = tagName.Replace(" ", "_").Trim();
            if (Enum.TryParse<Domain.Enums.CivitaiCategory>(normalized, ignoreCase: true, out var category)
                && category != Domain.Enums.CivitaiCategory.Unknown)
            {
                // Return a friendly display name (e.g., "BaseModel" → "Base Model")
                return category switch
                {
                    Domain.Enums.CivitaiCategory.BaseModel => "Base Model",
                    _ => category.ToString()
                };
            }
        }

        return null;
    }

    private void BuildLocalVersionTabs(ModelTileViewModel tile)
    {
        VersionTabs.Clear();

        foreach (var version in tile.Versions)
        {
            // Map local files to CivitaiModelFile so PersistDownloadedModelAsync has data
            var civFiles = version.Files.Select(f => new CivitaiModelFile
            {
                Id = f.CivitaiId ?? 0,
                Name = f.FileName,
                SizeKB = f.SizeKB,
                Primary = f.IsPrimary,
                DownloadUrl = f.DownloadUrl,
            }).ToList();

            // Map local images to CivitaiModelImage so thumbnails/IDs carry through
            var civImages = version.Images.Select(img => new CivitaiModelImage
            {
                Id = img.CivitaiId,
                Url = img.Url,
                Nsfw = img.IsNsfw,
                Width = img.Width,
                Height = img.Height,
                Hash = img.BlurHash,
                Type = img.MediaType,
                CreatedAt = img.CreatedAt,
                PostId = img.PostId,
                Username = img.Username,
            }).ToList();

            var civitaiVersion = new CivitaiModelVersion
            {
                Id = version.CivitaiId ?? 0,
                ModelId = tile.ModelEntity?.CivitaiId ?? tile.ModelEntity?.CivitaiModelPageId ?? 0,
                Name = version.Name,
                BaseModel = version.BaseModelRaw ?? "Unknown",
                TrainedWords = version.TriggerWords.Select(tw => tw.Word).ToList(),
                DownloadUrl = version.DownloadUrl,
                Files = civFiles,
                Images = civImages,
            };

            var label = !string.IsNullOrWhiteSpace(version.Name) ? version.Name : version.BaseModelRaw ?? "???";
            var tab = new CivitaiVersionTabItem(civitaiVersion, version, label, OnVersionTabSelected);
            VersionTabs.Add(tab);
        }

        // Select the tab matching the tile's currently selected version, or the first tab
        var selectedVersionId = tile.SelectedVersion?.Id;
        var matchingTab = selectedVersionId.HasValue
            ? VersionTabs.FirstOrDefault(t => t.LocalVersion?.Id == selectedVersionId.Value)
            : null;
        if (matchingTab is not null)
        {
            OnVersionTabSelected(matchingTab);
        }
        else if (VersionTabs.Count > 0)
        {
            OnVersionTabSelected(VersionTabs[0]);
        }
    }

    private async Task FetchCivitaiDataAsync(ModelTileViewModel tile)
    {
        if (_civitaiClient is null)
        {
            StatusMessage = "Civitai client not available";
            return;
        }

        var modelId = tile.ModelEntity?.CivitaiId
                      ?? tile.ModelEntity?.CivitaiModelPageId;

        if (modelId is null or 0)
        {
            StatusMessage = "No Civitai ID \u2014 run 'Download Metadata' first";
            return;
        }

        IsLoading = true;
        StatusMessage = "Fetching versions from Civitai...";

        try
        {
            var apiKey = await GetApiKeyAsync();
            var civitaiModel = await _civitaiClient.GetModelAsync(modelId.Value, apiKey);

            if (civitaiModel is null)
            {
                StatusMessage = "Model not found on Civitai";
                return;
            }

            _cachedCivitaiModel = civitaiModel;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Update model-level info
                ModelName = civitaiModel.Name;
                ModelIdDisplay = civitaiModel.Id.ToString();
                DescriptionText = HtmlTextHelper.HtmlToPlainText(civitaiModel.Description);

                // Update tags from Civitai
                if (civitaiModel.Tags.Count > 0)
                {
                    TagItems.Clear();
                    foreach (var tag in civitaiModel.Tags)
                    {
                        TagItems.Add(tag);
                    }
                    TagsDisplay = string.Join(", ", civitaiModel.Tags);
                    HasTags = true;
                }

                // Build version tabs with full Civitai data
                BuildCivitaiVersionTabs(civitaiModel, tile);

                StatusMessage = null;
            });
        }
        catch (HttpRequestException ex)
        {
            _logger?.Error(LogCategory.Network, "ModelDetail",
                $"Failed to fetch model from Civitai: {ex.StatusCode} {ex.Message}", ex);
            StatusMessage = $"Civitai error: {ex.StatusCode}";
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.Network, "ModelDetail",
                $"Failed to fetch model detail: {ex.Message}", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void BuildCivitaiVersionTabs(CivitaiModel civitaiModel, ModelTileViewModel tile)
    {
        // Build a lookup of locally downloaded version CivitaiIds
        var localVersionByCivitaiId = tile.Versions
            .Where(v => v.CivitaiId.HasValue)
            .ToDictionary(v => v.CivitaiId!.Value, v => v);

        // Also match by name as fallback
        var localVersionByName = tile.Versions
            .ToDictionary(v => v.Name, v => v, StringComparer.OrdinalIgnoreCase);

        VersionTabs.Clear();

        foreach (var civVersion in civitaiModel.ModelVersions)
        {
            // Try to find a matching local version
            ModelVersion? localVersion = null;
            if (localVersionByCivitaiId.TryGetValue(civVersion.Id, out var byId))
            {
                localVersion = byId;
            }
            else if (localVersionByName.TryGetValue(civVersion.Name, out var byName))
            {
                localVersion = byName;
            }

            var label = !string.IsNullOrWhiteSpace(civVersion.Name) ? civVersion.Name : civVersion.BaseModel;
            var tab = new CivitaiVersionTabItem(civVersion, localVersion, label, OnVersionTabSelected);
            VersionTabs.Add(tab);
        }

        // Select the tab matching the tile's currently selected version, then fall back
        // to the first downloaded tab, then the first tab overall.
        var selectedVersionId = tile.SelectedVersion?.Id;
        var matchingTab = selectedVersionId.HasValue
            ? VersionTabs.FirstOrDefault(t => t.LocalVersion?.Id == selectedVersionId.Value)
            : null;
        var firstTab = matchingTab
                       ?? VersionTabs.FirstOrDefault(t => t.IsDownloaded)
                       ?? VersionTabs.FirstOrDefault();
        if (firstTab is not null)
        {
            OnVersionTabSelected(firstTab);
        }
    }

    private void OnVersionTabSelected(CivitaiVersionTabItem selected)
    {
        foreach (var tab in VersionTabs)
        {
            tab.IsSelected = ReferenceEquals(tab, selected);
        }

        SelectedVersionTab = selected;

        // Update display for the selected version
        VersionIdDisplay = selected.CivitaiVersion.Id > 0
            ? selected.CivitaiVersion.Id.ToString()
            : "\u2014";
        BaseModelDisplay = selected.BaseModel;

        // Trigger words
        TriggerWordsDisplay = selected.TriggerWords;
        HasTriggerWords = selected.HasTriggerWords;

        // File name from Civitai or local
        if (selected.LocalVersion?.PrimaryFile is { } localFile)
        {
            FileNameDisplay = localFile.FileName ?? "\u2014";
        }
        else
        {
            var civFile = selected.CivitaiVersion.Files.FirstOrDefault(f => f.Primary == true)
                          ?? selected.CivitaiVersion.Files.FirstOrDefault();
            FileNameDisplay = civFile?.Name ?? "\u2014";
        }

        // Update thumbnail if local version available
        if (selected.LocalVersion is not null && SourceTile is not null)
        {
            // Find the matching version button on the source tile and select it
            var matchingButton = SourceTile.VersionButtons
                .FirstOrDefault(b => b.Version.Id == selected.LocalVersion.Id);
            if (matchingButton is not null)
            {
                matchingButton.SelectCommand.Execute(null);
                ThumbnailImage = SourceTile.ThumbnailImage;
            }
        }
        else if (selected.CivitaiVersion.Images.Count > 0)
        {
            // Cancel any in-flight thumbnail download from a previous version tab
            _detailThumbnailCts?.Cancel();
            _detailThumbnailCts?.Dispose();
            _detailThumbnailCts = new CancellationTokenSource();

            // Load first image from Civitai version
            _ = LoadCivitaiThumbnailAsync(selected.CivitaiVersion.Images[0], _detailThumbnailCts.Token);
        }
    }

    private async Task LoadCivitaiThumbnailAsync(CivitaiModelImage image, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(image.Url)) return;

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var data = await httpClient.GetByteArrayAsync(image.Url, ct);
            if (data.Length == 0) return;

            ct.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    using var stream = new MemoryStream(data);
                    ThumbnailImage = new Bitmap(stream);
                }
                catch
                {
                    // Image decode failure — ignore
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Version tab changed while downloading — discard silently
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.Network, "ModelDetail",
                $"Failed to load Civitai thumbnail: {ex.Message}");
        }
    }

    /// <summary>
    /// Retrieves the Civitai API key using a fresh DI scope to avoid stale EF Core tracked entities.
    /// The injected <c>_settingsService</c> may hold a cached <see cref="AppSettings"/> entity from
    /// a long-lived DbContext that was loaded before the key was saved via the Settings view.
    /// </summary>
    private static async Task<string?> GetApiKeyAsync()
    {
        using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        return await settingsService.GetCivitaiApiKeyAsync();
    }

    /// <summary>
    /// Checks whether a Civitai API token is configured. If not, opens a dialog
    /// for the user to enter one. Returns true when a token is available (either
    /// already configured or just provided), false when the user cancelled.
    /// </summary>
    private async Task<bool> EnsureCivitaiTokenAsync()
    {
        var existingKey = await GetApiKeyAsync();
        if (!string.IsNullOrWhiteSpace(existingKey))
            return true;

        // Show the token dialog on the UI thread
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var mainWindow = (App.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (mainWindow is null) return false;

            var dialog = new CivitaiTokenDialog();
            await dialog.ShowDialog(mainWindow);

            if (!dialog.IsSaved || string.IsNullOrWhiteSpace(dialog.TokenText))
                return false;

            // Persist the token (encrypted) via the settings service
            if (_settingsService is not null)
            {
                await _settingsService.SetCivitaiApiKeyAsync(dialog.TokenText);
                _logger?.Info(LogCategory.General, "CivitaiToken",
                    "Civitai API token saved from download prompt");
            }

            return true;
        });
    }

    private static async Task CopyToClipboardAsync(string text)
    {
        var topLevel = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        var clipboard = topLevel?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    #endregion
}
