using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the LoRA version download dialog.
/// Lets the user choose between downloading to an existing source folder or browsing for a custom folder.
/// </summary>
public partial class DownloadLoraVersionDialogViewModel : ObservableObject
{
    private readonly IDialogService? _dialogService;

    #region Observable Properties

    /// <summary>
    /// Whether the "Download to existing folder" option is selected.
    /// </summary>
    [ObservableProperty]
    private bool _isDownloadToExisting = true;

    /// <summary>
    /// Whether the "Download to folder" option is selected.
    /// </summary>
    [ObservableProperty]
    private bool _isDownloadToFolder;

    /// <summary>
    /// The currently selected source folder path.
    /// </summary>
    [ObservableProperty]
    private string? _selectedSourceFolder;

    /// <summary>
    /// The custom folder path chosen by the user.
    /// </summary>
    [ObservableProperty]
    private string? _customFolderPath;

    /// <summary>
    /// Display name of the model being downloaded.
    /// </summary>
    [ObservableProperty]
    private string _modelName = string.Empty;

    /// <summary>
    /// Version name being downloaded.
    /// </summary>
    [ObservableProperty]
    private string _versionName = string.Empty;

    /// <summary>
    /// File name to be downloaded.
    /// </summary>
    [ObservableProperty]
    private string _fileName = string.Empty;

    /// <summary>
    /// Human-readable file size.
    /// </summary>
    [ObservableProperty]
    private string _fileSizeDisplay = string.Empty;

    /// <summary>
    /// Base model string (e.g., "SDXL 1.0").
    /// </summary>
    [ObservableProperty]
    private string _baseModel = string.Empty;

    /// <summary>
    /// Inferred category of the model (e.g., "Character", "Style", "Concept").
    /// </summary>
    [ObservableProperty]
    private string _category = string.Empty;

    /// <summary>
    /// Whether to create a subfolder named after the base model inside the source folder.
    /// </summary>
    [ObservableProperty]
    private bool _createBaseModelFolder = true;

    /// <summary>
    /// Whether to create a subfolder named after the category inside the base model folder.
    /// </summary>
    [ObservableProperty]
    private bool _createCategoryFolder = true;

    #endregion

    #region Collections

    /// <summary>
    /// Available LoRA source folders from settings.
    /// </summary>
    public ObservableCollection<string> SourceFolders { get; } = [];

    #endregion

    #region Computed Properties

    /// <summary>
    /// Whether the Download button should be enabled.
    /// </summary>
    public bool CanDownload =>
        (IsDownloadToExisting && !string.IsNullOrWhiteSpace(SelectedSourceFolder)) ||
        (IsDownloadToFolder && !string.IsNullOrWhiteSpace(CustomFolderPath));

    /// <summary>
    /// Preview path showing the resolved download destination with optional subfolders.
    /// Only meaningful when "Download to existing source folder" is selected.
    /// </summary>
    public string PreviewPath
    {
        get
        {
            if (!IsDownloadToExisting || string.IsNullOrWhiteSpace(SelectedSourceFolder))
                return string.Empty;

            var path = SelectedSourceFolder;
            if (CreateBaseModelFolder && !string.IsNullOrWhiteSpace(BaseModel))
                path = Path.Combine(path, BaseModel);
            if (CreateCategoryFolder && !string.IsNullOrWhiteSpace(Category))
                path = Path.Combine(path, Category);
            return path;
        }
    }

    /// <summary>
    /// Whether the preview path should be visible (existing folder selected with a valid path).
    /// </summary>
    public bool HasPreviewPath => IsDownloadToExisting && !string.IsNullOrWhiteSpace(SelectedSourceFolder);

    #endregion

    #region Constructors

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public DownloadLoraVersionDialogViewModel()
    {
        ModelName = "Example Model";
        VersionName = "v1.0";
        FileName = "example_v1.safetensors";
        FileSizeDisplay = "1.2 GB";
        BaseModel = "SDXL 1.0";
        Category = "Character";
        SourceFolders.Add(@"C:\Models\Loras");
        SourceFolders.Add(@"D:\AI\Models\Loras");
        SelectedSourceFolder = SourceFolders[0];
    }

    /// <summary>
    /// Runtime constructor.
    /// </summary>
    public DownloadLoraVersionDialogViewModel(IDialogService? dialogService)
    {
        _dialogService = dialogService;
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Populates the dialog with version information and source folders.
    /// </summary>
    public void Initialize(
        string modelName,
        CivitaiModelVersion civitaiVersion,
        IReadOnlyList<string> sourceFolders,
        string? category = null)
    {
        ModelName = modelName;
        VersionName = civitaiVersion.Name;
        BaseModel = civitaiVersion.BaseModel;
        Category = category ?? string.Empty;

        var primaryFile = civitaiVersion.Files.FirstOrDefault(f => f.Primary == true)
                          ?? civitaiVersion.Files.FirstOrDefault();
        FileName = primaryFile?.Name ?? "unknown.safetensors";
        FileSizeDisplay = FormatFileSize(primaryFile?.SizeKB ?? 0);

        SourceFolders.Clear();
        foreach (var folder in sourceFolders)
        {
            SourceFolders.Add(folder);
        }

        if (SourceFolders.Count > 0)
        {
            SelectedSourceFolder = SourceFolders[0];
        }
    }

    #endregion

    #region Commands

    /// <summary>
    /// Browse for a custom folder.
    /// </summary>
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

    #endregion

    #region Change Handlers

    partial void OnIsDownloadToExistingChanged(bool value)
    {
        if (value) IsDownloadToFolder = false;
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(PreviewPath));
        OnPropertyChanged(nameof(HasPreviewPath));
    }

    partial void OnIsDownloadToFolderChanged(bool value)
    {
        if (value) IsDownloadToExisting = false;
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(PreviewPath));
        OnPropertyChanged(nameof(HasPreviewPath));
    }

    partial void OnSelectedSourceFolderChanged(string? value)
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(PreviewPath));
        OnPropertyChanged(nameof(HasPreviewPath));
    }

    partial void OnCustomFolderPathChanged(string? value) => OnPropertyChanged(nameof(CanDownload));

    partial void OnCreateBaseModelFolderChanged(bool value) => OnPropertyChanged(nameof(PreviewPath));

    partial void OnCreateCategoryFolderChanged(bool value) => OnPropertyChanged(nameof(PreviewPath));

    #endregion

    #region Helpers

    /// <summary>
    /// Gets the resolved target folder path based on the current selection,
    /// including optional base-model and category subfolders.
    /// </summary>
    public string? GetTargetFolder()
    {
        if (IsDownloadToExisting && !string.IsNullOrWhiteSpace(SelectedSourceFolder))
        {
            var path = SelectedSourceFolder;
            if (CreateBaseModelFolder && !string.IsNullOrWhiteSpace(BaseModel))
                path = Path.Combine(path, BaseModel);
            if (CreateCategoryFolder && !string.IsNullOrWhiteSpace(Category))
                path = Path.Combine(path, Category);
            return path;
        }

        if (IsDownloadToFolder) return CustomFolderPath;
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

    #endregion
}

/// <summary>
/// Result returned from the download LoRA version dialog.
/// </summary>
public sealed record DownloadLoraVersionResult
{
    /// <summary>
    /// Whether the user confirmed the download.
    /// </summary>
    public bool Confirmed { get; init; }

    /// <summary>
    /// The resolved target folder path.
    /// </summary>
    public string? TargetFolder { get; init; }

    /// <summary>
    /// Creates a cancelled result.
    /// </summary>
    public static DownloadLoraVersionResult Cancelled() => new() { Confirmed = false };
}
