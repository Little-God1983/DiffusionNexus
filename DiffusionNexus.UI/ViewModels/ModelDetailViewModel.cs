using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Infrastructure;
using DiffusionNexus.UI.Helpers;
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
    /// Opens the Civitai download URL in the default browser.
    /// </summary>
    [RelayCommand]
    private void DownloadSelectedVersion()
    {
        var tab = SelectedVersionTab;
        if (tab is null || tab.IsDownloaded) return;

        var downloadUrl = tab.DownloadUrl;
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            // Fall back to model page URL
            var modelId = tab.CivitaiVersion.ModelId;
            if (modelId > 0)
            {
                downloadUrl = $"https://civitai.com/models/{modelId}?modelVersionId={tab.CivitaiVersion.Id}";
            }
        }

        if (!string.IsNullOrWhiteSpace(downloadUrl))
        {
            // TODO: Linux Implementation for download task
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(downloadUrl)
            {
                UseShellExecute = true
            });
        }
    }

    #endregion

    #region Events

    /// <summary>
    /// Raised when the user requests to close the detail panel.
    /// </summary>
    public event EventHandler? CloseRequested;

    #endregion

    #region Private Methods

    private void PopulateFromLocalVersion(ModelTileViewModel tile)
    {
        var model = tile.ModelEntity;
        var version = tile.SelectedVersion;

        ModelIdDisplay = model?.CivitaiId?.ToString() ?? model?.CivitaiModelPageId?.ToString() ?? "—";
        VersionIdDisplay = version?.CivitaiId?.ToString() ?? "—";
        BaseModelDisplay = version?.BaseModelRaw ?? "Unknown";

        // File name
        var primaryFile = version?.PrimaryFile;
        FileNameDisplay = primaryFile?.FileName ?? "—";

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
    }

    private void BuildLocalVersionTabs(ModelTileViewModel tile)
    {
        VersionTabs.Clear();

        foreach (var version in tile.Versions)
        {
            var civitaiVersion = new CivitaiModelVersion
            {
                Id = version.CivitaiId ?? 0,
                ModelId = tile.ModelEntity?.CivitaiId ?? tile.ModelEntity?.CivitaiModelPageId ?? 0,
                Name = version.Name,
                BaseModel = version.BaseModelRaw ?? "Unknown",
                TrainedWords = version.TriggerWords.Select(tw => tw.Word).ToList(),
                DownloadUrl = version.DownloadUrl,
            };

            var label = !string.IsNullOrWhiteSpace(version.Name) ? version.Name : version.BaseModelRaw ?? "???";
            var tab = new CivitaiVersionTabItem(civitaiVersion, version, label, OnVersionTabSelected);
            VersionTabs.Add(tab);
        }

        // Select first tab
        if (VersionTabs.Count > 0)
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
            StatusMessage = "No Civitai ID — run 'Download Metadata' first";
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

        // Select the first downloaded tab, or the first tab
        var firstDownloaded = VersionTabs.FirstOrDefault(t => t.IsDownloaded);
        var firstTab = firstDownloaded ?? VersionTabs.FirstOrDefault();
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
            : "—";
        BaseModelDisplay = selected.BaseModel;

        // Trigger words
        TriggerWordsDisplay = selected.TriggerWords;
        HasTriggerWords = selected.HasTriggerWords;

        // File name from Civitai or local
        if (selected.LocalVersion?.PrimaryFile is { } localFile)
        {
            FileNameDisplay = localFile.FileName ?? "—";
        }
        else
        {
            var civFile = selected.CivitaiVersion.Files.FirstOrDefault(f => f.Primary == true)
                          ?? selected.CivitaiVersion.Files.FirstOrDefault();
            FileNameDisplay = civFile?.Name ?? "—";
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
            // Load first image from Civitai version
            _ = LoadCivitaiThumbnailAsync(selected.CivitaiVersion.Images[0]);
        }
    }

    private async Task LoadCivitaiThumbnailAsync(CivitaiModelImage image)
    {
        if (string.IsNullOrWhiteSpace(image.Url)) return;

        try
        {
            using var httpClient = new HttpClient();
            var data = await httpClient.GetByteArrayAsync(image.Url);
            if (data.Length == 0) return;

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
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.Network, "ModelDetail",
                $"Failed to load Civitai thumbnail: {ex.Message}");
        }
    }

    private async Task<string?> GetApiKeyAsync()
    {
        if (_settingsService is null || _secureStorage is null) return null;
        var settings = await _settingsService.GetSettingsAsync();
        return _secureStorage.Decrypt(settings.EncryptedCivitaiApiKey);
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
