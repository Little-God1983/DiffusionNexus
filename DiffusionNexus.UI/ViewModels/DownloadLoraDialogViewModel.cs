using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
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
            if (!IsDownloadToExisting || string.IsNullOrWhiteSpace(SelectedSourceFolder))
                return string.Empty;

            var path = SelectedSourceFolder;
            if (CreateBaseModelFolder && !string.IsNullOrWhiteSpace(ResolvedVersion?.BaseModel))
                path = Path.Combine(path, ResolvedVersion.BaseModel);
            if (CreateCategoryFolder && !string.IsNullOrWhiteSpace(Category))
                path = Path.Combine(path, Category);
            return path;
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
        IUnifiedLogger? logger)
    {
        _civitaiClient = civitaiClient;
        _settingsService = settingsService;
        _dialogService = dialogService;
        _logger = logger;
    }

    public async Task InitializeAsync(IReadOnlyList<string> sourceFolders)
    {
        SourceFolders.Clear();
        foreach (var folder in sourceFolders)
        {
            SourceFolders.Add(folder);
        }

        if (SourceFolders.Count > 0)
        {
            SelectedSourceFolder = SourceFolders[0];
        }

        await Task.CompletedTask;
    }

    public string? GetTargetFolder()
    {
        if (IsDownloadToExisting && !string.IsNullOrWhiteSpace(SelectedSourceFolder))
        {
            var path = SelectedSourceFolder;
            if (CreateBaseModelFolder && !string.IsNullOrWhiteSpace(ResolvedVersion?.BaseModel))
                path = Path.Combine(path, ResolvedVersion.BaseModel);
            if (CreateCategoryFolder && !string.IsNullOrWhiteSpace(Category))
                path = Path.Combine(path, Category);
            return path;
        }

        if (IsDownloadToFolder) return CustomFolderPath;
        return null;
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

            var primaryFile = version.Files.FirstOrDefault(f => f.Primary == true)
                              ?? version.Files.FirstOrDefault();
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
        FileSizeDisplay = FormatFileSize(primaryFile?.SizeKB ?? 0);
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
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var data = await http.GetByteArrayAsync(imageUrl);
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

    private async Task<string?> GetApiKeyAsync()
    {
        if (App.Services is not null)
        {
            using var scope = App.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var settingsService = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            return await settingsService.GetCivitaiApiKeyAsync();
        }

        return _settingsService is not null
            ? await _settingsService.GetCivitaiApiKeyAsync()
            : null;
    }

    private static string? InferCategoryFromTags(IReadOnlyList<string> tags)
    {
        foreach (var tagName in tags)
        {
            if (string.IsNullOrWhiteSpace(tagName)) continue;

            var normalized = tagName.Replace(" ", "_").Trim();
            if (Enum.TryParse<Domain.Enums.CivitaiCategory>(normalized, ignoreCase: true, out var category)
                && category != Domain.Enums.CivitaiCategory.Unknown)
            {
                return category switch
                {
                    Domain.Enums.CivitaiCategory.BaseModel => "Base Model",
                    _ => category.ToString()
                };
            }
        }

        return null;
    }

    private static string FormatFileSize(double sizeKb)
    {
        return sizeKb switch
        {
            >= 1_048_576 => $"{sizeKb / 1_048_576:F1} GB",
            >= 1_024 => $"{sizeKb / 1_024:F1} MB",
            > 0 => $"{sizeKb:F0} KB",
            _ => "Unknown"
        };
    }

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
