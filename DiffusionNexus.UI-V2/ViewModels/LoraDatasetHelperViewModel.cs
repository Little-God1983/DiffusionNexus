using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the LoRA Dataset Helper module providing dataset management,
/// image/video editing, captioning, and auto scale/crop functionality.
/// </summary>
public partial class LoraDatasetHelperViewModel : ViewModelBase, IDialogServiceAware
{
    private readonly IAppSettingsService _settingsService;
    private readonly IVideoThumbnailService? _videoThumbnailService;
    
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".webm", ".avi", ".mkv", ".wmv", ".flv", ".m4v"];
    private static readonly string[] MediaExtensions = [..ImageExtensions, ..VideoExtensions];

    private bool _isStorageConfigured;
    private string? _statusMessage;
    private bool _isViewingDataset;
    private DatasetCardViewModel? _activeDataset;
    private bool _isLoading;
    private bool _hasUnsavedChanges;
    private int _selectedTabIndex;
    private DatasetCategoryViewModel? _selectedCategory;
    private bool _flattenVersions;
    private bool _isFileDialogOpen;

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
        set
        {
            if (SetProperty(ref _isViewingDataset, value))
            {
                OnPropertyChanged(nameof(HasNoImages));
            }
        }
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

    /// <summary>
    /// Whether to flatten version folders in the overview.
    /// When true: shows individual cards for each version (V1, V2, V3).
    /// When false: shows one card per dataset with version count badge.
    /// </summary>
    public bool FlattenVersions
    {
        get => _flattenVersions;
        set
        {
            if (SetProperty(ref _flattenVersions, value))
            {
                // Rebuild grouped datasets with new view mode
                _ = LoadDatasetsAsync();
            }
        }
    }

    /// <summary>
    /// Indicates whether a file dialog is currently open.
    /// Used to disable drag-drop on the base view while the dialog is open.
    /// </summary>
    public bool IsFileDialogOpen
    {
        get => _isFileDialogOpen;
        set => SetProperty(ref _isFileDialogOpen, value);
    }

    /// <summary>
    /// Currently selected version number for the active dataset.
    /// Changing this reloads the dataset with the selected version.
    /// </summary>
    public int SelectedVersion
    {
        get => ActiveDataset?.CurrentVersion ?? 1;
        set
        {
            if (ActiveDataset is not null && ActiveDataset.CurrentVersion != value)
            {
                _ = SwitchVersionAsync(value);
            }
        }
    }

    /// <summary>
    /// Whether the active dataset has no media files (empty state).
    /// Used to show the drag-and-drop zone when a dataset is newly created or empty.
    /// </summary>
    public bool HasNoImages => IsViewingDataset && DatasetImages.Count == 0;

    #endregion

    #region Collections

    /// <summary>
    /// Collection of dataset cards (folders in the storage path).
    /// </summary>
    public ObservableCollection<DatasetCardViewModel> Datasets { get; } = [];

    /// <summary>
    /// Collection of datasets grouped by category for the overview display.
    /// Each group represents a category with its datasets, shown as horizontal sections.
    /// </summary>
    public ObservableCollection<DatasetGroupViewModel> GroupedDatasets { get; } = [];

    /// <summary>
    /// Collection of images in the active dataset.
    /// </summary>
    public ObservableCollection<DatasetImageViewModel> DatasetImages { get; } = [];

    /// <summary>
    /// Available dataset categories.
    /// </summary>
    public ObservableCollection<DatasetCategoryViewModel> AvailableCategories { get; } = [];

    /// <summary>
    /// Available versions for the active dataset (for version dropdown).
    /// </summary>
    public ObservableCollection<int> AvailableVersions { get; } = [];

    #endregion

    #region Commands

    public IAsyncRelayCommand CheckStorageConfigurationCommand { get; }
    public IAsyncRelayCommand LoadDatasetsCommand { get; }
    public IAsyncRelayCommand<DatasetCardViewModel?> OpenDatasetCommand { get; }
    public IAsyncRelayCommand GoToOverviewCommand { get; }
    public IAsyncRelayCommand CreateDatasetCommand { get; }
    public IAsyncRelayCommand AddImagesCommand { get; }
    public IAsyncRelayCommand IncrementVersionCommand { get; }
    public IRelayCommand SaveAllCaptionsCommand { get; }
    public IAsyncRelayCommand<DatasetCardViewModel?> DeleteDatasetCommand { get; }
    public IRelayCommand OpenContainingFolderCommand { get; }
    public IRelayCommand<DatasetImageViewModel?> SendToImageEditCommand { get; }
    public IRelayCommand ExportDatasetCommand { get; }

    #endregion

    #region Constructors

    public LoraDatasetHelperViewModel(IAppSettingsService settingsService, IVideoThumbnailService? videoThumbnailService = null)
    {
        _settingsService = settingsService;
        _videoThumbnailService = videoThumbnailService;
        
        // Subscribe to DatasetImages collection changes to update HasNoImages
        DatasetImages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoImages));
        
        // Initialize commands
        CheckStorageConfigurationCommand = new AsyncRelayCommand(CheckStorageConfigurationAsync);
        LoadDatasetsCommand = new AsyncRelayCommand(LoadDatasetsAsync);
        OpenDatasetCommand = new AsyncRelayCommand<DatasetCardViewModel?>(OpenDatasetAsync);
        GoToOverviewCommand = new AsyncRelayCommand(GoToOverviewAsync);
        CreateDatasetCommand = new AsyncRelayCommand(CreateDatasetAsync);
        AddImagesCommand = new AsyncRelayCommand(AddImagesAsync);
        IncrementVersionCommand = new AsyncRelayCommand(IncrementVersionAsync);
        SaveAllCaptionsCommand = new RelayCommand(SaveAllCaptions);
        DeleteDatasetCommand = new AsyncRelayCommand<DatasetCardViewModel?>(DeleteDatasetAsync);
        OpenContainingFolderCommand = new RelayCommand(OpenContainingFolder);
        SendToImageEditCommand = new RelayCommand<DatasetImageViewModel?>(SendToImageEdit);
        ExportDatasetCommand = new RelayCommand(ExportDataset);
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public LoraDatasetHelperViewModel() : this(null!, null)
    {
        IsStorageConfigured = true;
        
        // Design-time demo data
        Datasets.Add(new DatasetCardViewModel { Name = "Character Training", ImageCount = 45, CategoryId = 1 });
        Datasets.Add(new DatasetCardViewModel { Name = "Style Reference", ImageCount = 23, CategoryId = 2 });
        Datasets.Add(new DatasetCardViewModel { Name = "Background Scenes", ImageCount = 12 });

        // Design-time categories
        AvailableCategories.Add(new DatasetCategoryViewModel { Id = 1, Name = "Character" });
        AvailableCategories.Add(new DatasetCategoryViewModel { Id = 2, Name = "Style" });
        AvailableCategories.Add(new DatasetCategoryViewModel { Id = 3, Name = "Concept" });

        // Design-time grouped datasets
        var characterGroup = DatasetGroupViewModel.FromCategory(AvailableCategories[0], 0);
        characterGroup.Datasets.Add(Datasets[0]);
        GroupedDatasets.Add(characterGroup);

        var styleGroup = DatasetGroupViewModel.FromCategory(AvailableCategories[1], 1);
        styleGroup.Datasets.Add(Datasets[1]);
        GroupedDatasets.Add(styleGroup);

        var uncategorized = DatasetGroupViewModel.CreateUncategorized();
        uncategorized.Datasets.Add(Datasets[2]);
        GroupedDatasets.Add(uncategorized);
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
            GroupedDatasets.Clear();

            var folders = Directory.GetDirectories(settings.DatasetStoragePath);
            foreach (var folder in folders.OrderBy(f => Path.GetFileName(f)))
            {
                var card = DatasetCardViewModel.FromFolder(folder);
                Datasets.Add(card);
            }

            // Build category groups in order, then add uncategorized at the end
            var sortOrder = 0;
            foreach (var category in AvailableCategories)
            {
                var group = DatasetGroupViewModel.FromCategory(category, sortOrder++);
                var categoryDatasets = Datasets.Where(d => d.CategoryId == category.Id);
                
                foreach (var dataset in categoryDatasets)
                {
                    AddDatasetCardsToGroup(group, dataset);
                }
                
                // Only add groups that have datasets
                if (group.HasDatasets)
                {
                    GroupedDatasets.Add(group);
                }
            }

            // Add uncategorized datasets at the end
            var uncategorizedDatasets = Datasets.Where(d => d.CategoryId is null).ToList();
            if (uncategorizedDatasets.Count > 0)
            {
                var uncategorized = DatasetGroupViewModel.CreateUncategorized(sortOrder);
                foreach (var dataset in uncategorizedDatasets)
                {
                    AddDatasetCardsToGroup(uncategorized, dataset);
                }
                GroupedDatasets.Add(uncategorized);
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

    /// <summary>
    /// Adds dataset cards to a group, handling flattened view mode.
    /// </summary>
    private void AddDatasetCardsToGroup(DatasetGroupViewModel group, DatasetCardViewModel dataset)
    {
        if (_flattenVersions && dataset.IsVersionedStructure && dataset.TotalVersions > 1)
        {
            // Flattened view: add one card per version
            foreach (var version in dataset.GetAllVersionNumbers())
            {
                var versionCard = dataset.CreateVersionCard(version);
                group.Datasets.Add(versionCard);
            }
        }
        else
        {
            // Collapsed view: add single card (shows version count badge if multiple versions)
            group.Datasets.Add(dataset);
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
            
            // Populate available versions for the dropdown
            AvailableVersions.Clear();
            if (dataset.IsVersionedStructure)
            {
                foreach (var version in dataset.GetAllVersionNumbers())
                {
                    AvailableVersions.Add(version);
                }
            }
            else
            {
                AvailableVersions.Add(1);
            }
            OnPropertyChanged(nameof(SelectedVersion));

            // Use the current version folder path (versioned or legacy)
            var mediaFolderPath = dataset.CurrentVersionFolderPath;
            if (!Directory.Exists(mediaFolderPath))
            {
                StatusMessage = "Dataset folder no longer exists.";
                return;
            }

            // Load all media files (images and videos)
            var mediaFiles = Directory.EnumerateFiles(mediaFolderPath)
                .Where(f => MediaExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f)
                .ToList();

            foreach (var mediaPath in mediaFiles)
            {
                var mediaVm = DatasetImageViewModel.FromFile(
                    mediaPath,
                    OnImageDeleteRequested,
                    OnCaptionChanged);
                
                // Generate thumbnail for video files if service is available
                if (mediaVm.IsVideo && _videoThumbnailService is not null)
                {
                    await GenerateVideoThumbnailAsync(mediaVm);
                }
                
                DatasetImages.Add(mediaVm);
            }

            // Set selected category based on dataset metadata
            _selectedCategory = AvailableCategories.FirstOrDefault(c => c.Id == dataset.CategoryId);
            OnPropertyChanged(nameof(SelectedCategory));

            IsViewingDataset = true;
            HasUnsavedChanges = false;
            
            var imageCount = DatasetImages.Count(m => m.IsImage);
            var videoCount = DatasetImages.Count(m => m.IsVideo);
            
            if (dataset.HasMultipleVersions)
            {
                StatusMessage = videoCount > 0
                    ? $"Loaded {imageCount} images, {videoCount} videos (Version {dataset.CurrentVersion} of {dataset.TotalVersions})"
                    : $"Loaded {imageCount} images (Version {dataset.CurrentVersion} of {dataset.TotalVersions})";
            }
            else
            {
                StatusMessage = videoCount > 0
                    ? $"Loaded {imageCount} images, {videoCount} videos"
                    : $"Loaded {imageCount} images";
            }
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

    /// <summary>
    /// Generates a thumbnail for a video file if it doesn't already exist.
    /// </summary>
    private async Task GenerateVideoThumbnailAsync(DatasetImageViewModel mediaVm)
    {
        if (_videoThumbnailService is null || !mediaVm.IsVideo)
            return;

        try
        {
            var result = await _videoThumbnailService.GenerateThumbnailAsync(mediaVm.ImagePath);
            if (result.Success && result.ThumbnailPath is not null)
            {
                mediaVm.ThumbnailPath = result.ThumbnailPath;
            }
        }
        catch
        {
            // Ignore thumbnail generation errors - video will display without preview
        }
    }

    private async Task AddImagesAsync()
    {
        if (DialogService is null || ActiveDataset is null) return;

        // Set flag to disable drag-drop on base view while dialog is open
        IsFileDialogOpen = true;
        
        try
        {
            // Show drag-drop file picker dialog
            var files = await DialogService.ShowFileDropDialogAsync($"Add Media to: {ActiveDataset.Name}");
            if (files is null || files.Count == 0) return;

            IsLoading = true;
            try
            {
                var copied = 0;
                var skipped = 0;
                var videoThumbnailsGenerated = 0;

                // Add to the current version folder
                var destFolderPath = ActiveDataset.CurrentVersionFolderPath;
                
                // Ensure the folder exists (for new versioned datasets)
                Directory.CreateDirectory(destFolderPath);

                foreach (var sourceFile in files)
                {
                    var fileName = Path.GetFileName(sourceFile);
                    var destPath = Path.Combine(destFolderPath, fileName);

                    if (!File.Exists(destPath))
                    {
                        File.Copy(sourceFile, destPath);
                        copied++;
                        
                        // Generate thumbnail for video files
                        if (_videoThumbnailService is not null && DatasetImageViewModel.IsVideoFile(destPath))
                        {
                            var result = await _videoThumbnailService.GenerateThumbnailAsync(destPath);
                            if (result.Success)
                            {
                                videoThumbnailsGenerated++;
                            }
                        }
                    }
                    else
                    {
                        skipped++;
                    }
                }

                var message = skipped > 0 
                    ? $"Added {copied} files, skipped {skipped} duplicates"
                    : $"Added {copied} files to dataset";
                
                if (videoThumbnailsGenerated > 0)
                {
                    message += $" ({videoThumbnailsGenerated} video thumbnails generated)";
                }
                
                StatusMessage = message;
                
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
        finally
        {
            // Always reset the flag when dialog closes
            IsFileDialogOpen = false;
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

    /// <summary>
    /// Switches to a different version of the current dataset.
    /// </summary>
    private async Task SwitchVersionAsync(int version)
    {
        if (ActiveDataset is null) return;

        // Check for unsaved changes before switching
        if (HasUnsavedChanges && DialogService is not null)
        {
            var save = await DialogService.ShowConfirmAsync(
                "Unsaved Changes",
                "You have unsaved caption changes. Save them before switching versions?");
            
            if (save)
            {
                SaveAllCaptions();
            }
        }

        // Update the dataset's current version
        ActiveDataset.CurrentVersion = version;
        ActiveDataset.SaveMetadata();
        ActiveDataset.RefreshImageInfo();

        // Reload images for the new version
        await OpenDatasetAsync(ActiveDataset);
        
        StatusMessage = $"Switched to Version {version}";
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
            
            // Create a DatasetCardViewModel for the new dataset and navigate into it
            var newDataset = DatasetCardViewModel.FromFolder(datasetPath);
            Datasets.Add(newDataset);
            
            // Navigate into the new dataset
            await OpenDatasetAsync(newDataset);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to create dataset: {ex.Message}";
        }
    }

    private void OpenContainingFolder()
    {
        if (ActiveDataset is null) return;

        // Open the current version folder (not the base folder)
        var folderPath = ActiveDataset.CurrentVersionFolderPath;
        
        // Fall back to base folder if version folder doesn't exist
        if (!Directory.Exists(folderPath))
        {
            folderPath = ActiveDataset.FolderPath;
        }

        if (!Directory.Exists(folderPath)) return;

        try
        {
            // Open folder in file explorer
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error opening folder: {ex.Message}";
        }
    }

    private void OnCaptionChanged(DatasetImageViewModel image)
    {
        HasUnsavedChanges = DatasetImages.Any(i => i.HasUnsavedChanges);
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

    /// <summary>
    /// Increments the dataset version by creating a new version folder.
    /// User can choose to copy current version's files or start fresh (empty).
    /// </summary>
    private async Task IncrementVersionAsync()
    {
        if (ActiveDataset is null || DialogService is null) return;

        var currentVersion = ActiveDataset.CurrentVersion;
        var nextVersion = ActiveDataset.GetNextVersionNumber();
        
        // Show 3-option dialog: Cancel, Copy from existing, Start fresh
        var selectedOption = await DialogService.ShowOptionsAsync(
            "Create New Version",
            $"Create V{nextVersion} branching from V{currentVersion}.\n\n" +
            $"Choose how to initialize the new version:",
            "Cancel",
            "Start Fresh (Empty)",
            $"Copy from V{currentVersion}");

        // 0 = Cancel, 1 = Start Fresh, 2 = Copy
        if (selectedOption == 0 || selectedOption == -1)
        {
            // User cancelled
            return;
        }

        var copyFiles = selectedOption == 2;

        // If they chose to copy but there are no images, warn them
        if (copyFiles && ActiveDataset.ImageCount == 0)
        {
            await DialogService.ShowMessageAsync(
                "No Images",
                "There are no images in the current version to copy. Creating an empty version instead.");
            copyFiles = false;
        }

        IsLoading = true;
        try
        {
            var destPath = ActiveDataset.GetVersionFolderPath(nextVersion);

            // Handle migration from legacy (non-versioned) to versioned structure
            if (!ActiveDataset.IsVersionedStructure && ActiveDataset.ImageCount > 0)
            {
                // First, move existing files to V1 folder
                await MigrateLegacyToVersionedAsync(ActiveDataset);
            }

            // Create the new version folder
            Directory.CreateDirectory(destPath);

            var copied = 0;
            if (copyFiles)
            {
                var sourcePath = ActiveDataset.GetVersionFolderPath(currentVersion);
                
                // Copy all files (images and captions)
                var files = Directory.EnumerateFiles(sourcePath)
                    .Where(f => !Path.GetFileName(f).StartsWith(".")) // Skip hidden files
                    .ToList();

                foreach (var sourceFile in files)
                {
                    var fileName = Path.GetFileName(sourceFile);
                    var destFile = Path.Combine(destPath, fileName);
                    File.Copy(sourceFile, destFile, overwrite: false);
                    copied++;
                }
            }

            // Record which version this was branched from
            ActiveDataset.RecordBranch(nextVersion, currentVersion);

            // Update metadata
            ActiveDataset.CurrentVersion = nextVersion;
            ActiveDataset.IsVersionedStructure = true;
            ActiveDataset.SaveMetadata();

            // Refresh the dataset to show new version
            ActiveDataset.RefreshImageInfo();
            
            // Reload images for the new version
            await OpenDatasetAsync(ActiveDataset);

            StatusMessage = copyFiles 
                ? $"Created V{nextVersion} (branched from V{currentVersion}) with {copied} files copied."
                : $"Created V{nextVersion} (branched from V{currentVersion}, empty - ready to add images).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error creating new version: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Migrates a legacy (flat) dataset structure to the versioned structure.
    /// Moves all images and captions from root folder to V1 subfolder.
    /// </summary>
    private async Task MigrateLegacyToVersionedAsync(DatasetCardViewModel dataset)
    {
        var rootPath = dataset.FolderPath;
        var v1Path = dataset.GetVersionFolderPath(1);

        // Create V1 folder
        Directory.CreateDirectory(v1Path);

        // Move all media and text files to V1
        var filesToMove = Directory.EnumerateFiles(rootPath)
            .Where(f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                var fileName = Path.GetFileName(f);
                
                // Skip .dataset folder files and hidden files
                if (fileName.StartsWith(".")) return false;
                
                // Include images, videos, and text files
                return MediaExtensions.Contains(ext) || ext == ".txt";
            })
            .ToList();

        foreach (var sourceFile in filesToMove)
        {
            var fileName = Path.GetFileName(sourceFile);
            var destFile = Path.Combine(v1Path, fileName);
            File.Move(sourceFile, destFile);
        }

        // Update dataset state
        dataset.IsVersionedStructure = true;
        dataset.CurrentVersion = 1;
        dataset.TotalVersions = 1;
        dataset.SaveMetadata();

        await Task.CompletedTask;
    }

    private async void OnImageDeleteRequested(DatasetImageViewModel image)
    {
        if (DialogService is null) return;

        var confirm = await DialogService.ShowConfirmAsync(
            "Delete Media",
            $"Delete '{image.FullFileName}' and its caption?");

        if (!confirm) return;

        try
        {
            // Delete media file
            if (File.Exists(image.ImagePath))
            {
                File.Delete(image.ImagePath);
            }

            // Delete caption file
            if (File.Exists(image.CaptionFilePath))
            {
                File.Delete(image.CaptionFilePath);
            }
            
            // Delete video thumbnail if exists
            if (image.IsVideo)
            {
                var thumbnailPath = Path.ChangeExtension(image.ImagePath, ".webp");
                if (File.Exists(thumbnailPath))
                {
                    File.Delete(thumbnailPath);
                }
            }

            DatasetImages.Remove(image);
            
            // Update the dataset card counts
            if (ActiveDataset is not null)
            {
                ActiveDataset.ImageCount = DatasetImages.Count(m => m.IsImage);
                ActiveDataset.VideoCount = DatasetImages.Count(m => m.IsVideo);
            }

            StatusMessage = $"Deleted '{image.FullFileName}'";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error deleting media: {ex.Message}";
        }
    }

    private void SendToImageEdit(DatasetImageViewModel? image)
    {
        if (image is null) return;

        // Only allow image editing for images, not videos
        if (image.IsVideo)
        {
            StatusMessage = "Video editing is not supported. Use an external video editor.";
            return;
        }

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
            // Refresh the dataset card's image info from current version folder
            ActiveDataset.RefreshImageInfo();

            // Reload images if viewing the dataset
            if (IsViewingDataset)
            {
                await OpenDatasetAsync(ActiveDataset);
            }
        }
    }
}
