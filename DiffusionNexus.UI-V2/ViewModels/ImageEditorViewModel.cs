using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Image Editor tab in LoraDatasetHelper.
/// </summary>
public partial class ImageEditorViewModel : ObservableObject
{
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

    /// <summary>
    /// Whether the current image is marked as approved/production-ready.
    /// </summary>
    public bool IsApproved => _selectedDatasetImage?.IsApproved ?? false;

    /// <summary>
    /// Whether the current image is marked as rejected/failed.
    /// </summary>
    public bool IsRejected => _selectedDatasetImage?.IsRejected ?? false;

    /// <summary>
    /// Whether the current image has not been rated yet.
    /// </summary>
    public bool IsUnrated => _selectedDatasetImage?.IsUnrated ?? true;

    /// <summary>
    /// Whether the current image has any rating (approved or rejected).
    /// </summary>
    public bool HasRating => !IsUnrated;

    #endregion

    /// <summary>
    /// Current image width in pixels.
    /// </summary>
    public int ImageWidth
    {
        get => _imageWidth;
        set
        {
            if (SetProperty(ref _imageWidth, value))
                OnPropertyChanged(nameof(ImageDimensions));
        }
    }

    /// <summary>
    /// Current image height in pixels.
    /// </summary>
    public int ImageHeight
    {
        get => _imageHeight;
        set
        {
            if (SetProperty(ref _imageHeight, value))
                OnPropertyChanged(nameof(ImageDimensions));
        }
    }

    /// <summary>
    /// Formatted image dimensions for display.
    /// </summary>
    public string ImageDimensions => HasImage ? $"{ImageWidth} × {ImageHeight}" : string.Empty;

    /// <summary>
    /// Whether the crop tool is currently active.
    /// </summary>
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

    /// <summary>
    /// Current zoom percentage (10-1000).
    /// </summary>
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

    /// <summary>
    /// Formatted zoom percentage for display.
    /// </summary>
    public string ZoomPercentageText => $"{ZoomPercentage}%";

    /// <summary>
    /// Whether fit mode is active.
    /// </summary>
    public bool IsFitMode
    {
        get => _isFitMode;
        set => SetProperty(ref _isFitMode, value);
    }

    /// <summary>
    /// Image DPI (dots per inch).
    /// </summary>
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

    /// <summary>
    /// File size in bytes.
    /// </summary>
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

    /// <summary>
    /// Formatted file size for display.
    /// </summary>
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

    /// <summary>
    /// Combined image info for display.
    /// </summary>
    public string ImageInfo => HasImage
        ? $"Size: {ImageWidth} × {ImageHeight} px\nResolution: {ImageDpi} DPI\nFile: {FileSizeText}"
        : string.Empty;

    #region Commands

    /// <summary>
    /// Command to clear the current image.
    /// </summary>
    public IRelayCommand ClearImageCommand { get; }

    /// <summary>
    /// Command to reset to the original image.
    /// </summary>
    public IRelayCommand ResetImageCommand { get; }

    /// <summary>
    /// Command to toggle the crop tool.
    /// </summary>
    public IRelayCommand ToggleCropToolCommand { get; }

    /// <summary>
    /// Command to apply the current crop.
    /// </summary>
    public IRelayCommand ApplyCropCommand { get; }

    /// <summary>
    /// Command to cancel the current crop.
    /// </summary>
    public IRelayCommand CancelCropCommand { get; }

    /// <summary>
    /// Command to save as a new file.
    /// </summary>
    public IRelayCommand SaveAsNewCommand { get; }

    /// <summary>
    /// Command to save overwriting the original file.
    /// </summary>
    public IAsyncRelayCommand SaveOverwriteCommand { get; }

    /// <summary>
    /// Command to zoom in.
    /// </summary>
    public IRelayCommand ZoomInCommand { get; }

    /// <summary>
    /// Command to zoom out.
    /// </summary>
    public IRelayCommand ZoomOutCommand { get; }

    /// <summary>
    /// Command to zoom to fit.
    /// </summary>
    public IRelayCommand ZoomToFitCommand { get; }

    /// <summary>
    /// Command to zoom to 100%.
    /// </summary>
    public IRelayCommand ZoomToActualCommand { get; }

    /// <summary>
    /// Command to mark the image as approved (production-ready).
    /// </summary>
    public IRelayCommand MarkApprovedCommand { get; }

    /// <summary>
    /// Command to mark the image as rejected (failed).
    /// </summary>
    public IRelayCommand MarkRejectedCommand { get; }

    /// <summary>
    /// Command to clear the rating (set to unrated).
    /// </summary>
    public IRelayCommand ClearRatingCommand { get; }

    #endregion

    #region Events

    /// <summary>
    /// Event raised when image should be cleared in the control.
    /// </summary>
    public event EventHandler? ClearRequested;

    /// <summary>
    /// Event raised when image should be reset in the control.
    /// </summary>
    public event EventHandler? ResetRequested;

    /// <summary>
    /// Event raised when crop tool should be activated in the control.
    /// </summary>
    public event EventHandler? CropToolActivated;

    /// <summary>
    /// Event raised when crop tool should be deactivated in the control.
    /// </summary>
    public event EventHandler? CropToolDeactivated;

    /// <summary>
    /// Event raised when crop should be applied in the control.
    /// </summary>
    public event EventHandler? ApplyCropRequested;

    /// <summary>
    /// Event raised when crop should be cancelled in the control.
    /// </summary>
    public event EventHandler? CancelCropRequested;

    /// <summary>
    /// Event raised when save as new is requested.
    /// </summary>
    public event EventHandler? SaveAsNewRequested;

    /// <summary>
    /// Event raised when save overwrite is requested. Returns true to proceed.
    /// </summary>
    public event Func<Task<bool>>? SaveOverwriteConfirmRequested;

    /// <summary>
    /// Event raised when save overwrite should be executed.
    /// </summary>
    public event EventHandler? SaveOverwriteRequested;

    /// <summary>
    /// Event raised when zoom in is requested.
    /// </summary>
    public event EventHandler? ZoomInRequested;

    /// <summary>
    /// Event raised when zoom out is requested.
    /// </summary>
    public event EventHandler? ZoomOutRequested;

    /// <summary>
    /// Event raised when zoom to fit is requested.
    /// </summary>
    public event EventHandler? ZoomToFitRequested;

    /// <summary>
    /// Event raised when zoom to 100% is requested.
    /// </summary>
    public event EventHandler? ZoomToActualRequested;

    /// <summary>
    /// Event raised when an image save completes successfully.
    /// The string parameter contains the saved file path.
    /// </summary>
    public event EventHandler<string>? ImageSaved;

    #endregion

    public ImageEditorViewModel()
    {
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
        
        // Rating commands
        MarkApprovedCommand = new RelayCommand(ExecuteMarkApproved, () => HasImage && _selectedDatasetImage is not null);
        MarkRejectedCommand = new RelayCommand(ExecuteMarkRejected, () => HasImage && _selectedDatasetImage is not null);
        ClearRatingCommand = new RelayCommand(ExecuteClearRating, () => HasImage && _selectedDatasetImage is not null && !IsUnrated);
    }

    /// <summary>
    /// Loads an image by path.
    /// </summary>
    public void LoadImage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            StatusMessage = "Image file not found.";
            return;
        }

        CurrentImagePath = imagePath;
        
        // Pre-load file info directly so it's available immediately
        // The control will update these values when it loads, but this ensures
        // we have data even if the control's ImageChanged event hasn't fired yet
        try
        {
            var fileInfo = new FileInfo(imagePath);
            FileSizeBytes = fileInfo.Length;
            ImageDpi = 72; // Default DPI, will be updated by control if different
            
            // Try to read image dimensions
            using var stream = File.OpenRead(imagePath);
            using var skCodec = SkiaSharp.SKCodec.Create(stream);
            if (skCodec is not null)
            {
                ImageWidth = skCodec.Info.Width;
                ImageHeight = skCodec.Info.Height;
            }
        }
        catch
        {
            // Ignore errors reading file info - the control will update when it loads
        }
        
        StatusMessage = $"Loaded: {ImageFileName}";
    }

    /// <summary>
    /// Updates image dimensions from the editor control.
    /// </summary>
    public void UpdateDimensions(int width, int height)
    {
        ImageWidth = width;
        ImageHeight = height;
    }

    /// <summary>
    /// Updates zoom info from the editor control.
    /// </summary>
    public void UpdateZoomInfo(int percentage, bool isFitMode)
    {
        ZoomPercentage = percentage;
        IsFitMode = isFitMode;
    }

    /// <summary>
    /// Updates file info from the editor control.
    /// </summary>
    public void UpdateFileInfo(int dpi, long fileSize)
    {
        ImageDpi = dpi;
        FileSizeBytes = fileSize;
    }

    /// <summary>
    /// Called when crop is successfully applied.
    /// </summary>
    public void OnCropApplied()
    {
        IsCropToolActive = false;
        StatusMessage = "Crop applied successfully.";
        // Update dimensions will be called by the control's ImageChanged event
    }

    /// <summary>
    /// Called when save as new completes successfully.
    /// </summary>
    public void OnSaveAsNewCompleted(string newPath)
    {
        StatusMessage = $"Saved as: {Path.GetFileName(newPath)}";

        // Update current image path to the new file
        CurrentImagePath = newPath;

        // Notify that the image was saved
        ImageSaved?.Invoke(this, newPath);
    }

    /// <summary>
    /// Called when save overwrite completes successfully.
    /// </summary>
    public void OnSaveOverwriteCompleted()
    {
        StatusMessage = $"Saved: {ImageFileName}";

        // Notify that the image was saved (path remains the same)
        if (CurrentImagePath is not null)
        {
            ImageSaved?.Invoke(this, CurrentImagePath);
        }
    }

    private void ExecuteToggleCropTool()
    {
        IsCropToolActive = !IsCropToolActive;
        if (IsCropToolActive)
        {
            CropToolActivated?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            CropToolDeactivated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ExecuteApplyCrop()
    {
        ApplyCropRequested?.Invoke(this, EventArgs.Empty);
    }

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

    private void ExecuteSaveAsNew()
    {
        SaveAsNewRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task ExecuteSaveOverwriteAsync()
    {
        // Request confirmation
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

    private void ExecuteZoomIn()
    {
        ZoomInRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteZoomOut()
    {
        ZoomOutRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteZoomToFit()
    {
        ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteZoomToActual()
    {
        ZoomToActualRequested?.Invoke(this, EventArgs.Empty);
    }

    #region Rating Command Implementations

    private void ExecuteMarkApproved()
    {
        if (_selectedDatasetImage is null) return;

        // Toggle: if already approved, clear it
        _selectedDatasetImage.RatingStatus = _selectedDatasetImage.IsApproved
            ? ImageRatingStatus.Unrated
            : ImageRatingStatus.Approved;
        _selectedDatasetImage.SaveRating();

        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(IsRejected));
        OnPropertyChanged(nameof(IsUnrated));
        OnPropertyChanged(nameof(HasRating));
        NotifyRatingCommandsCanExecuteChanged();

        StatusMessage = _selectedDatasetImage.IsApproved ? "Marked as Ready" : "Rating cleared";
    }

    private void ExecuteMarkRejected()
    {
        if (_selectedDatasetImage is null) return;

        // Toggle: if already rejected, clear it
        _selectedDatasetImage.RatingStatus = _selectedDatasetImage.IsRejected
            ? ImageRatingStatus.Unrated
            : ImageRatingStatus.Rejected;
        _selectedDatasetImage.SaveRating();

        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(IsRejected));
        OnPropertyChanged(nameof(IsUnrated));
        OnPropertyChanged(nameof(HasRating));
        NotifyRatingCommandsCanExecuteChanged();

        StatusMessage = _selectedDatasetImage.IsRejected ? "Marked as Failed" : "Rating cleared";
    }

    private void ExecuteClearRating()
    {
        if (_selectedDatasetImage is null) return;

        _selectedDatasetImage.RatingStatus = ImageRatingStatus.Unrated;
        _selectedDatasetImage.SaveRating();

        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(IsRejected));
        OnPropertyChanged(nameof(IsUnrated));
        OnPropertyChanged(nameof(HasRating));
        NotifyRatingCommandsCanExecuteChanged();

        StatusMessage = "Rating cleared";
    }

    #endregion

    /// <summary>
    /// Notifies all commands that depend on HasImage to re-evaluate CanExecute.
    /// </summary>
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

    /// <summary>
    /// Notifies rating commands to re-evaluate CanExecute.
    /// </summary>
    private void NotifyRatingCommandsCanExecuteChanged()
    {
        MarkApprovedCommand.NotifyCanExecuteChanged();
        MarkRejectedCommand.NotifyCanExecuteChanged();
        ClearRatingCommand.NotifyCanExecuteChanged();
    }
}
