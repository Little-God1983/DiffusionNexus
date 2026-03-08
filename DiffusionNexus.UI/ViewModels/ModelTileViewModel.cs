using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
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
    /// Open model on Civitai. Falls back to a version-level URL when the
    /// model-level CivitaiId hasn't been synced yet.
    /// </summary>
    [RelayCommand]
    private void OpenOnCivitai()
    {
        string? url = null;

        if (ModelEntity?.CivitaiId is not null)
        {
            url = $"https://civitai.com/models/{ModelEntity.CivitaiId}";
        }
        else if (SelectedVersion?.CivitaiId is not null)
        {
            url = $"https://civitai.com/models/{SelectedVersion.CivitaiId}";
        }

        if (url is null) return;

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
        // Populate versions collection
        Versions.Clear();
        VersionButtons.Clear();
        
        if (value?.Versions is not null)
        {
            foreach (var version in value.Versions.OrderByDescending(v => v.CreatedAt))
            {
                Versions.Add(version);
                
                // Create button with short label from base model
                var (label, icon) = GetVersionButtonInfo(version);
                var button = new VersionButtonViewModel(version, label, icon, OnVersionButtonSelected);
                VersionButtons.Add(button);
            }
        }

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(ModelTypeDisplay));
        OnPropertyChanged(nameof(BaseModelsDisplay));
        OnPropertyChanged(nameof(IsNsfw));
        OnPropertyChanged(nameof(HasMultipleVersions));
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
    /// Downloads a thumbnail from a Civitai image URL and caches it as a BLOB.
    /// </summary>
    private async Task DownloadThumbnailAsync(ModelImage image)
    {
        IsLoading = true;
        try
        {
            // Civitai supports width parameter for resized images
            var thumbnailUrl = image.Url.Contains('?')
                ? $"{image.Url}&width=300"
                : $"{image.Url}/width=300";

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var imageBytes = await httpClient.GetByteArrayAsync(thumbnailUrl).ConfigureAwait(false);

            if (imageBytes.Length == 0) return;

            // Store in-memory for immediate display
            image.ThumbnailData = imageBytes;
            image.ThumbnailMimeType = "image/jpeg";

            // Persist BLOB to the database so next startup is instant
            if (image.Id > 0)
            {
                await PersistThumbnailAsync(image.Id, imageBytes);
            }

            // Display the downloaded thumbnail
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    using var stream = new MemoryStream(imageBytes);
                    ThumbnailImage = new Bitmap(stream);
                }
                catch
                {
                    ThumbnailImage = null;
                }
            });
        }
        catch
        {
            // Network errors are expected — silently fail
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
        }
    }

    /// <summary>
    /// Persists downloaded thumbnail bytes to the database for a given ModelImage.
    /// </summary>
    private static async Task PersistThumbnailAsync(int imageId, byte[] thumbnailData)
    {
        try
        {
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataAccess.Data.DiffusionNexusCoreDbContext>();
            var dbImage = await dbContext.ModelImages.FindAsync(imageId);
            if (dbImage is not null)
            {
                dbImage.ThumbnailData = thumbnailData;
                dbImage.ThumbnailMimeType = "image/jpeg";
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
        var vm = new ModelTileViewModel
        {
            ModelEntity = model
        };
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
