using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the LoRA Dataset Helper module providing dataset management,
/// image editing, captioning, and auto scale/crop functionality.
/// </summary>
public partial class LoraDatasetHelperViewModel : ViewModelBase, IDialogServiceAware
{
    private readonly IAppSettingsService _settingsService;

    /// <summary>
    /// Gets or sets the dialog service for showing dialogs.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    /// <summary>
    /// Indicates whether the dataset storage path is configured.
    /// </summary>
    [ObservableProperty]
    private bool _isStorageConfigured;

    /// <summary>
    /// Status message to display to the user.
    /// </summary>
    [ObservableProperty]
    private string? _statusMessage;

    public LoraDatasetHelperViewModel(IAppSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public LoraDatasetHelperViewModel()
    {
        _settingsService = null!;
        IsStorageConfigured = true;
    }

    /// <summary>
    /// Checks if the dataset storage path is configured.
    /// </summary>
    [RelayCommand]
    private async Task CheckStorageConfigurationAsync()
    {
        if (_settingsService is null) return;

        var settings = await _settingsService.GetSettingsAsync();
        IsStorageConfigured = !string.IsNullOrWhiteSpace(settings.DatasetStoragePath)
                              && Directory.Exists(settings.DatasetStoragePath);
    }

    /// <summary>
    /// Creates a new dataset folder.
    /// </summary>
    [RelayCommand]
    private async Task CreateDatasetAsync()
    {
        if (DialogService is null || _settingsService is null) return;

        // Check if storage path is configured
        var settings = await _settingsService.GetSettingsAsync();

        if (string.IsNullOrWhiteSpace(settings.DatasetStoragePath))
        {
            StatusMessage = "Please configure the Dataset Storage Path in Settings first.";
            IsStorageConfigured = false;
            return;
        }

        if (!Directory.Exists(settings.DatasetStoragePath))
        {
            StatusMessage = "The configured Dataset Storage Path does not exist. Please update it in Settings.";
            IsStorageConfigured = false;
            return;
        }

        IsStorageConfigured = true;

        // Ask for dataset name
        var datasetName = await DialogService.ShowInputAsync(
            "New Dataset",
            "Enter a name for the new dataset:",
            null);

        if (string.IsNullOrWhiteSpace(datasetName))
        {
            return;
        }

        // Sanitize the folder name
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitizedName = string.Concat(datasetName.Where(c => !invalidChars.Contains(c)));

        if (string.IsNullOrWhiteSpace(sanitizedName))
        {
            StatusMessage = "Invalid dataset name. Please use valid characters.";
            return;
        }

        // Create the folder
        var datasetPath = Path.Combine(settings.DatasetStoragePath, sanitizedName);

        if (Directory.Exists(datasetPath))
        {
            StatusMessage = $"A dataset named '{sanitizedName}' already exists.";
            return;
        }

        try
        {
            Directory.CreateDirectory(datasetPath);
            StatusMessage = $"Dataset '{sanitizedName}' created successfully.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to create dataset: {ex.Message}";
        }
    }
}
