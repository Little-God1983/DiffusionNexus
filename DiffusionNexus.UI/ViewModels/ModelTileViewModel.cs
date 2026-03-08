using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for a single model tile in the LoRA Helper grid.
/// </summary>
public partial class ModelTileViewModel : ViewModelBase
{
    #region Observable Properties

    /// <summary>
    /// The model entity.
    /// </summary>
    [ObservableProperty]
    private Model? _modelEntity;

    /// <summary>
    /// The currently selected version.
    /// </summary>
    [ObservableProperty]
    private ModelVersion? _selectedVersion;

    /// <summary>
    /// The thumbnail image to display.
    /// </summary>
    [ObservableProperty]
    private Bitmap? _thumbnailImage;

    /// <summary>
    /// Whether metadata is being loaded.
    /// </summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Whether the tile is selected.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    #endregion

    #region Grouping

    /// <summary>
    /// All model entities in this group (same Civitai page).
    /// For ungrouped models this contains just the single model.
    /// </summary>
    private List<Model> _allGroupedModels = [];

    #endregion

    #region Collections

    /// <summary>
    /// Available versions for the version selector.
    /// </summary>
    public ObservableCollection<ModelVersion> Versions { get; } = [];

    /// <summary>
    /// Version toggle buttons for the UI.
    /// </summary>
    public ObservableCollection<VersionButtonViewModel> VersionButtons { get; } = [];

    #endregion

    #region Base Model Display Mappings

    private static readonly Dictionary<string, (string Short, string? Icon)> BaseModelMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SD 1.5"] = ("1.5", null),
        ["SDXL 1.0"] = ("XL", null),
        ["SDXL 0.9"] = ("XL 0.9", null),
        ["SDXL Turbo"] = ("XL ⚡", null),
        ["SDXL Lightning"] = ("XL ⚡⚡", null),
        ["Pony"] = ("Pony", "🐎"),
        ["Illustrious"] = ("IL", null),
        ["Flux.1 S"] = ("Flux S", null),
        ["Flux.1 D"] = ("F.1D", null),
        ["Z-Image-Turbo"] = ("ZIT", "ZI⚡"),
        ["Wan Video 14B t2v"] = ("Wan 14B", "🎬"),
        ["Wan Video 1.3B t2v"] = ("Wan 1.3B", "🎬 1.3"),
        ["NoobAI"] = ("Noob", null),
        ["???"] = ("???", null), // Unknown base model indicator
    };

    #endregion

    #region Computed Properties

    /// <summary>
    /// Model name for display.
    /// </summary>
    public string DisplayName => ModelEntity?.Name ?? SelectedVersion?.Name ?? "Unknown Model";

    /// <summary>
    /// The filename on disk (without extension).
    /// </summary>
    public string FileName
    {
        get
        {
            var file = SelectedVersion?.Files?.FirstOrDefault(f => f.IsPrimary) 
                       ?? SelectedVersion?.Files?.FirstOrDefault();
            if (file?.FileName is not null)
            {
                // Remove extension
                var name = file.FileName;
                var lastDot = name.LastIndexOf('.');
                return lastDot > 0 ? name[..lastDot] : name;
            }
            return DisplayName; // Fall back to display name if no file info
        }
    }

    /// <summary>
    /// Model type display (e.g., "LORA", "Checkpoint").
    /// </summary>
    public string ModelTypeDisplay => ModelEntity?.Type.ToString().ToUpperInvariant() ?? "UNKNOWN";

    /// <summary>
    /// Base models display string with short names.
    /// Shows the base model for the currently selected version.
    /// </summary>
    public string BaseModelsDisplay
    {
        get
        {
            if (SelectedVersion is not null)
            {
                return FormatBaseModel(SelectedVersion.BaseModelRaw);
            }
            return "???";
        }
    }

    /// <summary>
    /// Whether this model has NSFW content.
    /// </summary>
    public bool IsNsfw => ModelEntity?.IsNsfw ?? false;

    /// <summary>
    /// Whether this model has multiple versions.
    /// </summary>
    public bool HasMultipleVersions => Versions.Count > 1;

    /// <summary>
    /// Whether this tile groups multiple model entities (same Civitai page).
    /// </summary>
    public bool IsGrouped => _allGroupedModels.Count > 1;

    /// <summary>
    /// Version count display.
    /// </summary>
    public string VersionCountDisplay => HasMultipleVersions
        ? $"{Versions.Count} versions"
        : string.Empty;

    /// <summary>
    /// Creator name.
    /// </summary>
    public string CreatorName => ModelEntity?.Creator?.Username ?? "Unknown";

    /// <summary>
    /// Download count display.
    /// </summary>
    public string DownloadCountDisplay
    {
        get
        {
            var count = SelectedVersion?.DownloadCount ?? ModelEntity?.TotalDownloads ?? 0;
            return count switch
            {
                >= 1_000_000 => $"{count / 1_000_000.0:F1}M",
                >= 1_000 => $"{count / 1_000.0:F1}K",
                _ => count.ToString()
            };
        }
    }

    /// <summary>
    /// Whether a thumbnail is available.
    /// </summary>
    public bool HasThumbnail => ThumbnailImage is not null;

    /// <summary>
    /// Whether to show placeholder.
    /// </summary>
    public bool ShowPlaceholder => !HasThumbnail && !IsLoading;

    #endregion

    #region Commands

    /// <summary>
    /// Open model details.
    /// </summary>
    [RelayCommand]
    private void OpenDetails()
    {
        // Will be implemented when details view is created
    }

    /// <summary>
    /// Copy trigger words to clipboard.
    /// </summary>
    [RelayCommand]
    private async Task CopyTriggerWordsAsync()
    {
        var triggerWords = SelectedVersion?.TriggerWordsText;
        if (string.IsNullOrWhiteSpace(triggerWords)) return;

        await CopyToClipboardAsync(triggerWords);
    }

    /// <summary>
    /// Copy model filename to clipboard.
    /// </summary>
    [RelayCommand]
    private async Task CopyFileNameAsync()
    {
        var fileName = FileName;
        if (string.IsNullOrWhiteSpace(fileName)) return;

        await CopyToClipboardAsync(fileName);
    }

    /// <summary>
    /// Copies text to the system clipboard via the Avalonia TopLevel.
    /// </summary>
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

    /// <summary>
    /// Open model on Civitai. Tries multiple ID sources to build the URL:
    /// CivitaiId → CivitaiModelPageId → SelectedVersion.CivitaiId (version-level URL).
    /// Logs a warning to the Unified Console when no Civitai link is available.
    /// </summary>
    [RelayCommand]
    private void OpenOnCivitai()
    {
        string? url = null;

        if (ModelEntity?.CivitaiId is { } modelCivitaiId)
        {
            url = $"https://civitai.com/models/{modelCivitaiId}";
        }
        else if (ModelEntity?.CivitaiModelPageId is { } pageId)
        {
            url = $"https://civitai.com/models/{pageId}";
        }
        else if (SelectedVersion?.CivitaiId is { } versionCivitaiId)
        {
            // Version-level ID — link to the version page directly
            url = $"https://civitai.com/api/v1/model-versions/{versionCivitaiId}";
        }

        if (url is null)
        {
            var logger = App.Services?.GetService<IUnifiedLogger>();
            logger?.Warn(LogCategory.General, "OpenOnCivitai",
                $"No Civitai link available for '{DisplayName}' — run 'Download Metadata' first to sync with Civitai.");
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
        {
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Open containing folder.
    /// </summary>
    [RelayCommand]
    private void OpenFolder()
    {
        var file = SelectedVersion?.Files?.FirstOrDefault(f => f.LocalPath is not null);
        if (file?.LocalPath is null)
        {
            return;
        }

        var folder = Path.GetDirectoryName(file.LocalPath);
        if (folder is not null && Directory.Exists(folder))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder)
            {
                UseShellExecute = true
            });
        }
    }

    /// <summary>
    /// Delete the model.
    /// </summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        // TODO: Implement delete with confirmation
        await Task.CompletedTask;
    }

    #endregion

    #region Lifecycle

    partial void OnModelEntityChanged(Model? value)
    {
        // Populate versions from all grouped models (or just the primary model)
        Versions.Clear();
        VersionButtons.Clear();

        var allVersions = _allGroupedModels.Count > 0
            ? _allGroupedModels.SelectMany(m => m.Versions)
            : value?.Versions ?? Enumerable.Empty<ModelVersion>();

        // Deduplicate versions that share the same primary filename (re-discovery duplicates).
        // Keep the version with the richest data per filename.
        var uniqueVersions = DeduplicateVersions(allVersions);

        var isGrouped = _allGroupedModels.Count > 1;

        foreach (var version in uniqueVersions.OrderByDescending(v => v.CreatedAt))
        {
            Versions.Add(version);

            // Create button with short label from base model
            var (label, icon) = GetVersionButtonInfo(version);
            var tooltip = BuildVersionTooltip(version, isGrouped);
            var button = new VersionButtonViewModel(version, label, icon, tooltip, OnVersionButtonSelected);
            VersionButtons.Add(button);
        }

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(ModelTypeDisplay));
        OnPropertyChanged(nameof(BaseModelsDisplay));
        OnPropertyChanged(nameof(IsNsfw));
        OnPropertyChanged(nameof(HasMultipleVersions));
        OnPropertyChanged(nameof(IsGrouped));
        OnPropertyChanged(nameof(VersionCountDisplay));
        OnPropertyChanged(nameof(CreatorName));
        OnPropertyChanged(nameof(DownloadCountDisplay));

        // Auto-select first version
        if (VersionButtons.Count > 0)
        {
            OnVersionButtonSelected(VersionButtons.First());
        }
    }

    partial void OnSelectedVersionChanged(ModelVersion? value)
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(BaseModelsDisplay));
        OnPropertyChanged(nameof(DownloadCountDisplay));
        LoadThumbnailFromVersion();
    }

    partial void OnThumbnailImageChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Deduplicates versions that share the same primary filename (re-discovery duplicates).
    /// Keeps the version with the richest metadata per unique filename.
    /// </summary>
    private static List<ModelVersion> DeduplicateVersions(IEnumerable<ModelVersion> versions)
    {
        var byFile = new Dictionary<string, ModelVersion>(StringComparer.OrdinalIgnoreCase);

        foreach (var version in versions)
        {
            var fileName = version.PrimaryFile?.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                // No file info — keep using a unique synthetic key
                byFile[$"__no_file_{version.Id}_{byFile.Count}"] = version;
                continue;
            }

            if (byFile.TryGetValue(fileName, out var existing))
            {
                // Keep the one with CivitaiId, then more images
                if (version.CivitaiId.HasValue && !existing.CivitaiId.HasValue)
                    byFile[fileName] = version;
                else if (version.Images.Count > existing.Images.Count)
                    byFile[fileName] = version;
            }
            else
            {
                byFile[fileName] = version;
            }
        }

        return byFile.Values.ToList();
    }

    private void OnVersionButtonSelected(VersionButtonViewModel selected)
    {
        // Update all button states
        foreach (var button in VersionButtons)
        {
            button.IsSelected = ReferenceEquals(button, selected);
        }
        
        // Update selected version
        SelectedVersion = selected.Version;
    }

    private static (string Label, string? Icon) GetVersionButtonInfo(ModelVersion version)
    {
        // Try to get short label from base model
        if (!string.IsNullOrWhiteSpace(version.BaseModelRaw))
        {
            if (BaseModelMappings.TryGetValue(version.BaseModelRaw, out var mapping))
            {
                return (mapping.Short, mapping.Icon);
            }

            // Truncate if too long
            var baseModel = version.BaseModelRaw;
            if (baseModel.Length > 8)
            {
                return (baseModel[..7] + "…", null);
            }
            return (baseModel, null);
        }

        // Fall back to version name
        if (!string.IsNullOrWhiteSpace(version.Name))
        {
            var name = version.Name;
            if (name.Length > 8)
            {
                return (name[..7] + "…", null);
            }
            return (name, null);
        }

        return ("???", null);
    }

    /// <summary>
    /// Builds a rich tooltip for a version button showing version name and filename.
    /// </summary>
    private static string BuildVersionTooltip(ModelVersion version, bool isGrouped)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(version.Name))
        {
            parts.Add(version.Name);
        }

        if (isGrouped)
        {
            var file = version.PrimaryFile;
            if (file is not null && !string.IsNullOrWhiteSpace(file.FileName))
            {
                parts.Add($"File: {file.FileName}");
            }

            if (!string.IsNullOrWhiteSpace(version.BaseModelRaw))
            {
                parts.Add($"Base: {version.BaseModelRaw}");
            }
        }

        return parts.Count > 0 ? string.Join("\n", parts) : "Unknown version";
    }

    private static string FormatBaseModel(string? baseModel)
    {
        if (string.IsNullOrWhiteSpace(baseModel))
        {
            return "???";
        }

        if (BaseModelMappings.TryGetValue(baseModel, out var mapping))
        {
            return mapping.Icon is not null
                ? $"{mapping.Icon} {mapping.Short}"
                : mapping.Short;
        }

        // Return truncated original if no mapping
        return baseModel.Length > 12 ? baseModel[..11] + "…" : baseModel;
    }

    private void LoadThumbnailFromVersion()
    {
        if (SelectedVersion?.PrimaryImage?.ThumbnailData is { } data && data.Length > 0)
        {
            try
            {
                using var stream = new MemoryStream(data);
                ThumbnailImage = new Bitmap(stream);
            }
            catch
            {
                ThumbnailImage = null;
            }
        }
        else if (SelectedVersion?.PrimaryImage is { } image && !string.IsNullOrEmpty(image.Url))
        {
            // No BLOB cached yet — download from Civitai URL in background
            ThumbnailImage = null;
            _ = DownloadThumbnailAsync(image);
        }
        else
        {
            ThumbnailImage = null;
        }
    }

    /// <summary>
    /// Returns true if the model was synced with Civitai but the selected version has no preview images.
    /// These models need their image records re-fetched from the Civitai API.
    /// </summary>
    public bool IsImageDataMissing =>
        ModelEntity?.CivitaiId is not null
        && SelectedVersion?.CivitaiId is not null
        && (SelectedVersion.Images is null || SelectedVersion.Images.Count == 0);

    /// <summary>
    /// Returns true if the tile is showing "No Preview" but has a downloadable image URL.
    /// Checks the actual visual state (Bitmap) rather than entity data, so it catches
    /// corrupt BLOBs and decode failures too.
    /// </summary>
    public bool IsThumbnailMissing =>
        ThumbnailImage is null
        && SelectedVersion?.PrimaryImage is { } img
        && !string.IsNullOrEmpty(img.Url);

    /// <summary>
    /// Attempts to download the thumbnail for the selected version if it is missing.
    /// </summary>
    public async Task TryDownloadMissingThumbnailAsync()
    {
        if (!IsThumbnailMissing) return;
        await DownloadThumbnailAsync(SelectedVersion!.PrimaryImage!);
    }

    /// <summary>
    /// Downloads a thumbnail from a Civitai image URL and caches it as a BLOB.
    /// For video previews, downloads the video to a temp file and extracts the mid-frame.
    /// </summary>
    private async Task DownloadThumbnailAsync(ModelImage image)
    {
        var logger = App.Services?.GetService<IUnifiedLogger>();
        var isVideo = IsVideoPreview(image);
        var previewType = isVideo ? "video" : "image";
        var displayName = DisplayName;

        logger?.Debug(LogCategory.Network, "ThumbnailDownload",
            $"Downloading {previewType} thumbnail for '{displayName}'",
            $"URL: {image.Url}\nMediaType: {image.MediaType ?? "(null)"}");

        IsLoading = true;
        try
        {
            byte[] thumbnailBytes;
            string mimeType;

            if (isVideo)
            {
                (thumbnailBytes, mimeType) = await DownloadVideoThumbnailAsync(image.Url, logger).ConfigureAwait(false);
            }
            else
            {
                (thumbnailBytes, mimeType) = await DownloadImageThumbnailAsync(image.Url).ConfigureAwait(false);
            }

            if (thumbnailBytes.Length == 0)
            {
                logger?.Warn(LogCategory.Network, "ThumbnailDownload",
                    $"Empty {previewType} thumbnail for '{displayName}'");
                return;
            }

            logger?.Info(LogCategory.Network, "ThumbnailDownload",
                $"Thumbnail created for '{displayName}' ({previewType}, {thumbnailBytes.Length / 1024.0:F1} KB)");

            // Store in-memory for immediate display
            image.ThumbnailData = thumbnailBytes;
            image.ThumbnailMimeType = mimeType;

            // Persist BLOB to the database so next startup is instant
            if (image.Id > 0)
            {
                await PersistThumbnailAsync(image.Id, thumbnailBytes, mimeType);
            }

            // Display the downloaded thumbnail
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    using var stream = new MemoryStream(thumbnailBytes);
                    ThumbnailImage = new Bitmap(stream);
                }
                catch
                {
                    ThumbnailImage = null;
                }
            });
        }
        catch (Exception ex)
        {
            logger?.Error(LogCategory.Network, "ThumbnailDownload",
                $"Failed to create {previewType} thumbnail for '{displayName}': {ex.Message}", ex);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
        }
    }

    /// <summary>
    /// Determines whether a preview image is a video based on MediaType or URL extension.
    /// Falls back to URL extension for legacy records that don't have MediaType set.
    /// </summary>
    private static bool IsVideoPreview(ModelImage image)
    {
        if (image.IsVideo)
            return true;

        // Fallback: detect video by URL extension for legacy records without MediaType
        if (image.MediaType is null && !string.IsNullOrEmpty(image.Url))
        {
            var extension = Path.GetExtension(new Uri(image.Url).AbsolutePath);
            return extension is ".mp4" or ".webm" or ".mov" or ".avi" or ".mkv";
        }

        return false;
    }

    /// <summary>
    /// Downloads an image thumbnail from a Civitai URL.
    /// </summary>
    private static async Task<(byte[] Data, string MimeType)> DownloadImageThumbnailAsync(string url)
    {
        // Civitai supports width parameter for resized images
        var thumbnailUrl = url.Contains('?')
            ? $"{url}&width=300"
            : $"{url}/width=300";

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var bytes = await httpClient.GetByteArrayAsync(thumbnailUrl).ConfigureAwait(false);
        return (bytes, "image/jpeg");
    }

    /// <summary>
    /// Downloads a video preview from Civitai, extracts a frame at the midpoint using FFmpeg,
    /// and returns the frame as thumbnail bytes.
    /// </summary>
    private static async Task<(byte[] Data, string MimeType)> DownloadVideoThumbnailAsync(
        string videoUrl, IUnifiedLogger? logger)
    {
        var videoThumbnailService = App.Services?.GetService<IVideoThumbnailService>();
        if (videoThumbnailService is null)
        {
            logger?.Warn(LogCategory.General, "ThumbnailDownload",
                "IVideoThumbnailService not available — cannot extract video thumbnail");
            return ([], string.Empty);
        }

        var tempVideoPath = Path.Combine(Path.GetTempPath(), $"dn_preview_{Guid.NewGuid():N}.mp4");
        string? generatedThumbnailPath = null;
        try
        {
            // Download the video to a temp file
            logger?.Debug(LogCategory.Network, "ThumbnailDownload",
                $"Downloading video to temp: {tempVideoPath}",
                $"URL: {videoUrl}");

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var videoBytes = await httpClient.GetByteArrayAsync(videoUrl).ConfigureAwait(false);
            await File.WriteAllBytesAsync(tempVideoPath, videoBytes).ConfigureAwait(false);

            logger?.Debug(LogCategory.Network, "ThumbnailDownload",
                $"Video downloaded ({videoBytes.Length / 1024.0:F0} KB), extracting mid-frame...");

            // Extract mid-frame thumbnail (VideoThumbnailService defaults to midpoint)
            var result = await videoThumbnailService.GenerateThumbnailAsync(
                tempVideoPath,
                new VideoThumbnailOptions { MaxWidth = 300, OutputFormat = ThumbnailFormat.WebP })
                .ConfigureAwait(false);

            if (!result.Success || string.IsNullOrEmpty(result.ThumbnailPath))
            {
                logger?.Warn(LogCategory.General, "ThumbnailDownload",
                    $"FFmpeg frame extraction failed: {result.ErrorMessage ?? "unknown error"}");
                return ([], string.Empty);
            }

            generatedThumbnailPath = result.ThumbnailPath;
            var thumbnailBytes = await File.ReadAllBytesAsync(result.ThumbnailPath).ConfigureAwait(false);

            logger?.Debug(LogCategory.General, "ThumbnailDownload",
                $"Video frame extracted ({thumbnailBytes.Length / 1024.0:F1} KB, {result.Width}x{result.Height})",
                $"Captured at {result.CapturePosition} of {result.VideoDuration}");

            return (thumbnailBytes, "image/webp");
        }
        finally
        {
            // TODO: Linux Implementation for temp file cleanup
            TryDeleteFile(tempVideoPath);
            if (generatedThumbnailPath is not null)
                TryDeleteFile(generatedThumbnailPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch { /* Best-effort cleanup */ }
    }

    /// <summary>
    /// Persists downloaded thumbnail bytes to the database for a given ModelImage.
    /// </summary>
    private static async Task PersistThumbnailAsync(int imageId, byte[] thumbnailData, string mimeType)
    {
        try
        {
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataAccess.Data.DiffusionNexusCoreDbContext>();
            var dbImage = await dbContext.ModelImages.FindAsync(imageId);
            if (dbImage is not null)
            {
                dbImage.ThumbnailData = thumbnailData;
                dbImage.ThumbnailMimeType = mimeType;
                await dbContext.SaveChangesAsync();
            }
        }
        catch
        {
            // Non-critical — thumbnail will be re-downloaded next time
        }
    }

    #endregion

    #region Factory Methods

    /// <summary>
    /// Creates a ModelTileViewModel from a Model entity.
    /// </summary>
    public static ModelTileViewModel FromModel(Model model)
    {
        var vm = new ModelTileViewModel();
        vm._allGroupedModels = [model];
        vm.ModelEntity = model;
        return vm;
    }

    /// <summary>
    /// Creates a ModelTileViewModel from multiple Model entities that share the same Civitai page.
    /// Versions from all models are merged into a single tile.
    /// </summary>
    public static ModelTileViewModel FromModelGroup(IReadOnlyList<Model> models)
    {
        // Use the model with the richest data as the primary display model
        var primary = models
            .OrderByDescending(m => m.CivitaiId.HasValue)
            .ThenByDescending(m => m.Versions.Sum(v => v.Images.Count))
            .ThenByDescending(m => m.LastSyncedAt)
            .First();

        var vm = new ModelTileViewModel();
        vm._allGroupedModels = models.ToList();
        vm.ModelEntity = primary;
        return vm;
    }

    /// <summary>
    /// Creates demo data for design-time and testing.
    /// </summary>
    public static ModelTileViewModel CreateDemo(
        string name,
        string creatorName,
        params string[] baseModels)
    {
        var creator = new Creator { Username = creatorName };
        var model = new Model
        {
            Name = name,
            Type = Domain.Enums.ModelType.LORA,
            Creator = creator,
            CreatorId = 1
        };

        var versionNum = 1;
        foreach (var baseModel in baseModels)
        {
            var version = new ModelVersion
            {
                Name = $"v{versionNum}.0 - {baseModel}",
                BaseModelRaw = baseModel,
                DownloadCount = Random.Shared.Next(100, 50000),
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(1, 90) * versionNum)
            };
            
            // Add a demo file with filename
            version.Files.Add(new ModelFile
            {
                FileName = $"{name.Replace(" ", "_").Replace("(", "").Replace(")", "")}_v{versionNum}.safetensors",
                IsPrimary = true
            });
            
            model.Versions.Add(version);
            versionNum++;
        }

        return FromModel(model);
    }

    #endregion
}
