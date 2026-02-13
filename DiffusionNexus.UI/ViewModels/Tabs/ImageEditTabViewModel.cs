using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Utilities;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// ViewModel for the Image Edit tab in the LoRA Dataset Helper.
/// Manages image editor state, dataset/version navigation, and thumbnail list.
/// 
/// <para>
/// <b>Responsibilities:</b>
/// <list type="bullet">
/// <item>Dataset and version selection for browsing images</item>
/// <item>Thumbnail list management</item>
/// <item>Coordinating with ImageEditorViewModel for editing</item>
/// <item>Publishing image save events</item>
/// </list>
/// </para>
/// 
/// <para>
/// <b>Event Integration:</b>
/// Subscribes to:
/// <list type="bullet">
/// <item>NavigateToImageEditorRequested - to load images sent from Dataset Management</item>
/// <item>ImageSaved - to refresh thumbnail list after saves</item>
/// <item>ActiveDatasetChanged - to sync editor dataset selection</item>
/// <item>ImageRatingChanged - to sync ratings from other components</item>
/// </list>
/// </para>
/// 
/// <para>
/// <b>Disposal:</b>
/// Implements <see cref="IDisposable"/> to properly unsubscribe from events.
/// </para>
/// </summary>
public partial class ImageEditTabViewModel : ObservableObject, IDialogServiceAware, IThumbnailAware, IDisposable
{
    private readonly IDatasetEventAggregator _eventAggregator;
    private readonly IDatasetState _state;
    private readonly IBackgroundRemovalService? _backgroundRemovalService;
    private readonly IImageUpscalingService? _upscalingService;
    private readonly IComfyUIWrapperService? _comfyUiService;
    private readonly IThumbnailOrchestrator? _thumbnailOrchestrator;
    private bool _disposed;

    private readonly ObservableCollection<DatasetCardViewModel> _editorDatasets = [];
    private DatasetCardViewModel? _selectedEditorDataset;
    private EditorVersionItem? _selectedEditorVersion;
    private DatasetImageViewModel? _selectedEditorImage;
    private DatasetCardViewModel? _temporaryEditorDataset;
    private readonly List<DatasetImageViewModel> _temporaryEditorImages = [];
    
    // Filter properties - all default to true (show all)
    private bool _showReady = true;
    private bool _showTrash = true;
    private bool _showUnrated = true;

    /// <summary>
    /// Gets or sets the dialog service for showing dialogs.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    #region Filter Properties

    /// <summary>
    /// Whether to show images marked as Ready/Production.
    /// </summary>
    public bool ShowReady
    {
        get => _showReady;
        set
        {
            if (SetProperty(ref _showReady, value))
            {
                ApplyFilters();
            }
        }
    }

    /// <summary>
    /// Whether to show images marked as Trash/Rejected.
    /// </summary>
    public bool ShowTrash
    {
        get => _showTrash;
        set
        {
            if (SetProperty(ref _showTrash, value))
            {
                ApplyFilters();
            }
        }
    }

    /// <summary>
    /// Whether to show unrated images.
    /// </summary>
    public bool ShowUnrated
    {
        get => _showUnrated;
        set
        {
            if (SetProperty(ref _showUnrated, value))
            {
                ApplyFilters();
            }
        }
    }

    /// <summary>
    /// Text showing current filter status.
    /// </summary>
    public string FilterStatusText
    {
        get
        {
            var total = EditorDatasetImages.Count;
            var filtered = FilteredEditorImages.Count;
            if (total == filtered)
                return $"{total} Images";
            return $"{filtered} of {total} Images";
        }
    }

    #endregion

    #region IThumbnailAware

    /// <inheritdoc />
    public ThumbnailOwnerToken OwnerToken { get; } = new("ImageEdit");

    /// <inheritdoc />
    public void OnThumbnailActivated()
    {
        _thumbnailOrchestrator?.SetActiveOwner(OwnerToken);
    }

    /// <inheritdoc />
    public void OnThumbnailDeactivated()
    {
        _thumbnailOrchestrator?.CancelRequests(OwnerToken);
    }

    #endregion

    #region Observable Properties

    /// <summary>
    /// Selected dataset in the Image Edit tab.
    /// </summary>
    public DatasetCardViewModel? SelectedEditorDataset
    {
        get => _selectedEditorDataset;
        set
        {
            // Ignore null pushed by Avalonia ComboBox TwoWay binding when the
            // control is detached from the visual tree during tab switches.
            if (value is null && _selectedEditorDataset is not null) return;

            if (SetProperty(ref _selectedEditorDataset, value))
            {
                _state.SelectedEditorDataset = value;
                _ = LoadEditorDatasetVersionsAsync();
            }
        }
    }

    /// <summary>
    /// Selected version in the Image Edit tab.
    /// </summary>
    public EditorVersionItem? SelectedEditorVersion
    {
        get => _selectedEditorVersion;
        set
        {
            // Ignore null pushed by Avalonia ComboBox TwoWay binding when the
            // control is detached from the visual tree during tab switches.
            if (value is null && _selectedEditorVersion is not null) return;

            if (SetProperty(ref _selectedEditorVersion, value))
            {
                _state.SelectedEditorVersion = value;
                _ = LoadEditorDatasetImagesAsync();
            }
        }
    }

    /// <summary>
    /// Currently selected image in the Image Edit tab thumbnail list.
    /// </summary>
    public DatasetImageViewModel? SelectedEditorImage
    {
        get => _selectedEditorImage;
        set
        {
            if (SetProperty(ref _selectedEditorImage, value))
            {
                _state.SelectedEditorImage = value;
                if (value is not null)
                {
                    LoadEditorImage(value);
                }
            }
        }
    }

    /// <summary>
    /// Status message to display.
    /// </summary>
    public string? StatusMessage
    {
        get => _state.StatusMessage;
        set => _state.StatusMessage = value;
    }

    #endregion

    #region Collections

    /// <summary>
    /// Collection of all datasets for the dropdown.
    /// </summary>
    public ObservableCollection<DatasetCardViewModel> EditorDatasets => _editorDatasets;

    /// <summary>
    /// Version items for the version dropdown.
    /// </summary>
    public ObservableCollection<EditorVersionItem> EditorVersionItems { get; } = [];

    /// <summary>
    /// All images available in the Image Edit tab for the selected dataset/version.
    /// </summary>
    public ObservableCollection<DatasetImageViewModel> EditorDatasetImages { get; } = [];

    /// <summary>
    /// Filtered images based on rating filter settings.
    /// </summary>
    public ObservableCollection<DatasetImageViewModel> FilteredEditorImages { get; } = [];

    #endregion

    #region Commands

    public IRelayCommand<DatasetImageViewModel?> LoadEditorImageCommand { get; }

    #endregion

    /// <summary>
    /// Gets the Image Editor ViewModel.
    /// </summary>
    public ImageEditorViewModel ImageEditor { get; }

    #region Constructors

    /// <summary>
    /// Creates a new instance of ImageEditTabViewModel.
    /// </summary>
    public ImageEditTabViewModel(
        IDatasetEventAggregator eventAggregator,
        IDatasetState state,
        IBackgroundRemovalService? backgroundRemovalService = null,
        IImageUpscalingService? upscalingService = null,
        IComfyUIWrapperService? comfyUiService = null,
        IThumbnailOrchestrator? thumbnailOrchestrator = null)
    {
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _backgroundRemovalService = backgroundRemovalService;
        _upscalingService = upscalingService;
        _comfyUiService = comfyUiService;
        _thumbnailOrchestrator = thumbnailOrchestrator;

        // Create the image editor with background removal and upscaling services
        ImageEditor = new ImageEditorViewModel(_eventAggregator, _backgroundRemovalService, _upscalingService, _comfyUiService);

        // Subscribe to events
        _eventAggregator.NavigateToImageEditorRequested += OnNavigateToImageEditorRequested;
        _eventAggregator.ImageSaved += OnImageSaved;
        _eventAggregator.ImageDeleted += OnImageDeleted;
        _eventAggregator.ImageRatingChanged += OnImageRatingChanged;
        _eventAggregator.DatasetCreated += OnDatasetCreated;
        _eventAggregator.VersionCreated += OnVersionCreated;
        _eventAggregator.ImageAdded += OnImageAdded;

        // Subscribe to state changes
        _state.StateChanged += OnStateChanged;
        _state.Datasets.CollectionChanged += OnDatasetsCollectionChanged;

        InitializeEditorDatasets();

        // Initialize commands
        LoadEditorImageCommand = new RelayCommand<DatasetImageViewModel?>(LoadEditorImage);
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public ImageEditTabViewModel() : this(null!, null!)
    {
    }

    #endregion

    #region Event Handlers

    private void OnStateChanged(object? sender, DatasetStateChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IDatasetState.StatusMessage):
                OnPropertyChanged(nameof(StatusMessage));
                break;
        }
    }

    private async void OnNavigateToImageEditorRequested(object? sender, NavigateToImageEditorEventArgs e)
    {
        if (e.Dataset.IsTemporary || e.Images is not null)
        {
            var tempImages = e.Images?.Where(img => !img.IsVideo).ToList()
                ?? new List<DatasetImageViewModel> { e.Image };

            if (tempImages.Count == 0)
            {
                StatusMessage = "No images available for editing.";
                return;
            }

            LoadTemporaryEditorDataset(e.Dataset, tempImages, e.Image);
            return;
        }

        // Find the matching dataset in the Datasets collection
        var editorDataset = EditorDatasets.FirstOrDefault(d =>
            string.Equals(d.FolderPath, e.Dataset.FolderPath, StringComparison.OrdinalIgnoreCase));

        if (editorDataset is null) return;

        // Set the dataset (this will trigger version loading)
        _selectedEditorDataset = editorDataset;
        OnPropertyChanged(nameof(SelectedEditorDataset));
        _state.SelectedEditorDataset = editorDataset;

        // Wait for version items to be loaded
        await LoadEditorDatasetVersionsAsync();

        // Find and select the matching version
        var currentVersion = e.Dataset.CurrentVersion;
        var versionItem = EditorVersionItems.FirstOrDefault(v => v.Version == currentVersion);

        if (versionItem is not null)
        {
            _selectedEditorVersion = versionItem;
            OnPropertyChanged(nameof(SelectedEditorVersion));
            _state.SelectedEditorVersion = versionItem;

            // Wait for images to be loaded
            await LoadEditorDatasetImagesAsync();

            // Find and select the image in the thumbnail list
            var editorImage = EditorDatasetImages.FirstOrDefault(img =>
                string.Equals(img.ImagePath, e.Image.ImagePath, StringComparison.OrdinalIgnoreCase));

            if (editorImage is not null)
            {
                // Clear previous editor selection
                foreach (var img in EditorDatasetImages)
                {
                    img.IsEditorSelected = false;
                }

                // Mark this image as selected
                editorImage.IsEditorSelected = true;
                _selectedEditorImage = editorImage;
                OnPropertyChanged(nameof(SelectedEditorImage));
                _state.SelectedEditorImage = editorImage;

                // Set the selected dataset image on the ImageEditor for rating support
                ImageEditor.SelectedDatasetImage = editorImage;
            }
        }

        // Load the image into the editor
        ImageEditor.LoadImage(e.Image.ImagePath);

        // If we didn't find the image in the editor list, still set it for rating support
        if (ImageEditor.SelectedDatasetImage is null)
        {
            ImageEditor.SelectedDatasetImage = e.Image;
        }

        // Switch to Image Edit tab
        _state.SelectedTabIndex = 1;

        StatusMessage = $"Editing: {e.Image.FullFileName}";
    }

    private async void OnImageSaved(object? sender, ImageSavedEventArgs e)
    {
        FileLogger.LogEntry($"ImagePath={e.ImagePath ?? "(null)"}, OriginalPath={e.OriginalPath ?? "(null)"}");
        
        try
        {
            // Skip refresh for temporary datasets - saved images go to their original location,
            // not to the temp dataset folder
            if (_selectedEditorDataset is null)
            {
                FileLogger.Log("_selectedEditorDataset is null, returning early");
                return;
            }
            
            if (_selectedEditorVersion is null)
            {
                FileLogger.Log("_selectedEditorVersion is null, returning early");
                return;
            }

            FileLogger.Log($"Dataset: {_selectedEditorDataset.Name}, IsTemporary={_selectedEditorDataset.IsTemporary}");

            if (_selectedEditorDataset.IsTemporary)
            {
                // For temporary datasets, the image is saved to its original folder.
                // No need to refresh the temp dataset's thumbnail list.
                FileLogger.Log("Dataset is temporary, skipping refresh");
                return;
            }

            var currentVersionNumber = _selectedEditorVersion.Version;
            FileLogger.Log($"Current version: {currentVersionNumber}");

            // Reload the version items (to update image counts)
            FileLogger.Log("Refreshing version items...");
            await RefreshVersionItemsAsync(currentVersionNumber);
            FileLogger.Log("Version items refreshed");

            // Reload images for the current version
            FileLogger.Log("Loading editor dataset images...");
            await LoadEditorDatasetImagesAsync();
            FileLogger.Log("Editor dataset images loaded");

            // Find and select the saved image in the list
            FileLogger.Log($"Looking for saved image in list: {e.ImagePath}");
            var savedImageVm = EditorDatasetImages.FirstOrDefault(img =>
                string.Equals(img.ImagePath, e.ImagePath, StringComparison.OrdinalIgnoreCase));

            if (savedImageVm is not null)
            {
                FileLogger.Log("Found saved image, selecting it");
                // Clear previous selection
                foreach (var img in EditorDatasetImages)
                {
                    img.IsEditorSelected = false;
                }

                savedImageVm.IsEditorSelected = true;
                _selectedEditorImage = savedImageVm;
                OnPropertyChanged(nameof(SelectedEditorImage));
                _state.SelectedEditorImage = savedImageVm;
            }
            else
            {
                FileLogger.Log("Saved image not found in list");
            }
            
            FileLogger.LogExit("success");
        }
        catch (Exception ex)
        {
            FileLogger.LogError("Exception in OnImageSaved", ex);
            StatusMessage = $"Error refreshing images: {ex.Message}";
        }
    }

    private async void OnImageDeleted(object? sender, ImageDeletedEventArgs e)
    {
        FileLogger.LogEntry($"ImagePath={e.ImagePath ?? "(null)"}");
        
        try
        {
            // Skip refresh for temporary datasets
            if (_selectedEditorDataset is null || _selectedEditorVersion is null || _selectedEditorDataset.IsTemporary)
            {
                FileLogger.Log("Skipping - dataset/version is null or temporary");
                return;
            }

            var currentVersionNumber = _selectedEditorVersion.Version;

            // Check if the deleted image was in our current dataset/version
            var deletedImage = EditorDatasetImages.FirstOrDefault(img =>
                string.Equals(img.ImagePath, e.ImagePath, StringComparison.OrdinalIgnoreCase));

            if (deletedImage is not null)
            {
                EditorDatasetImages.Remove(deletedImage);

                // Refresh version items for updated counts
                await RefreshVersionItemsAsync(currentVersionNumber);

                // If we were editing the deleted image, clear the editor
                if (_selectedEditorImage == deletedImage)
                {
                    _selectedEditorImage = null;
                    OnPropertyChanged(nameof(SelectedEditorImage));
                    _state.SelectedEditorImage = null;
                    ImageEditor.ClearImageCommand.Execute(null);
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error refreshing images: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles rating changes from other components (e.g., Dataset Management tab).
    /// Syncs the rating to our local image instances.
    /// </summary>
    private void OnImageRatingChanged(object? sender, ImageRatingChangedEventArgs e)
    {
        // Find the matching image in EditorDatasetImages by file path and sync the rating
        var matchingImage = EditorDatasetImages.FirstOrDefault(img =>
            string.Equals(img.ImagePath, e.Image.ImagePath, StringComparison.OrdinalIgnoreCase));

        if (matchingImage is not null && matchingImage != e.Image)
        {
            // Update the rating on our instance to match - this triggers UI update via PropertyChanged
            matchingImage.RatingStatus = e.NewRating;
        }

        // Also update the ImageEditor's SelectedDatasetImage if it matches
        if (ImageEditor.SelectedDatasetImage is not null &&
            string.Equals(ImageEditor.SelectedDatasetImage.ImagePath, e.Image.ImagePath, StringComparison.OrdinalIgnoreCase) &&
            ImageEditor.SelectedDatasetImage != e.Image)
        {
            ImageEditor.SelectedDatasetImage.RatingStatus = e.NewRating;
            // Notify ImageEditor to refresh its rating display
            ImageEditor.RefreshRatingDisplay();
        }
    }

    /// <summary>
    /// Handles dataset created events - notifies UI to refresh dataset dropdown.
    /// </summary>
    private void OnDatasetCreated(object? sender, DatasetCreatedEventArgs e)
    {
        // The Datasets collection is shared via IDatasetState, so it's already updated.
        // We just need to notify the UI that the collection may have changed.
        OnPropertyChanged(nameof(EditorDatasets));
    }

    /// <summary>
    /// Handles version created events - refreshes version dropdown if viewing the affected dataset.
    /// </summary>
    private async void OnVersionCreated(object? sender, VersionCreatedEventArgs e)
    {
        // If we're currently viewing this dataset, refresh the version list
        if (_selectedEditorDataset is not null &&
            string.Equals(_selectedEditorDataset.FolderPath, e.Dataset.FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            var currentVersion = _selectedEditorVersion?.Version ?? e.NewVersion;
            await RefreshVersionItemsAsync(currentVersion);
        }
    }

    /// <summary>
    /// Handles image added events - refreshes version dropdown and image list when images are added.
    /// </summary>
    private async void OnImageAdded(object? sender, ImageAddedEventArgs e)
    {
        // If we're currently viewing this dataset, refresh the version list and images
        if (_selectedEditorDataset is not null &&
            string.Equals(_selectedEditorDataset.FolderPath, e.Dataset.FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            var currentVersion = _selectedEditorVersion?.Version;
            if (currentVersion.HasValue)
            {
                // Refresh version items for updated image counts
                await RefreshVersionItemsAsync(currentVersion.Value);
                
                // Reload images for the current version
                await LoadEditorDatasetImagesAsync();
            }
        }
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Applies the current filter settings to update FilteredEditorImages.
    /// </summary>
    private void ApplyFilters()
    {
        FilteredEditorImages.Clear();

        foreach (var image in EditorDatasetImages)
        {
            if (ShouldShowImage(image))
            {
                FilteredEditorImages.Add(image);
            }
        }

        OnPropertyChanged(nameof(FilterStatusText));
    }

    /// <summary>
    /// Determines if an image should be shown based on current filter settings.
    /// </summary>
    private bool ShouldShowImage(DatasetImageViewModel image)
    {
        return image.RatingStatus switch
        {
            ImageRatingStatus.Approved => _showReady,
            ImageRatingStatus.Rejected => _showTrash,
            ImageRatingStatus.Unrated => _showUnrated,
            _ => true
        };
    }

    /// <summary>
    /// Loads the specified image into the Image Editor.
    /// </summary>
    private void LoadEditorImage(DatasetImageViewModel? image)
    {
        if (image is null) return;

        if (image.IsVideo)
        {
            StatusMessage = "Video editing is not supported.";
            return;
        }

        // Clear previous editor selection
        foreach (var img in EditorDatasetImages)
        {
            img.IsEditorSelected = false;
        }

        // Mark this image as selected in the editor
        image.IsEditorSelected = true;
        _selectedEditorImage = image;
        OnPropertyChanged(nameof(SelectedEditorImage));
        _state.SelectedEditorImage = image;

        // Set the selected dataset image on the ImageEditor for rating support
        ImageEditor.SelectedDatasetImage = image;

        ImageEditor.LoadImage(image.ImagePath);
        StatusMessage = $"Editing: {image.FullFileName}";
    }

    /// <summary>
    /// Loads the available versions for the selected dataset.
    /// </summary>
    private async Task LoadEditorDatasetVersionsAsync()
    {
        EditorVersionItems.Clear();
        EditorDatasetImages.Clear();
        FilteredEditorImages.Clear();
        _selectedEditorVersion = null;
        OnPropertyChanged(nameof(SelectedEditorVersion));
        OnPropertyChanged(nameof(FilterStatusText));

        if (_selectedEditorDataset is null) return;
        if (_selectedEditorDataset.IsTemporary)
        {
            PopulateTemporaryVersionItems();
            return;
        }

        try
        {
            await PopulateVersionItemsAsync(_selectedEditorDataset, EditorVersionItems);

            // Auto-select the first version if available
            if (EditorVersionItems.Count > 0)
            {
                SelectedEditorVersion = EditorVersionItems[0];
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading versions: {ex.Message}";
        }
    }

    /// <summary>
    /// Refreshes version items and re-selects the specified version.
    /// </summary>
    private async Task RefreshVersionItemsAsync(int versionToSelect)
    {
        if (_selectedEditorDataset is null) return;

        // For temporary datasets, use the special population method
        if (_selectedEditorDataset.IsTemporary)
        {
            PopulateTemporaryVersionItems();
            return;
        }

        EditorVersionItems.Clear();
        await PopulateVersionItemsAsync(_selectedEditorDataset, EditorVersionItems);

        // Re-select the version
        var versionToReselect = EditorVersionItems.FirstOrDefault(v => v.Version == versionToSelect);
        if (versionToReselect is not null)
        {
            _selectedEditorVersion = versionToReselect;
            OnPropertyChanged(nameof(SelectedEditorVersion));
            _state.SelectedEditorVersion = versionToReselect;
        }
    }

    /// <summary>
    /// Populates version items for a dataset. Shared logic for initial load and refresh.
    /// </summary>
    private static Task PopulateVersionItemsAsync(
        DatasetCardViewModel dataset,
        ObservableCollection<EditorVersionItem> versionItems)
    {
        var versionNumbers = dataset.GetAllVersionNumbers();

        foreach (var version in versionNumbers)
        {
            var versionPath = dataset.GetVersionFolderPath(version);
            var imageCount = 0;

            if (Directory.Exists(versionPath))
            {
                imageCount = Directory.EnumerateFiles(versionPath)
                    .Count(f => MediaFileExtensions.IsImageFile(f) && !MediaFileExtensions.IsVideoThumbnailFile(f));
            }

            versionItems.Add(EditorVersionItem.Create(version, imageCount));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Loads the images for the selected version.
    /// </summary>
    private Task LoadEditorDatasetImagesAsync()
    {
        EditorDatasetImages.Clear();
        FilteredEditorImages.Clear();

        if (_selectedEditorDataset is null || _selectedEditorVersion is null)
        {
            OnPropertyChanged(nameof(FilterStatusText));
            return Task.CompletedTask;
        }

        if (_selectedEditorDataset.IsTemporary)
        {
            foreach (var image in _temporaryEditorImages)
            {
                EditorDatasetImages.Add(image);
            }

            ApplyFilters();
            return Task.CompletedTask;
        }

        try
        {
            var versionPath = _selectedEditorDataset.GetVersionFolderPath(_selectedEditorVersion.Version);

            if (!Directory.Exists(versionPath))
            {
                OnPropertyChanged(nameof(FilterStatusText));
                return Task.CompletedTask;
            }

            // Load only image files (not videos) for the image editor
            var imageFiles = Directory.EnumerateFiles(versionPath)
                .Where(f => MediaFileExtensions.IsImageFile(f) && !MediaFileExtensions.IsVideoThumbnailFile(f))
                .OrderBy(f => f)
                .ToList();

            foreach (var imagePath in imageFiles)
            {
                var imageVm = DatasetImageViewModel.FromFile(imagePath, _eventAggregator);
                EditorDatasetImages.Add(imageVm);
            }

            // Apply filters to populate FilteredEditorImages
            ApplyFilters();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading images: {ex.Message}";
        }

        return Task.CompletedTask;
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Releases all resources and unsubscribes from events.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases resources used by this ViewModel.
    /// </summary>
    /// <param name="disposing">True if called from Dispose(), false if from finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // Unsubscribe from events to prevent memory leaks
            _eventAggregator.NavigateToImageEditorRequested -= OnNavigateToImageEditorRequested;
            _eventAggregator.ImageSaved -= OnImageSaved;
            _eventAggregator.ImageDeleted -= OnImageDeleted;
            _eventAggregator.ImageRatingChanged -= OnImageRatingChanged;
            _eventAggregator.DatasetCreated -= OnDatasetCreated;
            _eventAggregator.VersionCreated -= OnVersionCreated;
            _eventAggregator.ImageAdded -= OnImageAdded;
            _state.StateChanged -= OnStateChanged;
            _state.Datasets.CollectionChanged -= OnDatasetsCollectionChanged;
        }

        _disposed = true;
    }

    private void InitializeEditorDatasets()
    {
        _editorDatasets.Clear();
        foreach (var dataset in _state.Datasets)
        {
            _editorDatasets.Add(dataset);
        }

        if (_temporaryEditorDataset is not null && !_editorDatasets.Contains(_temporaryEditorDataset))
        {
            _editorDatasets.Add(_temporaryEditorDataset);
        }
    }



    private void OnDatasetsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InitializeEditorDatasets();
    }

    private void PopulateTemporaryVersionItems()
    {
        EditorVersionItems.Clear();
        var imageCount = _temporaryEditorImages.Count;
        var versionItem = EditorVersionItem.Create(1, imageCount);
        EditorVersionItems.Add(versionItem);
        SelectedEditorVersion = versionItem;
    }

    private void LoadTemporaryEditorDataset(
        DatasetCardViewModel dataset,
        IReadOnlyList<DatasetImageViewModel> images,
        DatasetImageViewModel selectedImage)
    {
        _temporaryEditorDataset = dataset;
        _temporaryEditorDataset.IsTemporary = true;
        _temporaryEditorDataset.CurrentVersion = 1;
        _temporaryEditorDataset.TotalVersions = 1;
        _temporaryEditorDataset.ImageCount = images.Count;
        _temporaryEditorDataset.TotalImageCountAllVersions = images.Count;

        if (!_editorDatasets.Contains(_temporaryEditorDataset))
        {
            _editorDatasets.Add(_temporaryEditorDataset);
        }

        _temporaryEditorImages.Clear();
        _temporaryEditorImages.AddRange(images);

        _selectedEditorDataset = _temporaryEditorDataset;
        OnPropertyChanged(nameof(SelectedEditorDataset));
        _state.SelectedEditorDataset = _temporaryEditorDataset;

        PopulateTemporaryVersionItems();

        var selected = _temporaryEditorImages.FirstOrDefault(img =>
            string.Equals(img.ImagePath, selectedImage.ImagePath, StringComparison.OrdinalIgnoreCase))
            ?? _temporaryEditorImages.First();

        foreach (var image in _temporaryEditorImages)
        {
            image.IsEditorSelected = false;
        }

        selected.IsEditorSelected = true;
        _selectedEditorImage = selected;
        OnPropertyChanged(nameof(SelectedEditorImage));
        _state.SelectedEditorImage = selected;
        ImageEditor.SelectedDatasetImage = selected;
        ImageEditor.LoadImage(selected.ImagePath);

        _state.SelectedTabIndex = 1;
        StatusMessage = $"Editing: {selected.FullFileName}";
    }

    #endregion
}
