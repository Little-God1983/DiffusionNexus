using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;
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
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

    private bool _isStorageConfigured;
    private string? _statusMessage;
    private bool _isViewingDataset;
    private DatasetCardViewModel? _activeDataset;
    private bool _isLoading;
    private bool _hasUnsavedChanges;
    private int _selectedTabIndex;
    private DatasetCategoryViewModel? _selectedCategory;

    /// <summary>
    /// Gets or sets the dialog service for showing dialogs.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    #region Observable Properties

    /// <summary>
    /// Indicates whether the dataset storage path is configured.
    /// </summary>
    public bool IsStorageConfigured
    {
        get => _isStorageConfigured;
        set => SetProperty(ref _isStorageConfigured, value);
    }

    /// <summary>
    /// Status message to display to the user.
    /// </summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Whether we are currently viewing a dataset's contents (vs overview).
    /// </summary>
    public bool IsViewingDataset
    {
        get => _isViewingDataset;
        set => SetProperty(ref _isViewingDataset, value);
    }

    /// <summary>
    /// The currently selected/active dataset.
    /// </summary>
    public DatasetCardViewModel? ActiveDataset
    {
        get => _activeDataset;
        set => SetProperty(ref _activeDataset, value);
    }

    /// <summary>
    /// Whether images are currently loading.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    /// <summary>
    /// Whether there are unsaved caption changes.
    /// </summary>
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        set => SetProperty(ref _hasUnsavedChanges, value);
    }

    /// <summary>
    /// Selected tab index for programmatic tab switching.
    /// </summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    /// <summary>
    /// Selected category for the active dataset.
    /// </summary>
    public DatasetCategoryViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value) && ActiveDataset is not null)
            {
                ActiveDataset.CategoryId = value?.Id;
                ActiveDataset.CategoryName = value?.Name;
                ActiveDataset.SaveMetadata();
                StatusMessage = value is not null 
                    ? $"Category set to '{value.Name}'" 
                    : "Category cleared";
            }
        }
    }

    #endregion

    #region Collections

    /// <summary>
    /// Collection of dataset cards (folders in the storage path).
    /// </summary>
    public ObservableCollection<DatasetCardViewModel> Datasets { get; } = [];

    /// <summary>
    /// Collection of images in the active dataset.
    /// </summary>
    public ObservableCollection<DatasetImageViewModel> DatasetImages { get; } = [];

    /// <summary>
    /// Available dataset categories.
    /// </summary>
    public ObservableCollection<DatasetCategoryViewModel> AvailableCategories { get; } = [];

    #endregion

    #region Commands

    public IAsyncRelayCommand CheckStorageConfigurationCommand { get; }
    public IAsyncRelayCommand LoadDatasetsCommand { get; }
    public IAsyncRelayCommand<DatasetCardViewModel?> OpenDatasetCommand { get; }
    public IAsyncRelayCommand GoToOverviewCommand { get; }
    public IAsyncRelayCommand CreateDatasetCommand { get; }
    public IAsyncRelayCommand AddImagesCommand { get; }
    public IRelayCommand SaveAllCaptionsCommand { get; }
    public IAsyncRelayCommand<DatasetCardViewModel?> DeleteDatasetCommand { get; }
    public IRelayCommand OpenContainingFolderCommand { get; }
    public IRelayCommand<DatasetImageViewModel?> SendToImageEditCommand { get; }
    public IRelayCommand ExportDatasetCommand { get; }

    #endregion

    #region Constructors

    public LoraDatasetHelperViewModel(IAppSettingsService settingsService)
    {
        _settingsService = settingsService;
        
        // Initialize commands
        CheckStorageConfigurationCommand = new AsyncRelayCommand(CheckStorageConfigurationAsync);
        LoadDatasetsCommand = new AsyncRelayCommand(LoadDatasetsAsync);
        OpenDatasetCommand = new AsyncRelayCommand<DatasetCardViewModel?>(OpenDatasetAsync);
        GoToOverviewCommand = new AsyncRelayCommand(GoToOverviewAsync);
        CreateDatasetCommand = new AsyncRelayCommand(CreateDatasetAsync);
        AddImagesCommand = new AsyncRelayCommand(AddImagesAsync);
        SaveAllCaptionsCommand = new RelayCommand(SaveAllCaptions);
        DeleteDatasetCommand = new AsyncRelayCommand<DatasetCardViewModel?>(DeleteDatasetAsync);
        OpenContainingFolderCommand = new RelayCommand(OpenContainingFolder);
        SendToImageEditCommand = new RelayCommand<DatasetImageViewModel?>(SendToImageEdit);
        ExportDatasetCommand = new RelayCommand(ExportDataset);
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public LoraDatasetHelperViewModel() : this(null!)
    {
        IsStorageConfigured = true;
        
        // Design-time demo data
        Datasets.Add(new DatasetCardViewModel { Name = "Character Training", ImageCount = 45 });
        Datasets.Add(new DatasetCardViewModel { Name = "Style Reference", ImageCount = 23 });
        Datasets.Add(new DatasetCardViewModel { Name = "Background Scenes", ImageCount = 12 });

        // Design-time categories
        AvailableCategories.Add(new DatasetCategoryViewModel { Id = 1, Name = "Character" });
        AvailableCategories.Add(new DatasetCategoryViewModel { Id = 2, Name = "Style" });
        AvailableCategories.Add(new DatasetCategoryViewModel { Id = 3, Name = "Concept" });
    }

    #endregion

    #region Command Implementations

    private async Task CheckStorageConfigurationAsync()
    {
        if (_settingsService is null) return;

        var settings = await _settingsService.GetSettingsAsync();
        IsStorageConfigured = !string.IsNullOrWhiteSpace(settings.DatasetStoragePath)
                              && Directory.Exists(settings.DatasetStoragePath);

        // Load categories
        await LoadCategoriesAsync(settings);

        if (IsStorageConfigured)
        {
            await LoadDatasetsAsync();
        }
    }

    private async Task LoadCategoriesAsync(AppSettings? settings = null)
    {
        if (_settingsService is null) return;

        settings ??= await _settingsService.GetSettingsAsync();

        AvailableCategories.Clear();
        foreach (var category in settings.DatasetCategories.OrderBy(c => c.Order))
        {
            AvailableCategories.Add(new DatasetCategoryViewModel
            {
                Id = category.Id,
                Name = category.Name,
                Description = category.Description,
                IsDefault = category.IsDefault
            });
        }
    }

    private async Task LoadDatasetsAsync()
    {
        if (_settingsService is null) return;

        IsLoading = true;
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            if (string.IsNullOrWhiteSpace(settings.DatasetStoragePath) || !Directory.Exists(settings.DatasetStoragePath))
            {
                IsStorageConfigured = false;
                return;
            }

            IsStorageConfigured = true;
            Datasets.Clear();

            var folders = Directory.GetDirectories(settings.DatasetStoragePath);
            foreach (var folder in folders.OrderBy(f => Path.GetFileName(f)))
            {
                var card = DatasetCardViewModel.FromFolder(folder);
                Datasets.Add(card);
            }

            StatusMessage = Datasets.Count == 0 ? null : $"Found {Datasets.Count} datasets";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading datasets: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task OpenDatasetAsync(DatasetCardViewModel? dataset)
    {
        if (dataset is null) return;

        IsLoading = true;
        try
        {
            ActiveDataset = dataset;
            DatasetImages.Clear();

            if (!Directory.Exists(dataset.FolderPath))
            {
                StatusMessage = "Dataset folder no longer exists.";
                return;
            }

            var imageFiles = Directory.EnumerateFiles(dataset.FolderPath)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f)
                .ToList();

            foreach (var imagePath in imageFiles)
            {
                var imageVm = DatasetImageViewModel.FromFile(
                    imagePath,
                    OnImageDeleteRequested,
                    OnCaptionChanged);
                DatasetImages.Add(imageVm);
            }

            // Set selected category based on dataset metadata
            _selectedCategory = AvailableCategories.FirstOrDefault(c => c.Id == dataset.CategoryId);
            OnPropertyChanged(nameof(SelectedCategory));

            IsViewingDataset = true;
            HasUnsavedChanges = false;
            StatusMessage = $"Loaded {DatasetImages.Count} images";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading dataset: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task GoToOverviewAsync()
    {
        // Check for unsaved changes
        if (HasUnsavedChanges && DialogService is not null)
        {
            var save = await DialogService.ShowConfirmAsync(
                "Unsaved Changes",
                "You have unsaved caption changes. Save them before leaving?");
            
            if (save)
            {
                SaveAllCaptions();
            }
        }

        ActiveDataset = null;
        DatasetImages.Clear();
        IsViewingDataset = false;
        HasUnsavedChanges = false;

        // Refresh the datasets list
        await LoadDatasetsAsync();
    }

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
            
            // Refresh the list
            await LoadDatasetsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to create dataset: {ex.Message}";
        }
    }

    private async Task AddImagesAsync()
    {
        if (DialogService is null || ActiveDataset is null) return;

        // Show drag-drop file picker dialog
        var files = await DialogService.ShowFileDropDialogAsync($"Add Images to: {ActiveDataset.Name}");
        if (files is null || files.Count == 0) return;

        IsLoading = true;
        try
        {
            var copied = 0;
            var skipped = 0;

            foreach (var sourceFile in files)
            {
                var fileName = Path.GetFileName(sourceFile);
                var destPath = Path.Combine(ActiveDataset.FolderPath, fileName);

                if (!File.Exists(destPath))
                {
                    File.Copy(sourceFile, destPath);
                    copied++;
                }
                else
                {
                    skipped++;
                }
            }

            StatusMessage = skipped > 0 
                ? $"Added {copied} files, skipped {skipped} duplicates"
                : $"Added {copied} files to dataset";
            
            // Reload the dataset
            await OpenDatasetAsync(ActiveDataset);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error adding files: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SaveAllCaptions()
    {
        var saved = 0;
        foreach (var image in DatasetImages.Where(i => i.HasUnsavedChanges))
        {
            image.SaveCaptionCommand.Execute(null);
            saved++;
        }

        HasUnsavedChanges = false;
        StatusMessage = saved > 0 ? $"Saved {saved} captions" : "No changes to save";
    }

    private async Task DeleteDatasetAsync(DatasetCardViewModel? dataset)
    {
        if (dataset is null || DialogService is null) return;

        var confirm = await DialogService.ShowConfirmAsync(
            "Delete Dataset",
            $"Are you sure you want to delete '{dataset.Name}'? This will permanently delete all images and captions in this dataset.");

        if (!confirm) return;

        try
        {
            Directory.Delete(dataset.FolderPath, recursive: true);
            Datasets.Remove(dataset);
            StatusMessage = $"Deleted dataset '{dataset.Name}'";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error deleting dataset: {ex.Message}";
        }
    }

    #endregion

    #region Private Methods

    private async void OnImageDeleteRequested(DatasetImageViewModel image)
    {
        if (DialogService is null) return;

        var confirm = await DialogService.ShowConfirmAsync(
            "Delete Image",
            $"Delete '{image.FullFileName}' and its caption?");

        if (!confirm) return;

        try
        {
            // Delete image file
            if (File.Exists(image.ImagePath))
            {
                File.Delete(image.ImagePath);
            }

            // Delete caption file
            if (File.Exists(image.CaptionFilePath))
            {
                File.Delete(image.CaptionFilePath);
            }

            DatasetImages.Remove(image);
            
            // Update the dataset card count
            if (ActiveDataset is not null)
            {
                ActiveDataset.ImageCount = DatasetImages.Count;
            }

            StatusMessage = $"Deleted '{image.FullFileName}'";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error deleting image: {ex.Message}";
        }
    }

    private void OnCaptionChanged(DatasetImageViewModel image)
    {
        HasUnsavedChanges = DatasetImages.Any(i => i.HasUnsavedChanges);
    }

    private void OpenContainingFolder()
    {
        if (ActiveDataset is null || !Directory.Exists(ActiveDataset.FolderPath)) return;

        try
        {
            // Open folder in file explorer
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ActiveDataset.FolderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error opening folder: {ex.Message}";
        }
    }

    private void SendToImageEdit(DatasetImageViewModel? image)
    {
        if (image is null) return;

        ImageEditor.LoadImage(image.ImagePath);
        SelectedTabIndex = 1; // Switch to Image Edit tab
        StatusMessage = $"Editing: {image.FullFileName}";
    }

    private void ExportDataset()
    {
        // TODO: Implement dataset export functionality
        // - Export as zip with images and captions
        // - Export in kohya_ss format
        // - Export in other training formats
        StatusMessage = "Export Dataset: Not yet implemented";
    }

    #endregion

    /// <summary>
    /// Gets the Image Editor ViewModel.
    /// </summary>
    public ImageEditorViewModel ImageEditor { get; } = new();

    /// <summary>
    /// Refreshes the active dataset to reflect any file changes.
    /// </summary>
    public async Task RefreshActiveDatasetAsync()
    {
        if (ActiveDataset is not null)
        {
            // Update the dataset card with fresh image count
            ActiveDataset.ImageCount = Directory.Exists(ActiveDataset.FolderPath)
                ? Directory.EnumerateFiles(ActiveDataset.FolderPath)
                    .Count(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                : 0;

            // Reload images if viewing the dataset
            if (IsViewingDataset)
            {
                await OpenDatasetAsync(ActiveDataset);
            }
        }
    }
}
