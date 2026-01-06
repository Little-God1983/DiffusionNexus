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
                ClearImageCommand.NotifyCanExecuteChanged();
                ResetImageCommand.NotifyCanExecuteChanged();
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
        private set => SetProperty(ref _hasImage, value);
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

    public ImageEditorViewModel()
    {
        ClearImageCommand = new RelayCommand(ExecuteClearImage, () => HasImage);
        ResetImageCommand = new RelayCommand(ExecuteResetImage, () => HasImage);
        ToggleCropToolCommand = new RelayCommand(ExecuteToggleCropTool, () => HasImage);
        ApplyCropCommand = new RelayCommand(ExecuteApplyCrop, () => HasImage && IsCropToolActive);
        CancelCropCommand = new RelayCommand(ExecuteCancelCrop, () => IsCropToolActive);
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
    /// Called when crop is successfully applied.
    /// </summary>
    public void OnCropApplied()
    {
        IsCropToolActive = false;
        StatusMessage = "Crop applied successfully.";
        // Update dimensions will be called by the control's ImageChanged event
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
}
