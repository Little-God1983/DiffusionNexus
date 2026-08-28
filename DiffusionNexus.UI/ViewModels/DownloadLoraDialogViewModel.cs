using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Infrastructure.Services;
using DiffusionNexus.Service.Services.Lora;
using DiffusionNexus.UI.Helpers;
using DiffusionNexus.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for downloading a LoRA from a pasted Civitai link.
/// </summary>
public partial class DownloadLoraDialogViewModel : ObservableObject
{
    private readonly ICivitaiClient? _civitaiClient;
    private readonly IAppSettingsService? _settingsService;
    private readonly IDialogService? _dialogService;
    private readonly IUnifiedLogger? _logger;
    private ICivitaiApiKeyProvider? _apiKeyProvider;

    /// <summary>
    /// One client for the lifetime of the process. A per-operation HttpClient discards the
    /// connection pool and the TLS session every time, which is socket churn against a host we
    /// are already asking to be patient with us. PooledConnectionLifetime keeps a process-lifetime
    /// client from pinning a stale DNS answer for the app's whole run.
    /// </summary>
    private static readonly HttpClient s_previewHttp = new(
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    { Timeout = TimeSpan.FromSeconds(15) };

    [ObservableProperty]
    private string _urlText = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _hasPreview;

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private bool _hasPreviewImage;

    [ObservableProperty]
    private string _previewName = string.Empty;

    [ObservableProperty]
    private string _previewType = string.Empty;

    [ObservableProperty]
    private string _previewVersion = string.Empty;

    [ObservableProperty]
    private string _previewBaseModel = string.Empty;

    [ObservableProperty]
    private string _previewCreator = string.Empty;

    [ObservableProperty]
    private string _previewIds = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _fileSizeDisplay = string.Empty;

    [ObservableProperty]
    private bool _isDownloadToExisting = true;

    [ObservableProperty]
    private bool _isDownloadToFolder;

    [ObservableProperty]
    private string? _selectedSourceFolder;

    [ObservableProperty]
    private string? _customFolderPath;

    [ObservableProperty]
    private bool _createBaseModelFolder = true;

    [ObservableProperty]
    private bool _createCategoryFolder = true;

    public ObservableCollection<string> SourceFolders { get; } = [];

    public CivitaiModel? ResolvedModel { get; private set; }

    public CivitaiModelVersion? ResolvedVersion { get; private set; }

    public string Category { get; private set; } = string.Empty;

    public bool CanDownload => HasPreview && ResolvedVersion is not null &&
        ((IsDownloadToExisting && !string.IsNullOrWhiteSpace(SelectedSourceFolder)) ||
         (IsDownloadToFolder && !string.IsNullOrWhiteSpace(CustomFolderPath)));

    public string PreviewPath
    {
        get
        {
            if (!IsDownloadToExisting) return string.Empty;
            return BuildExistingSourceTargetDirectory() ?? string.Empty;
        }
    }

    public bool HasDestinationPreview => IsDownloadToExisting && !string.IsNullOrWhiteSpace(SelectedSourceFolder);

    public DownloadLoraDialogViewModel()
    {
        PreviewName = "Example LoRA";
        PreviewType = "Type: LORA";
        PreviewVersion = "Version: v1.0";
        PreviewBaseModel = "Base Model: SDXL 1.0";
        PreviewCreator = "Creator: ExampleCreator";
        PreviewIds = "Model ID: 1176712    Version ID: 2903152";
        FileName = "example.safetensors";
        FileSizeDisplay = "1.2 GB";
        HasPreview = true;
        SourceFolders.Add(@"C:\Models\Loras");
        SelectedSourceFolder = SourceFolders[0];
    }

    public DownloadLoraDialogViewModel(
        ICivitaiClient? civitaiClient,
        IAppSettingsService? settingsService,
        IDialogService? dialogService,
        IUnifiedLogger? logger,
        ICivitaiApiKeyProvider? apiKeyProvider = null)
    {
        _civitaiClient = civitaiClient;
        _settingsService = settingsService;
        _dialogService = dialogService;
        _logger = logger;
        _apiKeyProvider = apiKeyProvider;
    }

    public async Task InitializeAsync(IReadOnlyList<string> sourceFolders, string? favoriteFolder = null)
    {
        SourceFolders.Clear();
        foreach (var folder in sourceFolders)
        {
            SourceFolders.Add(folder);
        }

        if (SourceFolders.Count > 0)
        {
            // Prefer the user-favorited folder if it's in the configured list,
            // otherwise default to the first source.
            string? preferred = null;
            if (!string.IsNullOrWhiteSpace(favoriteFolder))
            {
                preferred = SourceFolders.FirstOrDefault(
                    f => string.Equals(f, favoriteFolder, StringComparison.OrdinalIgnoreCase));
            }
            SelectedSourceFolder = preferred ?? SourceFolders[0];
        }

        await Task.CompletedTask;
    }

    public string? GetTargetFolder()
    {
        if (IsDownloadToExisting) return BuildExistingSourceTargetDirectory();
        if (IsDownloadToFolder) return CustomFolderPath;
        return null;
    }

    /// <summary>
    /// The "download to existing source folder" branch shared by <see cref="PreviewPath"/> and
    /// <see cref="GetTargetFolder"/> — both used to hand-roll the same base-model/category
    /// combine, which is exactly the folder-toggle drift spec §4.4 exists to kill.
    /// </summary>
    private string? BuildExistingSourceTargetDirectory()
    {
        if (string.IsNullOrWhiteSpace(SelectedSourceFolder)) return null;
        return LoraPathBuilder.BuildTargetDirectory(
            SelectedSourceFolder, ResolvedVersion?.BaseModel, Category,
            includeBaseModel: CreateBaseModelFolder, includeCategory: CreateCategoryFolder);
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        HasPreview = false;
        PreviewImage = null;
        HasPreviewImage = false;
        ResolvedModel = null;
        ResolvedVersion = null;
        Category = string.Empty;
        StatusMessage = null;
        OnDownloadStateChanged();

        if (_civitaiClient is null)
        {
            StatusMessage = "Civitai client is not available.";
            return;
        }

        if (!CivitaiUrlParser.TryResolveIds(UrlText, out var modelId, out var versionId, out var error))
        {
            StatusMessage = error;
            return;
        }

        try
        {
            IsSearching = true;
            StatusMessage = "Searching Civitai...";

            var apiKey = await GetApiKeyAsync();
            CivitaiModel? model = null;
            CivitaiModelVersion? version = null;

            if (modelId.HasValue)
            {
                model = await _civitaiClient.GetModelAsync(modelId.Value, apiKey);
            }
            else if (versionId.HasValue)
            {
                version = await _civitaiClient.GetModelVersionAsync(versionId.Value, apiKey);
                if (version is not null && version.ModelId > 0)
                {
                    model = await _civitaiClient.GetModelAsync(version.ModelId, apiKey);
                }
            }

            if (model is null)
            {
                StatusMessage = "No model found for the supplied link.";
                return;
            }

            if (versionId.HasValue)
            {
                version = model.ModelVersions.FirstOrDefault(v => v.Id == versionId.Value) ?? version;
                if (version is null)
                {
                    StatusMessage = $"Model {model.Id} found, but it has no version with ID {versionId.Value}.";
                    return;
                }
            }
            else
            {
                version ??= model.ModelVersions.FirstOrDefault();
            }

            if (version is null)
            {
                StatusMessage = "Model found, but it has no downloadable versions.";
                return;
            }

            var primaryFile = CivitaiVersionFiles.PickPrimary(version);
            var downloadUrl = primaryFile?.DownloadUrl ?? version.DownloadUrl;
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                StatusMessage = "The selected version does not expose a download URL.";
                return;
            }

            ResolvedModel = model;
            ResolvedVersion = version;
            Category = InferCategoryFromTags(model.Tags) ?? string.Empty;

            PopulatePreview(model, version, primaryFile);
            await LoadPreviewImageAsync(model, version);

            HasPreview = true;
            StatusMessage = null;
            OnDownloadStateChanged();
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Civitai request failed: {ex.StatusCode} {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.Network, "DownloadLora", $"Search failed: {ex.Message}");
            StatusMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var dialogService = _dialogService ?? App.Services?.GetService<IDialogService>();
        if (dialogService is null) return;

        var path = await dialogService.ShowOpenFolderDialogAsync("Select Download Folder");
        if (!string.IsNullOrEmpty(path))
        {
            CustomFolderPath = path;
        }
    }

    private void PopulatePreview(CivitaiModel model, CivitaiModelVersion version, CivitaiModelFile? primaryFile)
    {
        PreviewName = model.Name;
        PreviewType = $"Type: {model.Type}";
        PreviewVersion = $"Version: {version.Name}";
        PreviewBaseModel = $"Base Model: {version.BaseModel}";
        PreviewCreator = $"Creator: {model.Creator?.Username ?? "Unknown"}";
        PreviewIds = $"Model ID: {model.Id}    Version ID: {version.Id}";
        FileName = primaryFile?.Name ?? "unknown.safetensors";
        FileSizeDisplay = FileSizeFormatter.FormatKilobytes(primaryFile?.SizeKB ?? 0);
    }

    private async Task LoadPreviewImageAsync(CivitaiModel model, CivitaiModelVersion version)
    {
        var imageUrl = version.Images.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Url))?.Url
                       ?? model.ModelVersions
                           .SelectMany(v => v.Images)
                           .FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Url))?.Url;

        if (string.IsNullOrEmpty(imageUrl)) return;

        try
        {
            var data = await s_previewHttp.GetByteArrayAsync(imageUrl);
            if (data.Length == 0) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                using var ms = new MemoryStream(data);
                PreviewImage = new Bitmap(ms);
                HasPreviewImage = true;
            });
        }
        catch
        {
            HasPreviewImage = false;
        }
    }

    private Task<string?> GetApiKeyAsync()
    {
        _apiKeyProvider ??= CivitaiApiKeys.Resolve(fallbackSettings: _settingsService);
        return _apiKeyProvider.GetApiKeyAsync();
    }

    /// <summary>
    /// Delegates to the one shared inference helper — see
    /// <see cref="Services.Lora.Sorting.SorterCategoryResolver.InferFolderName"/>.
    /// </summary>
    private static string? InferCategoryFromTags(IReadOnlyList<string> tags)
        => Services.Lora.Sorting.SorterCategoryResolver.InferFolderName(tags);

    private void OnDownloadStateChanged()
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(PreviewPath));
        OnPropertyChanged(nameof(HasDestinationPreview));
    }

    partial void OnIsDownloadToExistingChanged(bool value)
    {
        if (value) IsDownloadToFolder = false;
        OnDownloadStateChanged();
    }

    partial void OnIsDownloadToFolderChanged(bool value)
    {
        if (value) IsDownloadToExisting = false;
        OnDownloadStateChanged();
    }

    partial void OnSelectedSourceFolderChanged(string? value) => OnDownloadStateChanged();

    partial void OnCustomFolderPathChanged(string? value) => OnDownloadStateChanged();

    partial void OnCreateBaseModelFolderChanged(bool value) => OnDownloadStateChanged();

    partial void OnCreateCategoryFolderChanged(bool value) => OnDownloadStateChanged();
}
