using System.Collections.ObjectModel;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
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
    private DatasetType? _selectedType;
    private bool _flattenVersions;
    private bool _isFileDialogOpen;
    private int _selectionCount;
    private DatasetImageViewModel? _lastClickedImage;

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
    /// Selected type for the active dataset.
    /// </summary>
    public DatasetType? SelectedType
    {
        get => _selectedType;
        set
        {
            if (SetProperty(ref _selectedType, value) && ActiveDataset is not null)
            {
                ActiveDataset.Type = value;
                ActiveDataset.SaveMetadata();
                StatusMessage = value is not null 
                    ? $"Type set to '{value.Value.GetDisplayName()}'" 
                    : "Type cleared";
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

    /// <summary>
    /// Number of currently selected images.
    /// </summary>
    public int SelectionCount
    {
        get => _selectionCount;
        private set
        {
            if (SetProperty(ref _selectionCount, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionText));
            }
        }
    }

    /// <summary>
    /// Whether any images are currently selected.
    /// </summary>
    public bool HasSelection => _selectionCount > 0;

    /// <summary>
    /// Text describing the current selection (e.g., "3 selected").
    /// </summary>
    public string SelectionText => _selectionCount == 1 ? "1 selected" : $"{_selectionCount} selected";

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
    /// Available dataset types (hardcoded: Image, Video, Instruction).
    /// </summary>
    public IReadOnlyList<DatasetType> AvailableTypes { get; } = DatasetTypeExtensions.GetAll();

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
    public IAsyncRelayCommand ExportDatasetCommand { get; }
    
    // Selection commands
    public IRelayCommand<DatasetImageViewModel?> ToggleSelectionCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand ClearSelectionCommand { get; }
    public IRelayCommand SelectApprovedCommand { get; }
    public IRelayCommand SelectRejectedCommand { get; }
    public IRelayCommand ApproveSelectedCommand { get; }
    public IRelayCommand RejectSelectedCommand { get; }
    public IRelayCommand ClearRatingSelectedCommand { get; }
    public IAsyncRelayCommand DeleteSelectedCommand { get; }

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
        ExportDatasetCommand = new AsyncRelayCommand(ExportDatasetAsync);
        
        // Selection commands
        ToggleSelectionCommand = new RelayCommand<DatasetImageViewModel?>(ToggleSelection);
        SelectAllCommand = new RelayCommand(SelectAll);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        ApproveSelectedCommand = new RelayCommand(ApproveSelected);
        RejectSelectedCommand = new RelayCommand(RejectSelected);
        ClearRatingSelectedCommand = new RelayCommand(ClearRatingSelected);
        SelectApprovedCommand = new RelayCommand(SelectApproved);
        SelectRejectedCommand = new RelayCommand(SelectRejected);
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync);
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

            // Get all files in the folder
            var allFiles = Directory.EnumerateFiles(mediaFolderPath).ToList();
            
            // Load all media files (images and videos), excluding video thumbnails
            var mediaFiles = allFiles
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (!MediaExtensions.Contains(ext))
                        return false;
                    
                    // Exclude video thumbnail files (files ending with _thumb.webp etc.)
                    if (DatasetCardViewModel.IsVideoThumbnailFile(f))
                        return false;
                    
                    return true;
                })
                .OrderBy(f => f)
                .ToList();

            foreach (var mediaPath in mediaFiles)
            {
                var mediaVm = DatasetImageViewModel.FromFile(
                    mediaPath,
                    OnImageDeleteRequested,
                    OnCaptionChanged,
                    onRatingChanged: null,
                    onSelectionChanged: OnSelectionChanged);
                
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

            // Set selected type based on dataset metadata
            _selectedType = dataset.Type;
            OnPropertyChanged(nameof(SelectedType));

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

        // Clear selection before leaving
        ClearSelectionSilent();

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

        // Show create dataset dialog with category and type selection
        var result = await DialogService.ShowCreateDatasetDialogAsync(AvailableCategories);

        if (!result.Confirmed || string.IsNullOrWhiteSpace(result.Name))
        {
            return;
        }

        var sanitizedName = result.Name;

        // Create the folder
        var datasetPath = Path.Combine(settings.DatasetStoragePath, sanitizedName);

        if (Directory.Exists(datasetPath))
        {
            StatusMessage = $"A dataset named '{sanitizedName}' already exists.";
            return;
        }

        try
        {
            // Create the main dataset folder
            Directory.CreateDirectory(datasetPath);
            
            // Create V1 subfolder immediately for versioned structure
            var v1Path = Path.Combine(datasetPath, "V1");
            Directory.CreateDirectory(v1Path);
            
            // Create a DatasetCardViewModel for the new dataset
            var newDataset = new DatasetCardViewModel
            {
                Name = sanitizedName,
                FolderPath = datasetPath,
                IsVersionedStructure = true,
                CurrentVersion = 1,
                TotalVersions = 1,
                ImageCount = 0,
                VideoCount = 0,
                CategoryId = result.CategoryId,
                CategoryName = result.CategoryName,
                Type = result.Type
            };
            
            // Save metadata to establish versioned structure
            newDataset.SaveMetadata();
            
            Datasets.Add(newDataset);
            
            StatusMessage = $"Dataset '{sanitizedName}' created successfully.";
            
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

    private void OnSelectionChanged(DatasetImageViewModel image)
    {
        UpdateSelectionCount();
    }

    private async Task DeleteDatasetAsync(DatasetCardViewModel? dataset)
    {
        if (dataset is null || DialogService is null) return;

        // Check if this is a version card (flattened view) or a full dataset
        if (dataset.IsVersionCard && dataset.DisplayVersion.HasValue)
        {
            // Deleting a specific version
            await DeleteVersionAsync(dataset, dataset.DisplayVersion.Value);
        }
        else
        {
            // Deleting the entire dataset
            var confirm = await DialogService.ShowConfirmAsync(
                "Delete Dataset",
                $"Are you sure you want to delete '{dataset.Name}'? This will permanently delete all images and captions in ALL versions of this dataset.");

            if (!confirm) return;

            try
            {
                Directory.Delete(dataset.FolderPath, recursive: true);
                Datasets.Remove(dataset);
                
                // Also remove from grouped datasets
                foreach (var group in GroupedDatasets)
                {
                    group.Datasets.Remove(dataset);
                }
                
                // Remove any empty groups
                var emptyGroups = GroupedDatasets.Where(g => !g.HasDatasets).ToList();
                foreach (var emptyGroup in emptyGroups)
                {
                    GroupedDatasets.Remove(emptyGroup);
                }
                
                StatusMessage = $"Deleted dataset '{dataset.Name}'";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error deleting dataset: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Deletes a specific version from a dataset.
    /// </summary>
    private async Task DeleteVersionAsync(DatasetCardViewModel dataset, int version)
    {
        if (DialogService is null) return;

        var versionPath = dataset.GetVersionFolderPath(version);
        
        // Count versions to determine if this would delete the last one
        var allVersions = dataset.GetAllVersionNumbers();
        var isLastVersion = allVersions.Count == 1;

        string confirmMessage;
        if (isLastVersion)
        {
            confirmMessage = $"Are you sure you want to delete V{version} of '{dataset.Name}'?\n\n" +
                           "This is the only version - the entire dataset will be removed.";
        }
        else
        {
            confirmMessage = $"Are you sure you want to delete V{version} of '{dataset.Name}'?\n\n" +
                           "This will permanently delete all images and captions in this version.";
        }

        var confirm = await DialogService.ShowConfirmAsync("Delete Version", confirmMessage);
        if (!confirm) return;

        try
        {
            if (isLastVersion)
            {
                // Delete entire dataset if last version
                Directory.Delete(dataset.FolderPath, recursive: true);
                
                // Remove from Datasets collection
                var parentDataset = Datasets.FirstOrDefault(d => d.FolderPath == dataset.FolderPath);
                if (parentDataset is not null)
                {
                    Datasets.Remove(parentDataset);
                }
            }
            else
            {
                // Delete only the version folder
                if (Directory.Exists(versionPath))
                {
                    Directory.Delete(versionPath, recursive: true);
                }

                // Clean up version metadata
                dataset.VersionBranchedFrom.Remove(version);
                dataset.VersionDescriptions.Remove(version);
                
                // Update the parent dataset's metadata
                var parentDataset = Datasets.FirstOrDefault(d => d.FolderPath == dataset.FolderPath);
                if (parentDataset is not null)
                {
                    parentDataset.VersionBranchedFrom.Remove(version);
                    parentDataset.VersionDescriptions.Remove(version);
                    
                    // If we deleted the current version, switch to another version
                    if (parentDataset.CurrentVersion == version)
                    {
                        var remainingVersions = parentDataset.GetAllVersionNumbers();
                        parentDataset.CurrentVersion = remainingVersions.FirstOrDefault(v => v != version);
                        if (parentDataset.CurrentVersion == 0)
                        {
                            parentDataset.CurrentVersion = remainingVersions.First();
                        }
                    }
                    
                    parentDataset.RefreshImageInfo();
                    parentDataset.SaveMetadata();
                }
            }
            
            // Remove from grouped datasets
            foreach (var group in GroupedDatasets)
            {
                group.Datasets.Remove(dataset);
            }
            
            // Remove any empty groups
            var emptyGroups = GroupedDatasets.Where(g => !g.HasDatasets).ToList();
            foreach (var emptyGroup in emptyGroups)
            {
                GroupedDatasets.Remove(emptyGroup);
            }
            
            StatusMessage = isLastVersion 
                ? $"Deleted dataset '{dataset.Name}'" 
                : $"Deleted V{version} of '{dataset.Name}'";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error deleting version: {ex.Message}";
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
                var thumbnailPath = DatasetCardViewModel.GetVideoThumbnailPath(image.ImagePath);
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

    private async Task ExportDatasetAsync()
    {
        if (DialogService is null || ActiveDataset is null)
        {
            StatusMessage = "No dataset selected for export.";
            return;
        }

        if (DatasetImages.Count == 0)
        {
            StatusMessage = "No files in dataset to export.";
            return;
        }

        // Show export configuration dialog
        var result = await DialogService.ShowExportDialogAsync(ActiveDataset.Name, DatasetImages);
        
        if (!result.Confirmed || result.FilesToExport.Count == 0)
        {
            return;
        }

        // Ask for destination
        string? destinationPath;
        if (result.ExportType == ExportType.Zip)
        {
            // Format: DatasetName_V1-2025-01-06.zip
            var dateStr = DateTime.Today.ToString("yyyy-MM-dd");
            var defaultFileName = $"{ActiveDataset.Name}_V{ActiveDataset.CurrentVersion}-{dateStr}.zip";
            
            destinationPath = await DialogService.ShowSaveFileDialogAsync(
                "Export Dataset as ZIP",
                defaultFileName,
                "*.zip");
        }
        else
        {
            destinationPath = await DialogService.ShowOpenFolderDialogAsync("Select Export Destination Folder");
        }

        if (string.IsNullOrEmpty(destinationPath))
        {
            return;
        }

        IsLoading = true;
        try
        {
            var exportedCount = 0;

            if (result.ExportType == ExportType.Zip)
            {
                exportedCount = await ExportAsZipAsync(result.FilesToExport, destinationPath);
            }
            else
            {
                exportedCount = await ExportAsSingleFilesAsync(result.FilesToExport, destinationPath);
            }

            StatusMessage = $"Exported {exportedCount} files successfully.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Exports files as individual files with their caption .txt files.
    /// </summary>
    private async Task<int> ExportAsSingleFilesAsync(List<DatasetImageViewModel> files, string destinationFolder)
    {
        var exportedCount = 0;

        // Create destination folder if it doesn't exist
        Directory.CreateDirectory(destinationFolder);

        foreach (var mediaFile in files)
        {
            // Copy media file
            if (File.Exists(mediaFile.ImagePath))
            {
                var destMediaPath = Path.Combine(destinationFolder, mediaFile.FullFileName);
                File.Copy(mediaFile.ImagePath, destMediaPath, overwrite: true);
                exportedCount++;
            }

            // Copy caption file if it exists
            if (File.Exists(mediaFile.CaptionFilePath))
            {
                var captionFileName = Path.GetFileName(mediaFile.CaptionFilePath);
                var destCaptionPath = Path.Combine(destinationFolder, captionFileName);
                File.Copy(mediaFile.CaptionFilePath, destCaptionPath, overwrite: true);
            }
        }

        await Task.CompletedTask;
        return exportedCount;
    }

    /// <summary>
    /// Exports files as a ZIP archive with their caption .txt files.
    /// </summary>
    private async Task<int> ExportAsZipAsync(List<DatasetImageViewModel> files, string zipPath)
    {
        var exportedCount = 0;

        // Delete existing file if present
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var mediaFile in files)
            {
                // Add media file
                if (File.Exists(mediaFile.ImagePath))
                {
                    archive.CreateEntryFromFile(mediaFile.ImagePath, mediaFile.FullFileName);
                    exportedCount++;
                }

                // Add caption file if it exists
                if (File.Exists(mediaFile.CaptionFilePath))
                {
                    var captionFileName = Path.GetFileName(mediaFile.CaptionFilePath);
                    archive.CreateEntryFromFile(mediaFile.CaptionFilePath, captionFileName);
                }
            }
        }

        await Task.CompletedTask;
        return exportedCount;
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

    #region Selection Methods

    /// <summary>
    /// Toggles selection state of an image.
    /// </summary>
    private void ToggleSelection(DatasetImageViewModel? image)
    {
        if (image is null) return;

        image.IsSelected = !image.IsSelected;
        _lastClickedImage = image;
        UpdateSelectionCount();
    }

    /// <summary>
    /// Handles selection with modifier keys (Shift for range, Ctrl for toggle).
    /// </summary>
    public void SelectWithModifiers(DatasetImageViewModel? image, bool isShiftPressed, bool isCtrlPressed)
    {
        if (image is null) return;

        if (isShiftPressed && _lastClickedImage is not null)
        {
            // Range selection: select all images between last clicked and current
            SelectRange(_lastClickedImage, image);
        }
        else if (isCtrlPressed)
        {
            // Toggle selection (Ctrl+Click)
            image.IsSelected = !image.IsSelected;
            _lastClickedImage = image;
        }
        else
        {
            // Normal click: clear other selections and select this one
            ClearSelectionSilent();
            image.IsSelected = true;
            _lastClickedImage = image;
        }

        UpdateSelectionCount();
    }

    /// <summary>
    /// Selects all images in a range between two images (inclusive).
    /// </summary>
    private void SelectRange(DatasetImageViewModel from, DatasetImageViewModel to)
    {
        var fromIndex = DatasetImages.IndexOf(from);
        var toIndex = DatasetImages.IndexOf(to);

        if (fromIndex == -1 || toIndex == -1) return;

        var startIndex = Math.Min(fromIndex, toIndex);
        var endIndex = Math.Max(fromIndex, toIndex);

        for (var i = startIndex; i <= endIndex; i++)
        {
            DatasetImages[i].IsSelected = true;
        }

        _lastClickedImage = to;
    }

    /// <summary>
    /// Selects images by their indices (used for marquee/drag selection).
    /// </summary>
    public void SelectByIndices(IEnumerable<int> indices, bool addToSelection)
    {
        if (!addToSelection)
        {
            ClearSelectionSilent();
        }

        foreach (var index in indices)
        {
            if (index >= 0 && index < DatasetImages.Count)
            {
                DatasetImages[index].IsSelected = true;
            }
        }

        UpdateSelectionCount();
    }

    /// <summary>
    /// Selects all images in the current dataset.
    /// </summary>
    private void SelectAll()
    {
        foreach (var image in DatasetImages)
        {
            image.IsSelected = true;
        }
        UpdateSelectionCount();
        StatusMessage = $"Selected all {SelectionCount} items";
    }

    /// <summary>
    /// Clears all selections.
    /// </summary>
    private void ClearSelection()
    {
        ClearSelectionSilent();
        StatusMessage = "Selection cleared";
    }

    /// <summary>
    /// Clears all selections without showing a status message.
    /// Used when navigating away or resetting state.
    /// </summary>
    private void ClearSelectionSilent()
    {
        foreach (var image in DatasetImages)
        {
            image.IsSelected = false;
        }
        SelectionCount = 0;
    }

    /// <summary>
    /// Marks all selected images as approved (production-ready).
    /// </summary>
    private void ApproveSelected()
    {
        var selected = DatasetImages.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        foreach (var image in selected)
        {
            image.RatingStatus = ImageRatingStatus.Approved;
            image.SaveRating();
        }
        StatusMessage = $"Marked {selected.Count} items as production-ready";
    }

    /// <summary>
    /// Marks all selected images as rejected (failed).
    /// </summary>
    private void RejectSelected()
    {
        var selected = DatasetImages.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        foreach (var image in selected)
        {
            image.RatingStatus = ImageRatingStatus.Rejected;
            image.SaveRating();
        }
        StatusMessage = $"Marked {selected.Count} items as failed";
    }

    /// <summary>
    /// Clears rating for all selected images.
    /// </summary>
    private void ClearRatingSelected()
    {
        var selected = DatasetImages.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        foreach (var image in selected)
        {
            image.RatingStatus = ImageRatingStatus.Unrated;
            image.SaveRating();
        }
        StatusMessage = $"Cleared rating for {selected.Count} items";
    }

    /// <summary>
    /// Selects all approved images.
    /// </summary>
    private void SelectApproved()
    {
        // Clear current selection
        ClearSelectionSilent();

        foreach (var image in DatasetImages.Where(i => i.RatingStatus == ImageRatingStatus.Approved))
        {
            image.IsSelected = true;
        }
        UpdateSelectionCount();
        StatusMessage = $"Selected {SelectionCount} approved items";
    }

    /// <summary>
    /// Selects all rejected images.
    /// </summary>
    private void SelectRejected()
    {
        // Clear current selection
        ClearSelectionSilent();

        foreach (var image in DatasetImages.Where(i => i.RatingStatus == ImageRatingStatus.Rejected))
        {
            image.IsSelected = true;
        }
        UpdateSelectionCount();
        StatusMessage = $"Selected {SelectionCount} rejected items";
    }

    /// <summary>
    /// Deletes all selected images.
    /// </summary>
    private async Task DeleteSelectedAsync()
    {
        if (DialogService is null) return;

        var selectedImages = DatasetImages.Where(i => i.IsSelected).ToList();
        if (selectedImages.Count == 0) return;

        var confirm = await DialogService.ShowConfirmAsync(
            "Delete Selected Media",
            $"Are you sure you want to delete {selectedImages.Count} selected media items?");

        if (!confirm) return;

        try
        {
            foreach (var image in selectedImages)
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
                    var thumbnailPath = DatasetCardViewModel.GetVideoThumbnailPath(image.ImagePath);
                    if (File.Exists(thumbnailPath))
                    {
                        File.Delete(thumbnailPath);
                    }
                }

                DatasetImages.Remove(image);
            }

            // Update the dataset card counts
            if (ActiveDataset is not null)
            {
                ActiveDataset.ImageCount = DatasetImages.Count(m => m.IsImage);
                ActiveDataset.VideoCount = DatasetImages.Count(m => m.IsVideo);
            }

            StatusMessage = $"Deleted {selectedImages.Count} items";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error deleting selected media: {ex.Message}";
        }
    }

    /// <summary>
    /// Updates the selection count based on currently selected images.
    /// </summary>
    private void UpdateSelectionCount()
    {
        SelectionCount = DatasetImages.Count(i => i.IsSelected);
    }

    #endregion
}
