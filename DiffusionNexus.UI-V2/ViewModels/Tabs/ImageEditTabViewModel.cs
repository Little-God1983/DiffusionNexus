using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
public partial class ImageEditTabViewModel : ObservableObject, IDialogServiceAware, IDisposable
{
    private readonly IDatasetEventAggregator _eventAggregator;
    private readonly IDatasetState _state;
    private bool _disposed;

    private DatasetCardViewModel? _selectedEditorDataset;
    private EditorVersionItem? _selectedEditorVersion;
    private DatasetImageViewModel? _selectedEditorImage;

    /// <summary>
    /// Gets or sets the dialog service for showing dialogs.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    #region Observable Properties

    /// <summary>
    /// Selected dataset in the Image Edit tab.
    /// </summary>
    public DatasetCardViewModel? SelectedEditorDataset
    {
        get => _selectedEditorDataset;
        set
        {
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
    public ObservableCollection<DatasetCardViewModel> Datasets => _state.Datasets;

    /// <summary>
    /// Version items for the version dropdown.
    /// </summary>
    public ObservableCollection<EditorVersionItem> EditorVersionItems { get; } = [];

    /// <summary>
    /// Images available in the Image Edit tab for the selected dataset/version.
    /// </summary>
    public ObservableCollection<DatasetImageViewModel> EditorDatasetImages { get; } = [];

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
        IDatasetState state)
    {
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _state = state ?? throw new ArgumentNullException(nameof(state));

        // Create the image editor
        ImageEditor = new ImageEditorViewModel(_eventAggregator);

        // Subscribe to events
        _eventAggregator.NavigateToImageEditorRequested += OnNavigateToImageEditorRequested;
        _eventAggregator.ImageSaved += OnImageSaved;
        _eventAggregator.ImageDeleted += OnImageDeleted;
        _eventAggregator.ImageRatingChanged += OnImageRatingChanged;

        // Subscribe to state changes
        _state.StateChanged += OnStateChanged;

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
        // Find the matching dataset in the Datasets collection
        var editorDataset = Datasets.FirstOrDefault(d =>
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
        // Refresh the thumbnail list when an image is saved
        if (_selectedEditorDataset is not null && _selectedEditorVersion is not null)
        {
            var currentVersionNumber = _selectedEditorVersion.Version;

            // Reload the version items (to update image counts)
            await RefreshVersionItemsAsync(currentVersionNumber);

            // Reload images for the current version
            await LoadEditorDatasetImagesAsync();

            // Find and select the saved image in the list
            var savedImageVm = EditorDatasetImages.FirstOrDefault(img =>
                string.Equals(img.ImagePath, e.ImagePath, StringComparison.OrdinalIgnoreCase));

            if (savedImageVm is not null)
            {
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
        }
    }

    private async void OnImageDeleted(object? sender, ImageDeletedEventArgs e)
    {
        // Refresh the thumbnail list when an image is deleted
        if (_selectedEditorDataset is not null && _selectedEditorVersion is not null)
        {
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

    #endregion

    #region Private Methods

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
        _selectedEditorVersion = null;
        OnPropertyChanged(nameof(SelectedEditorVersion));

        if (_selectedEditorDataset is null) return;

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

        if (_selectedEditorDataset is null || _selectedEditorVersion is null) return Task.CompletedTask;

        try
        {
            var versionPath = _selectedEditorDataset.GetVersionFolderPath(_selectedEditorVersion.Version);

            if (!Directory.Exists(versionPath)) return Task.CompletedTask;

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
            _state.StateChanged -= OnStateChanged;
        }

        _disposed = true;
    }

    #endregion
}
