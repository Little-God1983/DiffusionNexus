using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Image Editor tab in LoraDatasetHelper.
/// Delegates tool-specific concerns to sub-ViewModels and coordinates cross-cutting events.
/// </summary>
public partial class ImageEditorViewModel : ObservableObject
{
    private readonly IDatasetEventAggregator? _eventAggregator;
    private readonly EditorServices _services;

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
    private string _cropResolutionText = string.Empty;
    private bool _cropAspectInverted;

    #region Sub-ViewModels

    /// <summary>Gets the editor services for use by the View layer.</summary>
    public EditorServices Services => _services;

    /// <summary>
    /// Callback provided by the View to save the current editor image to a file path.
    /// Returns true if the save succeeded.
    /// </summary>
    public Func<string, bool>? SaveImageFunc { get; set; }

    /// <summary>
    /// Callback provided by the View to save a layered TIFF to a file path.
    /// Returns true if the save succeeded.
    /// </summary>
    public Func<string, bool>? SaveLayeredTiffFunc { get; set; }

    /// <summary>
    /// Callback provided by the View to show a save-file dialog.
    /// Parameters: title, suggestedFileName, filter. Returns the chosen path, or null if cancelled.
    /// </summary>
    public Func<string, string, string, Task<string?>>? ShowSaveFileDialogFunc { get; set; }

    /// <summary>Sub-ViewModel for the layer panel (layer list, selection, commands).</summary>
    public LayerPanelViewModel LayerPanel { get; }

    /// <summary>Sub-ViewModel for color balance and brightness/contrast tools.</summary>
    public ColorToolsViewModel ColorTools { get; }

    /// <summary>Sub-ViewModel for drawing and shape tools.</summary>
    public DrawingToolsViewModel DrawingTools { get; }

    /// <summary>Sub-ViewModel for background removal tool.</summary>
    public BackgroundRemovalViewModel BackgroundRemoval { get; }

    /// <summary>Sub-ViewModel for background fill tool.</summary>
    public BackgroundFillViewModel BackgroundFill { get; }

    /// <summary>Sub-ViewModel for AI upscaling tool.</summary>
    public UpscalingViewModel Upscaling { get; }

    /// <summary>Sub-ViewModel for inpainting tool.</summary>
    public InpaintingViewModel Inpainting { get; }

    /// <summary>Sub-ViewModel for image rating.</summary>
    public RatingViewModel Rating { get; }

    #endregion

    #region Core Properties

    /// <summary>Path to the currently loaded image.</summary>
    public string? CurrentImagePath
    {
        get => _currentImagePath;
        set
        {
            if (SetProperty(ref _currentImagePath, value))
            {
                HasImage = !string.IsNullOrEmpty(value);
                ImageFileName = HasImage ? Path.GetFileName(value) : null;
                LayerPanel.CurrentImagePath = value;
                NotifyCommandsCanExecuteChanged();
            }
        }
    }

    /// <summary>File name of the current image for display.</summary>
    public string? ImageFileName
    {
        get => _imageFileName;
        private set => SetProperty(ref _imageFileName, value);
    }

    /// <summary>Whether an image is currently loaded.</summary>
    public bool HasImage
    {
        get => _hasImage;
        private set
        {
            if (SetProperty(ref _hasImage, value))
                NotifyCommandsCanExecuteChanged();
        }
    }

    /// <summary>Status message to display.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

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
                if (value)
                    DeactivateOtherTools(ToolIds.Crop);
                ColorTools.IsCropToolActive = value;
                ToggleCropToolCommand.NotifyCanExecuteChanged();
                ApplyCropCommand.NotifyCanExecuteChanged();
                CancelCropCommand.NotifyCanExecuteChanged();
                NotifyToolCommandsCanExecuteChanged();
                StatusMessage = value ? "Crop: Drag to select region. Press C or Enter to apply, Escape to cancel." : null;
            }
        }
    }

    /// <summary>Resolution text for the current crop region.</summary>
    public string CropResolutionText
    {
        get => _cropResolutionText;
        set => SetProperty(ref _cropResolutionText, value);
    }

    /// <summary>Whether the crop aspect ratio buttons are in inverted (H:W) mode.</summary>
    public bool CropAspectInverted
    {
        get => _cropAspectInverted;
        set => SetProperty(ref _cropAspectInverted, value);
    }

    /// <summary>Current zoom percentage (10-1000).</summary>
    public int ZoomPercentage
    {
        get => _zoomPercentage;
        set
        {
            if (SetProperty(ref _zoomPercentage, value))
                OnPropertyChanged(nameof(ZoomPercentageText));
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
                OnPropertyChanged(nameof(ImageInfo));
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

    #endregion

    #region Commands

    public IRelayCommand ClearImageCommand { get; }
    public IRelayCommand ResetImageCommand { get; }
    public IRelayCommand ToggleCropToolCommand { get; }
    public IRelayCommand ApplyCropCommand { get; }
    public IRelayCommand CancelCropCommand { get; }
    public IRelayCommand FitCropCommand { get; }
    public IRelayCommand FillCropCommand { get; }
    public IRelayCommand<string> SetCropAspectRatioCommand { get; }
    public IRelayCommand SwitchCropAspectRatioCommand { get; }
    public IAsyncRelayCommand SaveAsNewCommand { get; }
    public IAsyncRelayCommand SaveOverwriteCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IRelayCommand ZoomInCommand { get; }
    public IRelayCommand ZoomOutCommand { get; }
    public IRelayCommand ZoomToFitCommand { get; }
    public IRelayCommand ZoomToActualCommand { get; }
    public IRelayCommand RotateLeftCommand { get; }
    public IRelayCommand RotateRightCommand { get; }
    public IRelayCommand Rotate180Command { get; }
    public IRelayCommand FlipHorizontalCommand { get; }
    public IRelayCommand FlipVerticalCommand { get; }

    #endregion

    #region Events (for View wiring)

    public event EventHandler? ClearRequested;
    public event EventHandler? ResetRequested;
    public event EventHandler? CropToolActivated;
    public event EventHandler? CropToolDeactivated;
    public event EventHandler? ApplyCropRequested;
    public event EventHandler? CancelCropRequested;
    public event EventHandler? FitCropRequested;
    public event EventHandler? FillCropRequested;
    public event EventHandler<(float W, float H)>? SetCropAspectRatioRequested;
    public event EventHandler? SwitchCropAspectRatioRequested;
    public event Func<Task<SaveAsResult>>? SaveAsDialogRequested;
    public event Func<Task<bool>>? SaveOverwriteConfirmRequested;
    public event EventHandler? ZoomInRequested;
    public event EventHandler? ZoomOutRequested;
    public event EventHandler? ZoomToFitRequested;
    public event EventHandler? ZoomToActualRequested;
    public event EventHandler? RotateLeftRequested;
    public event EventHandler? RotateRightRequested;
    public event EventHandler? Rotate180Requested;
    public event EventHandler? FlipHorizontalRequested;
    public event EventHandler? FlipVerticalRequested;
    public event EventHandler<string>? ImageSaved;

    #endregion

    /// <summary>
    /// Creates a new ImageEditorViewModel with event aggregator integration.
    /// </summary>
    public ImageEditorViewModel(
        IDatasetEventAggregator? eventAggregator = null,
        IBackgroundRemovalService? backgroundRemovalService = null,
        IImageUpscalingService? upscalingService = null,
        IComfyUIWrapperService? comfyUiService = null,
        EditorServices? services = null)
    {
        _eventAggregator = eventAggregator;
        _services = services ?? EditorServiceFactory.Create();

        // Initialize sub-ViewModels
        LayerPanel = new LayerPanelViewModel(() => HasImage);
        ColorTools = new ColorToolsViewModel(() => HasImage, DeactivateOtherTools);
        DrawingTools = new DrawingToolsViewModel(() => HasImage, DeactivateOtherTools);
        BackgroundRemoval = new BackgroundRemovalViewModel(() => HasImage, DeactivateOtherTools, backgroundRemovalService);
        BackgroundFill = new BackgroundFillViewModel(() => HasImage, DeactivateOtherTools);
        Upscaling = new UpscalingViewModel(() => HasImage, () => ImageWidth, () => ImageHeight, DeactivateOtherTools, upscalingService);
        Inpainting = new InpaintingViewModel(() => HasImage, DeactivateOtherTools, comfyUiService, eventAggregator);
        Rating = new RatingViewModel(() => HasImage, eventAggregator);

        WireSubViewModelEvents();

        _services.Viewport.Changed += (_, _) =>
        {
            ZoomPercentage = _services.Viewport.ZoomPercentage;
            IsFitMode = _services.Viewport.IsFitMode;
        };

        _services.Tools.ActiveToolChanged += (_, _) => NotifyToolCommandsCanExecuteChanged();

        // Core commands
        ClearImageCommand = new RelayCommand(ExecuteClearImage, () => HasImage);
        ResetImageCommand = new RelayCommand(ExecuteResetImage, () => HasImage);
        ToggleCropToolCommand = new RelayCommand(ExecuteToggleCropTool, () => HasImage && !ColorTools.IsColorBalancePanelOpen);
        ApplyCropCommand = new RelayCommand(ExecuteApplyCrop, () => HasImage && IsCropToolActive);
        CancelCropCommand = new RelayCommand(ExecuteCancelCrop, () => IsCropToolActive);
        FitCropCommand = new RelayCommand(ExecuteFitCrop, () => HasImage && IsCropToolActive);
        FillCropCommand = new RelayCommand(ExecuteFillCrop, () => HasImage && IsCropToolActive);
        SetCropAspectRatioCommand = new RelayCommand<string>(ExecuteSetCropAspectRatio, _ => HasImage && IsCropToolActive);
        SwitchCropAspectRatioCommand = new RelayCommand(ExecuteSwitchCropAspectRatio, () => HasImage && IsCropToolActive);
        SaveAsNewCommand = new AsyncRelayCommand(ExecuteSaveAsNewAsync, () => HasImage);
        SaveOverwriteCommand = new AsyncRelayCommand(ExecuteSaveOverwriteAsync, () => HasImage);
        ExportCommand = new AsyncRelayCommand(ExecuteExportAsync, () => HasImage);
        ZoomInCommand = new RelayCommand(ExecuteZoomIn, () => HasImage);
        ZoomOutCommand = new RelayCommand(ExecuteZoomOut, () => HasImage);
        ZoomToFitCommand = new RelayCommand(ExecuteZoomToFit, () => HasImage);
        ZoomToActualCommand = new RelayCommand(ExecuteZoomToActual, () => HasImage);
        RotateLeftCommand = new RelayCommand(ExecuteRotateLeft, () => HasImage);
        RotateRightCommand = new RelayCommand(ExecuteRotateRight, () => HasImage);
        Rotate180Command = new RelayCommand(ExecuteRotate180, () => HasImage);
        FlipHorizontalCommand = new RelayCommand(ExecuteFlipHorizontal, () => HasImage);
        FlipVerticalCommand = new RelayCommand(ExecuteFlipVertical, () => HasImage);
    }

    /// <summary>Wires internal coordination events from sub-ViewModels (status, tool state, services).</summary>
    private void WireSubViewModelEvents()
    {
        LayerPanel.SaveCompleted += (_, msg) => StatusMessage = msg;

        ColorTools.ToolStateChanged += (_, _) => NotifyToolCommandsCanExecuteChanged();
        ColorTools.ToolToggled += (_, args) =>
        {
            if (args.IsActive) _services.Tools.Activate(args.ToolId);
            else _services.Tools.Deactivate(args.ToolId);
        };

        DrawingTools.ToolStateChanged += (_, _) => NotifyToolCommandsCanExecuteChanged();
        DrawingTools.StatusMessageChanged += (_, msg) => StatusMessage = msg;
        DrawingTools.ToolToggled += (_, args) =>
        {
            if (args.IsActive) _services.Tools.Activate(args.ToolId);
            else _services.Tools.Deactivate(args.ToolId);
        };

        BackgroundRemoval.ToolStateChanged += (_, _) => NotifyToolCommandsCanExecuteChanged();
        BackgroundRemoval.StatusMessageChanged += (_, msg) => StatusMessage = msg;
        BackgroundRemoval.ToolToggled += (_, args) =>
        {
            if (args.IsActive) _services.Tools.Activate(args.ToolId);
            else _services.Tools.Deactivate(args.ToolId);
        };

        BackgroundFill.ToolStateChanged += (_, _) => NotifyToolCommandsCanExecuteChanged();
        BackgroundFill.StatusMessageChanged += (_, msg) => StatusMessage = msg;
        BackgroundFill.ToolToggled += (_, args) =>
        {
            if (args.IsActive) _services.Tools.Activate(args.ToolId);
            else _services.Tools.Deactivate(args.ToolId);
        };

        Upscaling.ToolStateChanged += (_, _) => NotifyToolCommandsCanExecuteChanged();
        Upscaling.StatusMessageChanged += (_, msg) => StatusMessage = msg;
        Upscaling.ToolToggled += (_, args) =>
        {
            if (args.IsActive) _services.Tools.Activate(args.ToolId);
            else _services.Tools.Deactivate(args.ToolId);
        };

        Inpainting.ToolStateChanged += (_, _) => NotifyToolCommandsCanExecuteChanged();
        Inpainting.StatusMessageChanged += (_, msg) => StatusMessage = msg;
        Inpainting.ToolToggled += (_, args) =>
        {
            if (args.IsActive) _services.Tools.Activate(args.ToolId);
            else _services.Tools.Deactivate(args.ToolId);
        };

        Rating.StatusMessageChanged += (_, msg) => StatusMessage = msg;
    }

    #region Tool Coordination

    /// <summary>Deactivates all tools except the one identified by its <see cref="ToolIds"/> constant.</summary>
    private void DeactivateOtherTools(string exceptToolId)
    {
        if (exceptToolId != ToolIds.Crop && _isCropToolActive)
        {
            _isCropToolActive = false;
            OnPropertyChanged(nameof(IsCropToolActive));
            CropToolDeactivated?.Invoke(this, EventArgs.Empty);
        }

        if (exceptToolId != ToolIds.ColorBalance && exceptToolId != ToolIds.BrightnessContrast)
            ColorTools.CloseAllPanels();

        if (exceptToolId != ToolIds.Drawing)
            DrawingTools.CloseAll();

        if (exceptToolId != ToolIds.BackgroundRemoval)
            BackgroundRemoval.ClosePanel();

        if (exceptToolId != ToolIds.BackgroundFill)
            BackgroundFill.ClosePanel();

        if (exceptToolId != ToolIds.Upscaling)
            Upscaling.ClosePanel();

        if (exceptToolId != ToolIds.Inpainting)
            Inpainting.ClosePanel();
    }

    /// <summary>Closes all active tools and resets their state.</summary>
    private void CloseAllTools()
    {
        if (_isCropToolActive)
        {
            _isCropToolActive = false;
            OnPropertyChanged(nameof(IsCropToolActive));
            CropToolDeactivated?.Invoke(this, EventArgs.Empty);
        }

        ColorTools.CloseAllPanels();
        DrawingTools.CloseAll();
        BackgroundRemoval.ClosePanel();
        BackgroundFill.ClosePanel();
        Upscaling.ClosePanel();
        Inpainting.ClosePanel();
    }

    private void NotifyToolCommandsCanExecuteChanged()
    {
        ToggleCropToolCommand.NotifyCanExecuteChanged();
        ApplyCropCommand.NotifyCanExecuteChanged();
        CancelCropCommand.NotifyCanExecuteChanged();
        FitCropCommand.NotifyCanExecuteChanged();
        FillCropCommand.NotifyCanExecuteChanged();
        SetCropAspectRatioCommand.NotifyCanExecuteChanged();
        SwitchCropAspectRatioCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();

        ColorTools.RefreshCommandStates();
        DrawingTools.RefreshCommandStates();
        LayerPanel.NotifyCommandsCanExecuteChanged();
        BackgroundRemoval.RefreshCommandStates();
        BackgroundFill.RefreshCommandStates();
        Upscaling.RefreshCommandStates();
        Inpainting.RefreshCommandStates();
    }

    private void NotifyCommandsCanExecuteChanged()
    {
        ClearImageCommand.NotifyCanExecuteChanged();
        ResetImageCommand.NotifyCanExecuteChanged();
        SaveAsNewCommand.NotifyCanExecuteChanged();
        SaveOverwriteCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
        ZoomToFitCommand.NotifyCanExecuteChanged();
        ZoomToActualCommand.NotifyCanExecuteChanged();
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        Rotate180Command.NotifyCanExecuteChanged();
        FlipHorizontalCommand.NotifyCanExecuteChanged();
        FlipVerticalCommand.NotifyCanExecuteChanged();

        NotifyToolCommandsCanExecuteChanged();
        Rating.RefreshCommandStates();
    }

    #endregion

    #region Public Methods (View wiring)

    /// <summary>Loads an image from the specified file path.</summary>
    public void LoadImage(string imagePath)
    {
        CurrentImagePath = imagePath;
    }

    /// <summary>Updates the image dimensions displayed in the ViewModel.</summary>
    public void UpdateDimensions(int width, int height)
    {
        ImageWidth = width;
        ImageHeight = height;
        Upscaling.RefreshOutputDimensions();
    }

    /// <summary>Updates file information displayed in the ViewModel.</summary>
    public void UpdateFileInfo(int dpi, long fileSizeBytes)
    {
        ImageDpi = dpi;
        FileSizeBytes = fileSizeBytes;
    }

    /// <summary>Updates zoom information displayed in the ViewModel.</summary>
    public void UpdateZoomInfo(int zoomPercentage, bool isFitMode)
    {
        ZoomPercentage = zoomPercentage;
        IsFitMode = isFitMode;
    }

    /// <summary>Called when crop is applied successfully.</summary>
    public void OnCropApplied()
    {
        IsCropToolActive = false;
        CropResolutionText = string.Empty;
        StatusMessage = "Crop applied";
    }

    /// <summary>Called when color balance is applied successfully.</summary>
    public void OnColorBalanceApplied()
    {
        ColorTools.IsColorBalancePanelOpen = false;
        StatusMessage = "Color balance applied";
    }

    /// <summary>Called when brightness/contrast is applied successfully.</summary>
    public void OnBrightnessContrastApplied()
    {
        ColorTools.IsBrightnessContrastPanelOpen = false;
        StatusMessage = "Brightness/Contrast applied";
    }

    /// <summary>Called when Save As New completes successfully.</summary>
    public void OnSaveAsNewCompleted(string newPath, ImageRatingStatus rating)
    {
        FileLogger.LogEntry($"newPath={newPath}, rating={rating}");

        try
        {
            StatusMessage = $"Saved as: {Path.GetFileName(newPath)}";
            FileLogger.Log("Invoking ImageSaved event...");
            ImageSaved?.Invoke(this, newPath);
            FileLogger.Log("ImageSaved event invoked");

            FileLogger.Log($"Publishing to aggregator (aggregator is {(_eventAggregator is null ? "null" : "valid")})...");
            _eventAggregator?.PublishImageSaved(new ImageSavedEventArgs
            {
                ImagePath = newPath,
                OriginalPath = CurrentImagePath,
                Rating = rating
            });
            FileLogger.Log("Published to aggregator");
        }
        catch (Exception ex)
        {
            FileLogger.LogError("Exception in OnSaveAsNewCompleted", ex);
            throw;
        }

        FileLogger.LogExit();
    }

    /// <summary>Called when Save Overwrite completes successfully.</summary>
    public void OnSaveOverwriteCompleted()
    {
        FileLogger.LogEntry($"CurrentImagePath={CurrentImagePath ?? "(null)"}");

        try
        {
            StatusMessage = "Image saved";
            if (CurrentImagePath is not null)
            {
                FileLogger.Log("Invoking ImageSaved event...");
                ImageSaved?.Invoke(this, CurrentImagePath);
                FileLogger.Log("ImageSaved event invoked");

                FileLogger.Log($"Publishing to aggregator (aggregator is {(_eventAggregator is null ? "null" : "valid")})...");
                _eventAggregator?.PublishImageSaved(new ImageSavedEventArgs
                {
                    ImagePath = CurrentImagePath,
                    OriginalPath = null
                });
                FileLogger.Log("Published to aggregator");
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogError("Exception in OnSaveOverwriteCompleted", ex);
            throw;
        }

        FileLogger.LogExit();
    }

    /// <summary>Called when export completes successfully.</summary>
    public void OnExportCompleted(string exportPath)
    {
        FileLogger.LogEntry($"exportPath={exportPath}");
        StatusMessage = $"Exported to: {Path.GetFileName(exportPath)}";
        FileLogger.LogExit();
    }

    /// <summary>Updates the crop resolution text from the current crop dimensions.</summary>
    public void UpdateCropResolution(int width, int height)
    {
        CropResolutionText = width > 0 && height > 0 ? $"{width} x {height}" : string.Empty;
    }

    /// <summary>
    /// The currently selected DatasetImageViewModel being edited.
    /// Syncs with the Rating sub-ViewModel.
    /// </summary>
    public DatasetImageViewModel? SelectedDatasetImage
    {
        get => Rating.SelectedDatasetImage;
        set => Rating.SelectedDatasetImage = value;
    }

    /// <summary>Refreshes the rating display properties after an external rating change.</summary>
    public void RefreshRatingDisplay() => Rating.RefreshRatingDisplay();

    #endregion

    #region Command Implementations

    private void ExecuteClearImage()
    {
        CloseAllTools();
        CurrentImagePath = null;
        ImageWidth = 0;
        ImageHeight = 0;
        ImageDpi = 72;
        FileSizeBytes = 0;
        SelectedDatasetImage = null;
        ClearRequested?.Invoke(this, EventArgs.Empty);
        StatusMessage = null;
    }

    private void ExecuteResetImage()
    {
        CloseAllTools();
        ResetRequested?.Invoke(this, EventArgs.Empty);
        StatusMessage = "Reset to original";
    }

    private void ExecuteToggleCropTool()
    {
        IsCropToolActive = !IsCropToolActive;
        if (IsCropToolActive)
        {
            _services.Tools.Activate(ToolIds.Crop);
            CropToolActivated?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _services.Tools.Deactivate(ToolIds.Crop);
            CropToolDeactivated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ExecuteApplyCrop() => ApplyCropRequested?.Invoke(this, EventArgs.Empty);

    private void ExecuteCancelCrop()
    {
        IsCropToolActive = false;
        CropResolutionText = string.Empty;
        CancelCropRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteFitCrop() => FitCropRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteFillCrop() => FillCropRequested?.Invoke(this, EventArgs.Empty);

    private void ExecuteSetCropAspectRatio(string? ratio)
    {
        if (string.IsNullOrWhiteSpace(ratio)) return;
        var parts = ratio.Split(':');
        if (parts.Length != 2 || !float.TryParse(parts[0], out var w) || !float.TryParse(parts[1], out var h))
            return;
        if (_cropAspectInverted) (w, h) = (h, w);
        SetCropAspectRatioRequested?.Invoke(this, (w, h));
    }

    private void ExecuteSwitchCropAspectRatio()
    {
        CropAspectInverted = !CropAspectInverted;
        SwitchCropAspectRatioRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task ExecuteSaveAsNewAsync()
    {
        if (SaveAsDialogRequested is null || SaveImageFunc is null) return;

        var result = await SaveAsDialogRequested.Invoke();
        if (result.IsCancelled || string.IsNullOrWhiteSpace(result.FileName) || CurrentImagePath is null)
            return;

        var newPath = ResolveSavePath(result);
        if (newPath is null) return;

        if (File.Exists(newPath))
        {
            var extension = result.Destination == SaveAsDestination.LayeredTiff
                ? ".tif"
                : Path.GetExtension(CurrentImagePath);
            StatusMessage = $"File '{result.FileName}{extension}' already exists.";
            return;
        }

        try
        {
            bool saved;
            if (result.Destination == SaveAsDestination.LayeredTiff)
            {
                if (SaveLayeredTiffFunc is null)
                {
                    StatusMessage = "Layered TIFF export is not available.";
                    return;
                }
                saved = SaveLayeredTiffFunc(newPath);
            }
            else
            {
                saved = SaveImageFunc(newPath);
            }

            if (saved)
            {
                if (result.Destination != SaveAsDestination.LayeredTiff)
                {
                    SaveRatingToFile(newPath, result.Rating);
                }
                OnSaveAsNewCompleted(newPath, result.Rating);
            }
            else
            {
                StatusMessage = "Failed to save image.";
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogError("Exception during save", ex);
            StatusMessage = $"Error saving image: {ex.Message}";
        }
    }

    private async Task ExecuteSaveOverwriteAsync()
    {
        if (SaveImageFunc is null) return;

        if (SaveOverwriteConfirmRequested is not null)
        {
            var confirmed = await SaveOverwriteConfirmRequested.Invoke();
            if (!confirmed) return;
        }

        try
        {
            if (SaveImageFunc(CurrentImagePath!))
                OnSaveOverwriteCompleted();
            else
                StatusMessage = "Failed to save image.";
        }
        catch (Exception ex)
        {
            FileLogger.LogError("Exception during save overwrite", ex);
            StatusMessage = $"Error saving image: {ex.Message}";
        }
    }

    private async Task ExecuteExportAsync()
    {
        if (CurrentImagePath is null || SaveImageFunc is null || ShowSaveFileDialogFunc is null) return;

        var extension = Path.GetExtension(CurrentImagePath);
        var fileName = Path.GetFileNameWithoutExtension(CurrentImagePath);
        var suggestedName = $"{fileName}_export{extension}";

        var exportPath = await ShowSaveFileDialogFunc("Export Image", suggestedName, $"*{extension}");
        if (string.IsNullOrEmpty(exportPath)) return;

        try
        {
            if (SaveImageFunc(exportPath))
                OnExportCompleted(exportPath);
            else
                StatusMessage = "Failed to export image.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error exporting image: {ex.Message}";
        }
    }

    /// <summary>Resolves the full save path from a SaveAsResult, creating directories as needed.</summary>
    private string? ResolveSavePath(SaveAsResult result)
    {
        var extension = result.Destination == SaveAsDestination.LayeredTiff
            ? ".tif"
            : Path.GetExtension(CurrentImagePath);

        if (result.Destination is SaveAsDestination.OriginFolder)
        {
            var directory = Path.GetDirectoryName(CurrentImagePath);
            if (string.IsNullOrEmpty(directory))
            {
                StatusMessage = "Cannot determine save location.";
                return null;
            }
            return Path.Combine(directory, result.FileName + extension);
        }

        if (result.Destination is SaveAsDestination.CustomFolder or SaveAsDestination.LayeredTiff)
        {
            var folderPath = result.CustomFolderPath;
            if (string.IsNullOrEmpty(folderPath))
            {
                StatusMessage = "No folder selected.";
                return null;
            }

            try
            {
                if (!Directory.Exists(folderPath))
                    Directory.CreateDirectory(folderPath);
            }
            catch (Exception ex)
            {
                FileLogger.LogError($"Failed to create directory: {folderPath}", ex);
                StatusMessage = "Failed to create directory.";
                return null;
            }

            return Path.Combine(folderPath, result.FileName + extension);
        }

        var dataset = result.SelectedDataset;
        if (dataset is null)
        {
            StatusMessage = "No dataset selected.";
            return null;
        }

        var version = result.SelectedVersion ?? 1;
        var datasetFolderPath = dataset.GetVersionFolderPath(version);

        try
        {
            if (!Directory.Exists(datasetFolderPath))
                Directory.CreateDirectory(datasetFolderPath);
        }
        catch (Exception ex)
        {
            FileLogger.LogError($"Failed to create dataset directory: {datasetFolderPath}", ex);
            StatusMessage = "Failed to create dataset directory.";
            return null;
        }

        return Path.Combine(datasetFolderPath, result.FileName + extension);
    }

    /// <summary>Saves the rating to a .rating file next to the image.</summary>
    private static void SaveRatingToFile(string imagePath, ImageRatingStatus rating)
    {
        try
        {
            var ratingFilePath = Path.ChangeExtension(imagePath, ".rating");

            if (rating == ImageRatingStatus.Unrated)
            {
                if (File.Exists(ratingFilePath))
                    File.Delete(ratingFilePath);
            }
            else
            {
                File.WriteAllText(ratingFilePath, rating.ToString());
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ExecuteZoomIn() { _services.Viewport.ZoomIn(); ZoomInRequested?.Invoke(this, EventArgs.Empty); }
    private void ExecuteZoomOut() { _services.Viewport.ZoomOut(); ZoomOutRequested?.Invoke(this, EventArgs.Empty); }
    private void ExecuteZoomToFit() { _services.Viewport.ZoomToFit(); ZoomToFitRequested?.Invoke(this, EventArgs.Empty); }
    private void ExecuteZoomToActual() { _services.Viewport.ZoomToActual(); ZoomToActualRequested?.Invoke(this, EventArgs.Empty); }
    private void ExecuteRotateLeft() => RotateLeftRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteRotateRight() => RotateRightRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteRotate180() => Rotate180Requested?.Invoke(this, EventArgs.Empty);
    private void ExecuteFlipHorizontal() => FlipHorizontalRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteFlipVertical() => FlipVerticalRequested?.Invoke(this, EventArgs.Empty);

    #endregion
}

/// <summary>Event arguments for the "Generate and Compare" inpainting result.</summary>
public class InpaintCompareEventArgs : EventArgs
{
    /// <summary>Path to the "before" image (flattened original before inpainting).</summary>
    public required string BeforeImagePath { get; init; }

    /// <summary>Path to the "after" image (inpainting result).</summary>
    public required string AfterImagePath { get; init; }
}
