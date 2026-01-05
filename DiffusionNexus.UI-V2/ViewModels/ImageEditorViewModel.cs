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
    /// Command to clear the current image.
    /// </summary>
    public IRelayCommand ClearImageCommand { get; }

    /// <summary>
    /// Command to reset to the original image.
    /// </summary>
    public IRelayCommand ResetImageCommand { get; }

    /// <summary>
    /// Event raised when image should be cleared in the control.
    /// </summary>
    public event EventHandler? ClearRequested;

    /// <summary>
    /// Event raised when image should be reset in the control.
    /// </summary>
    public event EventHandler? ResetRequested;

    public ImageEditorViewModel()
    {
        ClearImageCommand = new RelayCommand(ExecuteClearImage, () => HasImage);
        ResetImageCommand = new RelayCommand(ExecuteResetImage, () => HasImage);
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

    private void ExecuteClearImage()
    {
        CurrentImagePath = null;
        ImageWidth = 0;
        ImageHeight = 0;
        StatusMessage = "Image cleared.";
        ClearRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteResetImage()
    {
        StatusMessage = "Image reset to original.";
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }
}
