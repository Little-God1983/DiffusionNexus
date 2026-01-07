using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Image Editor tab in LoraDatasetHelper.
/// Publishes image save and rating change events via <see cref="IDatasetEventAggregator"/>.
/// 
/// <para>
/// <b>Event Integration:</b>
/// This ViewModel publishes the following events:
/// <list type="bullet">
/// <item><see cref="ImageSavedEventArgs"/> - When an image is saved (new or overwritten)</item>
/// <item><see cref="ImageRatingChangedEventArgs"/> - When an image's rating changes</item>
/// </list>
/// </para>
/// </summary>
public partial class ImageEditorViewModel : ObservableObject
{
    private readonly IDatasetEventAggregator? _eventAggregator;

    private string? _currentImagePath;
    private string? _imageFileName;
    private bool _hasImage;
    private string? _statusMessage;
    private int _imageWidth;
    private int _imageHeight;
    private bool _isCropToolActive;
    private int _zoomPercentage = 100;
    private bool _isFitMode = true;
    private int _imageDpi = 72;
    private long _fileSizeBytes;
    private DatasetImageViewModel? _selectedDatasetImage;

    /// <summary>
    /// Path to the currently loaded image.
    /// </summary>
    public string? CurrentImagePath
    {
        get => _currentImagePath;
        set
        {
            if (SetProperty(ref _currentImagePath, value))
            {
                HasImage = !string.IsNullOrEmpty(value);
                ImageFileName = HasImage ? Path.GetFileName(value) : null;
                NotifyCommandsCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// File name of the current image for display.
    /// </summary>
    public string? ImageFileName
    {
        get => _imageFileName;
        private set => SetProperty(ref _imageFileName, value);
    }

    /// <summary>
    /// Whether an image is currently loaded.
    /// </summary>
    public bool HasImage
    {
        get => _hasImage;
        private set
        {
            if (SetProperty(ref _hasImage, value))
            {
                NotifyCommandsCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Status message to display.
    /// </summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// The currently selected DatasetImageViewModel being edited.
    /// Used to access rating and other metadata.
    /// </summary>
    public DatasetImageViewModel? SelectedDatasetImage
    {
        get => _selectedDatasetImage;
        set
        {
            if (SetProperty(ref _selectedDatasetImage, value))
            {
                OnPropertyChanged(nameof(IsApproved));
                OnPropertyChanged(nameof(IsRejected));
                OnPropertyChanged(nameof(IsUnrated));
                OnPropertyChanged(nameof(HasRating));
                NotifyRatingCommandsCanExecuteChanged();
            }
        }
    }

    #region Rating Properties

    /// <summary>Whether the current image is marked as approved/production-ready.</summary>
    public bool IsApproved => _selectedDatasetImage?.IsApproved ?? false;

    /// <summary>Whether the current image is marked as rejected/failed.</summary>
    public bool IsRejected => _selectedDatasetImage?.IsRejected ?? false;

    /// <summary>Whether the current image has not been rated yet.</summary>
    public bool IsUnrated => _selectedDatasetImage?.IsUnrated ?? true;

    /// <summary>Whether the current image has any rating (approved or rejected).</summary>
    public bool HasRating => !IsUnrated;

    #endregion

    /// <summary>Current image width in pixels.</summary>
    public int ImageWidth
    {
        get => _imageWidth;
        set
        {
            if (SetProperty(ref _imageWidth, value))
                OnPropertyChanged(nameof(ImageDimensions));
        }
    }

    /// <summary>Current image height in pixels.</summary>
    public int ImageHeight
    {
        get => _imageHeight;
        set
        {
            if (SetProperty(ref _imageHeight, value))
                OnPropertyChanged(nameof(ImageDimensions));
        }
    }

    /// <summary>Formatted image dimensions for display.</summary>
    public string ImageDimensions => HasImage ? $"{ImageWidth} × {ImageHeight}" : string.Empty;

    /// <summary>Whether the crop tool is currently active.</summary>
    public bool IsCropToolActive
    {
        get => _isCropToolActive;
        set
        {
            if (SetProperty(ref _isCropToolActive, value))
            {
                ToggleCropToolCommand.NotifyCanExecuteChanged();
                ApplyCropCommand.NotifyCanExecuteChanged();
                CancelCropCommand.NotifyCanExecuteChanged();
                StatusMessage = value ? "Crop: Drag to select region. Press C or Enter to apply, Escape to cancel." : null;
            }
        }
    }

    /// <summary>Current zoom percentage (10-1000).</summary>
    public int ZoomPercentage
    {
        get => _zoomPercentage;
        set
        {
            if (SetProperty(ref _zoomPercentage, value))
            {
                OnPropertyChanged(nameof(ZoomPercentageText));
            }
        }
    }

    /// <summary>Formatted zoom percentage for display.</summary>
    public string ZoomPercentageText => $"{ZoomPercentage}%";

    /// <summary>Whether fit mode is active.</summary>
    public bool IsFitMode
    {
        get => _isFitMode;
        set => SetProperty(ref _isFitMode, value);
    }

    /// <summary>Image DPI (dots per inch).</summary>
    public int ImageDpi
    {
        get => _imageDpi;
        set
        {
            if (SetProperty(ref _imageDpi, value))
            {
                OnPropertyChanged(nameof(ImageInfo));
            }
        }
    }

    /// <summary>File size in bytes.</summary>
    public long FileSizeBytes
    {
        get => _fileSizeBytes;
        set
        {
            if (SetProperty(ref _fileSizeBytes, value))
            {
                OnPropertyChanged(nameof(FileSizeText));
                OnPropertyChanged(nameof(ImageInfo));
            }
        }
    }

    /// <summary>Formatted file size for display.</summary>
    public string FileSizeText
    {
        get
        {
            if (FileSizeBytes < 1024)
                return $"{FileSizeBytes} B";
            if (FileSizeBytes < 1024 * 1024)
                return $"{FileSizeBytes / 1024.0:F1} KB";
            return $"{FileSizeBytes / (1024.0 * 1024.0):F1} MB";
        }
    }

    /// <summary>Combined image info for display.</summary>
    public string ImageInfo => HasImage
        ? $"Size: {ImageWidth} × {ImageHeight} px\nResolution: {ImageDpi} DPI\nFile: {FileSizeText}"
        : string.Empty;

    #region Commands

    public IRelayCommand ClearImageCommand { get; }
    public IRelayCommand ResetImageCommand { get; }
    public IRelayCommand ToggleCropToolCommand { get; }
    public IRelayCommand ApplyCropCommand { get; }
    public IRelayCommand CancelCropCommand { get; }
    public IRelayCommand SaveAsNewCommand { get; }
    public IAsyncRelayCommand SaveOverwriteCommand { get; }
    public IRelayCommand ZoomInCommand { get; }
    public IRelayCommand ZoomOutCommand { get; }
    public IRelayCommand ZoomToFitCommand { get; }
    public IRelayCommand ZoomToActualCommand { get; }
    public IRelayCommand MarkApprovedCommand { get; }
    public IRelayCommand MarkRejectedCommand { get; }
    public IRelayCommand ClearRatingCommand { get; }

    #endregion

    #region Events (for View wiring)

    public event EventHandler? ClearRequested;
    public event EventHandler? ResetRequested;
    public event EventHandler? CropToolActivated;
    public event EventHandler? CropToolDeactivated;
    public event EventHandler? ApplyCropRequested;
    public event EventHandler? CancelCropRequested;
    public event EventHandler? SaveAsNewRequested;
    public event Func<Task<bool>>? SaveOverwriteConfirmRequested;
    public event EventHandler? SaveOverwriteRequested;
    public event EventHandler? ZoomInRequested;
    public event EventHandler? ZoomOutRequested;
    public event EventHandler? ZoomToFitRequested;
    public event EventHandler? ZoomToActualRequested;

    /// <summary>
    /// Event raised when an image save completes successfully.
    /// The string parameter contains the saved file path.
    /// </summary>
    public event EventHandler<string>? ImageSaved;

    #endregion

    /// <summary>
    /// Creates a new ImageEditorViewModel with event aggregator integration.
    /// </summary>
    /// <param name="eventAggregator">The event aggregator for publishing events.</param>
    public ImageEditorViewModel(IDatasetEventAggregator? eventAggregator = null)
    {
        _eventAggregator = eventAggregator;

        ClearImageCommand = new RelayCommand(ExecuteClearImage, () => HasImage);
        ResetImageCommand = new RelayCommand(ExecuteResetImage, () => HasImage);
        ToggleCropToolCommand = new RelayCommand(ExecuteToggleCropTool, () => HasImage);
        ApplyCropCommand = new RelayCommand(ExecuteApplyCrop, () => HasImage && IsCropToolActive);
        CancelCropCommand = new RelayCommand(ExecuteCancelCrop, () => IsCropToolActive);
        SaveAsNewCommand = new RelayCommand(ExecuteSaveAsNew, () => HasImage);
        SaveOverwriteCommand = new AsyncRelayCommand(ExecuteSaveOverwriteAsync, () => HasImage);
        ZoomInCommand = new RelayCommand(ExecuteZoomIn, () => HasImage);
        ZoomOutCommand = new RelayCommand(ExecuteZoomOut, () => HasImage);
        ZoomToFitCommand = new RelayCommand(ExecuteZoomToFit, () => HasImage);
        ZoomToActualCommand = new RelayCommand(ExecuteZoomToActual, () => HasImage);
        
        MarkApprovedCommand = new RelayCommand(ExecuteMarkApproved, () => HasImage && _selectedDatasetImage is not null);
        MarkRejectedCommand = new RelayCommand(ExecuteMarkRejected, () => HasImage && _selectedDatasetImage is not null);
        ClearRatingCommand = new RelayCommand(ExecuteClearRating, () => HasImage && _selectedDatasetImage is not null && !IsUnrated);
    }

    /// <summary>Loads an image by path.</summary>
    public void LoadImage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            StatusMessage = "Image file not found.";
            return;
        }

        CurrentImagePath = imagePath;
        
        try
        {
            var fileInfo = new FileInfo(imagePath);
            FileSizeBytes = fileInfo.Length;
            ImageDpi = 72;
            
            using var stream = File.OpenRead(imagePath);
            using var skCodec = SkiaSharp.SKCodec.Create(stream);
            if (skCodec is not null)
            {
                ImageWidth = skCodec.Info.Width;
                ImageHeight = skCodec.Info.Height;
            }
        }
        catch { }
        
        StatusMessage = $"Loaded: {ImageFileName}";
    }

    /// <summary>Updates image dimensions from the editor control.</summary>
    public void UpdateDimensions(int width, int height)
    {
        ImageWidth = width;
        ImageHeight = height;
    }

    /// <summary>Updates zoom info from the editor control.</summary>
    public void UpdateZoomInfo(int percentage, bool isFitMode)
    {
        ZoomPercentage = percentage;
        IsFitMode = isFitMode;
    }

    /// <summary>Updates file info from the editor control.</summary>
    public void UpdateFileInfo(int dpi, long fileSize)
    {
        ImageDpi = dpi;
        FileSizeBytes = fileSize;
    }

    /// <summary>Called when crop is successfully applied.</summary>
    public void OnCropApplied()
    {
        IsCropToolActive = false;
        StatusMessage = "Crop applied successfully.";
    }

    /// <summary>Called when save as new completes successfully.</summary>
    public void OnSaveAsNewCompleted(string newPath)
    {
        StatusMessage = $"Saved as: {Path.GetFileName(newPath)}";
        CurrentImagePath = newPath;

        // Publish event via aggregator
        _eventAggregator?.PublishImageSaved(new ImageSavedEventArgs
        {
            ImagePath = newPath,
            OriginalPath = _currentImagePath
        });

        // Also raise legacy event for backward compatibility
        ImageSaved?.Invoke(this, newPath);
    }

    /// <summary>Called when save overwrite completes successfully.</summary>
    public void OnSaveOverwriteCompleted()
    {
        StatusMessage = $"Saved: {ImageFileName}";

        if (CurrentImagePath is not null)
        {
            // Publish event via aggregator
            _eventAggregator?.PublishImageSaved(new ImageSavedEventArgs
            {
                ImagePath = CurrentImagePath
            });

            // Also raise legacy event
            ImageSaved?.Invoke(this, CurrentImagePath);
        }
    }

    private void ExecuteToggleCropTool()
    {
        IsCropToolActive = !IsCropToolActive;
        if (IsCropToolActive)
            CropToolActivated?.Invoke(this, EventArgs.Empty);
        else
            CropToolDeactivated?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteApplyCrop() => ApplyCropRequested?.Invoke(this, EventArgs.Empty);

    private void ExecuteCancelCrop()
    {
        IsCropToolActive = false;
        CancelCropRequested?.Invoke(this, EventArgs.Empty);
        StatusMessage = "Crop cancelled.";
    }

    private void ExecuteClearImage()
    {
        CurrentImagePath = null;
        SelectedDatasetImage = null;
        ImageWidth = 0;
        ImageHeight = 0;
        IsCropToolActive = false;
        StatusMessage = "Image cleared.";
        ClearRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteResetImage()
    {
        IsCropToolActive = false;
        StatusMessage = "Image reset to original.";
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteSaveAsNew() => SaveAsNewRequested?.Invoke(this, EventArgs.Empty);

    private async Task ExecuteSaveOverwriteAsync()
    {
        if (SaveOverwriteConfirmRequested is not null)
        {
            var confirmed = await SaveOverwriteConfirmRequested.Invoke();
            if (!confirmed)
            {
                StatusMessage = "Save cancelled.";
                return;
            }
        }
        SaveOverwriteRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteZoomIn() => ZoomInRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteZoomOut() => ZoomOutRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteZoomToFit() => ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteZoomToActual() => ZoomToActualRequested?.Invoke(this, EventArgs.Empty);

    #region Rating Command Implementations

    private void ExecuteMarkApproved()
    {
        if (_selectedDatasetImage is null) return;

        var previousRating = _selectedDatasetImage.RatingStatus;
        _selectedDatasetImage.RatingStatus = _selectedDatasetImage.IsApproved
            ? ImageRatingStatus.Unrated
            : ImageRatingStatus.Approved;
        _selectedDatasetImage.SaveRating();

        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(IsRejected));
        OnPropertyChanged(nameof(IsUnrated));
        OnPropertyChanged(nameof(HasRating));
        NotifyRatingCommandsCanExecuteChanged();

        // Publish event via aggregator
        _eventAggregator?.PublishImageRatingChanged(new ImageRatingChangedEventArgs
        {
            Image = _selectedDatasetImage,
            NewRating = _selectedDatasetImage.RatingStatus,
            PreviousRating = previousRating
        });

        StatusMessage = _selectedDatasetImage.IsApproved ? "Marked as Ready" : "Rating cleared";
    }

    private void ExecuteMarkRejected()
    {
        if (_selectedDatasetImage is null) return;

        var previousRating = _selectedDatasetImage.RatingStatus;
        _selectedDatasetImage.RatingStatus = _selectedDatasetImage.IsRejected
            ? ImageRatingStatus.Unrated
            : ImageRatingStatus.Rejected;
        _selectedDatasetImage.SaveRating();

        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(IsRejected));
        OnPropertyChanged(nameof(IsUnrated));
        OnPropertyChanged(nameof(HasRating));
        NotifyRatingCommandsCanExecuteChanged();

        // Publish event via aggregator
        _eventAggregator?.PublishImageRatingChanged(new ImageRatingChangedEventArgs
        {
            Image = _selectedDatasetImage,
            NewRating = _selectedDatasetImage.RatingStatus,
            PreviousRating = previousRating
        });

        StatusMessage = _selectedDatasetImage.IsRejected ? "Marked as Failed" : "Rating cleared";
    }

    private void ExecuteClearRating()
    {
        if (_selectedDatasetImage is null) return;

        var previousRating = _selectedDatasetImage.RatingStatus;
        _selectedDatasetImage.RatingStatus = ImageRatingStatus.Unrated;
        _selectedDatasetImage.SaveRating();

        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(IsRejected));
        OnPropertyChanged(nameof(IsUnrated));
        OnPropertyChanged(nameof(HasRating));
        NotifyRatingCommandsCanExecuteChanged();

        // Publish event via aggregator
        _eventAggregator?.PublishImageRatingChanged(new ImageRatingChangedEventArgs
        {
            Image = _selectedDatasetImage,
            NewRating = ImageRatingStatus.Unrated,
            PreviousRating = previousRating
        });

        StatusMessage = "Rating cleared";
    }

    #endregion

    private void NotifyCommandsCanExecuteChanged()
    {
        ClearImageCommand.NotifyCanExecuteChanged();
        ResetImageCommand.NotifyCanExecuteChanged();
        ToggleCropToolCommand.NotifyCanExecuteChanged();
        ApplyCropCommand.NotifyCanExecuteChanged();
        SaveAsNewCommand.NotifyCanExecuteChanged();
        SaveOverwriteCommand.NotifyCanExecuteChanged();
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
        ZoomToFitCommand.NotifyCanExecuteChanged();
        ZoomToActualCommand.NotifyCanExecuteChanged();
        NotifyRatingCommandsCanExecuteChanged();
    }

    private void NotifyRatingCommandsCanExecuteChanged()
    {
        MarkApprovedCommand.NotifyCanExecuteChanged();
        MarkRejectedCommand.NotifyCanExecuteChanged();
        ClearRatingCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Refreshes the rating display properties after an external rating change.
    /// Called when another component changes the rating of the currently selected image.
    /// </summary>
    public void RefreshRatingDisplay()
    {
        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(IsRejected));
        OnPropertyChanged(nameof(IsUnrated));
        OnPropertyChanged(nameof(HasRating));
        NotifyRatingCommandsCanExecuteChanged();
    }
}
