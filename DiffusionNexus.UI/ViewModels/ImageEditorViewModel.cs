using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.Domain.Services;
using Avalonia.Media;

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
    private readonly IBackgroundRemovalService? _backgroundRemovalService;
    private readonly IImageUpscalingService? _upscalingService;
    private readonly IComfyUIWrapperService? _comfyUiService;
    private readonly EditorServices _services;

    /// <summary>
    /// Gets the editor services for use by the View layer during migration.
    /// Services provide decoupled access to viewport, tools, layers, and document operations.
    /// </summary>
    public EditorServices Services => _services;

    /// <summary>
    /// Sub-ViewModel for the layer panel (layer list, selection, commands).
    /// </summary>
    public LayerPanelViewModel LayerPanel { get; }

    /// <summary>
    /// Sub-ViewModel for color balance and brightness/contrast tools.
    /// </summary>
    public ColorToolsViewModel ColorTools { get; }

    /// <summary>
    /// Sub-ViewModel for drawing and shape tools.
    /// </summary>
    public DrawingToolsViewModel DrawingTools { get; }

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

    // Background Removal fields
    private bool _isBackgroundRemovalPanelOpen;
    private bool _isBackgroundRemovalBusy;
    private string? _backgroundRemovalStatus;
    private int _backgroundRemovalProgress;

    // Background Fill fields
    private bool _isBackgroundFillPanelOpen;
    private byte _backgroundFillRed = 255;
    private byte _backgroundFillGreen = 255;
    private byte _backgroundFillBlue = 255;

    // AI Upscaling fields
    private bool _isUpscalingPanelOpen;
    private bool _isUpscalingBusy;
    private string? _upscalingStatus;
    private int _upscalingProgress;
    private float _upscaleTargetScale = 2.0f;

    // Inpainting fields
    private bool _isInpaintingPanelOpen;
    private float _inpaintBrushSize = 40f;
    private float _inpaintMaskFeather = 10f;
    private float _inpaintDenoise = 1.0f;
    private bool _isInpaintingBusy;
    private string? _pendingCompareBeforeImagePath;
    private string? _inpaintingStatus;
    private string _inpaintPositivePrompt = string.Empty;
    private Avalonia.Media.Imaging.Bitmap? _inpaintBaseThumbnail;
    private bool _hasInpaintBase;
    private string _inpaintNegativePrompt = DefaultInpaintNegativePrompt;
    private const string DefaultInpaintNegativePrompt = "blurry, low quality, artifacts, distorted, deformed, ugly, bad anatomy, watermark, text";

    // Crop aspect ratio fields
    private string _cropResolutionText = string.Empty;
    private bool _cropAspectInverted;

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
                LayerPanel.CurrentImagePath = value;
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

    #region Background Removal Properties

    /// <summary>Whether the background removal panel is open.</summary>
    public bool IsBackgroundRemovalPanelOpen
    {
        get => _isBackgroundRemovalPanelOpen;
        set
        {
            if (SetProperty(ref _isBackgroundRemovalPanelOpen, value))
            {
                if (value)
                {
                    // Deactivate other tools when background removal is activated
                    DeactivateOtherTools(nameof(IsBackgroundRemovalPanelOpen));
                }
                RemoveBackgroundCommand.NotifyCanExecuteChanged();
                DownloadBackgroundRemovalModelCommand.NotifyCanExecuteChanged();
                NotifyToolCommandsCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether background removal is currently in progress.</summary>
    public bool IsBackgroundRemovalBusy
    {
        get => _isBackgroundRemovalBusy;
        private set
        {
            if (SetProperty(ref _isBackgroundRemovalBusy, value))
            {
                RemoveBackgroundCommand.NotifyCanExecuteChanged();
                DownloadBackgroundRemovalModelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Status message for background removal operations.</summary>
    public string? BackgroundRemovalStatus
    {
        get => _backgroundRemovalStatus;
        private set => SetProperty(ref _backgroundRemovalStatus, value);
    }

    /// <summary>Progress percentage for model download (0-100).</summary>
    public int BackgroundRemovalProgress
    {
        get => _backgroundRemovalProgress;
        private set => SetProperty(ref _backgroundRemovalProgress, value);
    }

    /// <summary>Whether the background removal model is ready for use.</summary>
    public bool IsBackgroundRemovalModelReady => 
        _backgroundRemovalService?.GetModelStatus() == ModelStatus.Ready;

    /// <summary>Whether the background removal model needs to be downloaded.</summary>
    public bool IsBackgroundRemovalModelMissing
    {
        get
        {
            var status = _backgroundRemovalService?.GetModelStatus() ?? ModelStatus.NotDownloaded;
            return status == ModelStatus.NotDownloaded || status == ModelStatus.Corrupted;
        }
    }

    /// <summary>Whether GPU acceleration is available for background removal.</summary>
    public bool IsBackgroundRemovalGpuAvailable => _backgroundRemovalService?.IsGpuAvailable ?? false;

    /// <summary>Refreshes the background removal model status properties.</summary>
    public void RefreshBackgroundRemovalModelStatus()
    {
        OnPropertyChanged(nameof(IsBackgroundRemovalModelReady));
        OnPropertyChanged(nameof(IsBackgroundRemovalModelMissing));
        RemoveBackgroundCommand.NotifyCanExecuteChanged();
        DownloadBackgroundRemovalModelCommand.NotifyCanExecuteChanged();
        
        // Clear attention flag when model becomes ready
        if (IsBackgroundRemovalModelReady)
        {
        }
    }

    #endregion

    #region Background Fill Properties

    /// <summary>Whether the background fill panel is open.</summary>
    public bool IsBackgroundFillPanelOpen
    {
        get => _isBackgroundFillPanelOpen;
        set
        {
            if (SetProperty(ref _isBackgroundFillPanelOpen, value))
            {
                if (value)
                {
                    // Deactivate other tools when background fill is activated
                    DeactivateOtherTools(nameof(IsBackgroundFillPanelOpen));
                    // Request initial preview
                    RequestBackgroundFillPreview();
                }
                else
                {
                    // Cancel preview when panel closes
                    CancelBackgroundFillPreviewRequested?.Invoke(this, EventArgs.Empty);
                }
                ApplyBackgroundFillCommand.NotifyCanExecuteChanged();
                NotifyToolCommandsCanExecuteChanged();
            }
        }
    }

    /// <summary>Red component of the fill color (0-255).</summary>
    public byte BackgroundFillRed
    {
        get => _backgroundFillRed;
        set
        {
            if (SetProperty(ref _backgroundFillRed, value))
            {
                OnPropertyChanged(nameof(BackgroundFillColor));
                OnPropertyChanged(nameof(BackgroundFillColorHex));
                RequestBackgroundFillPreview();
            }
        }
    }

    /// <summary>Green component of the fill color (0-255).</summary>
    public byte BackgroundFillGreen
    {
        get => _backgroundFillGreen;
        set
        {
            if (SetProperty(ref _backgroundFillGreen, value))
            {
                OnPropertyChanged(nameof(BackgroundFillColor));
                OnPropertyChanged(nameof(BackgroundFillColorHex));
                RequestBackgroundFillPreview();
            }
        }
    }

    /// <summary>Blue component of the fill color (0-255).</summary>
    public byte BackgroundFillBlue
    {
        get => _backgroundFillBlue;
        set
        {
            if (SetProperty(ref _backgroundFillBlue, value))
            {
                OnPropertyChanged(nameof(BackgroundFillColor));
                OnPropertyChanged(nameof(BackgroundFillColorHex));
                RequestBackgroundFillPreview();
            }
        }
    }

    /// <summary>The current fill color as an Avalonia Color.</summary>
    public Avalonia.Media.Color BackgroundFillColor
    {
        get => Avalonia.Media.Color.FromRgb(_backgroundFillRed, _backgroundFillGreen, _backgroundFillBlue);
        set
        {
            if (_backgroundFillRed != value.R || _backgroundFillGreen != value.G || _backgroundFillBlue != value.B)
            {
                _backgroundFillRed = value.R;
                _backgroundFillGreen = value.G;
                _backgroundFillBlue = value.B;
                OnPropertyChanged(nameof(BackgroundFillRed));
                OnPropertyChanged(nameof(BackgroundFillGreen));
                OnPropertyChanged(nameof(BackgroundFillBlue));
                OnPropertyChanged(nameof(BackgroundFillColor));
                OnPropertyChanged(nameof(BackgroundFillColorHex));
                RequestBackgroundFillPreview();
            }
        }
    }

    /// <summary>Hex string representation of the fill color.</summary>
    public string BackgroundFillColorHex => $"#{_backgroundFillRed:X2}{_backgroundFillGreen:X2}{_backgroundFillBlue:X2}";

    /// <summary>Gets the current background fill settings.</summary>
    public ImageEditor.BackgroundFillSettings CurrentBackgroundFillSettings =>
        new(_backgroundFillRed, _backgroundFillGreen, _backgroundFillBlue);

    private void RequestBackgroundFillPreview()
    {
        if (IsBackgroundFillPanelOpen && HasImage)
        {
            BackgroundFillPreviewRequested?.Invoke(this, CurrentBackgroundFillSettings);
        }
    }

    /// <summary>Sets the fill color from a preset.</summary>
    public void SetBackgroundFillPreset(string? preset)
    {
        if (preset is null) return;
        
        var settings = preset.ToUpperInvariant() switch
        {
            "WHITE" => ImageEditor.BackgroundFillSettings.Presets.White,
            "BLACK" => ImageEditor.BackgroundFillSettings.Presets.Black,
            "GRAY" or "GREY" => ImageEditor.BackgroundFillSettings.Presets.Gray,
            "GREEN" => ImageEditor.BackgroundFillSettings.Presets.Green,
            "BLUE" => ImageEditor.BackgroundFillSettings.Presets.Blue,
            _ => null
        };

        if (settings is not null)
        {
            BackgroundFillRed = settings.Red;
            BackgroundFillGreen = settings.Green;
            BackgroundFillBlue = settings.Blue;
        }
    }

    #endregion

    #region AI Upscaling Properties

    /// <summary>Whether the AI upscaling panel is open.</summary>
    public bool IsUpscalingPanelOpen
    {
        get => _isUpscalingPanelOpen;
        set
        {
            if (SetProperty(ref _isUpscalingPanelOpen, value))
            {
                if (value)
                {
                    // Deactivate other tools when upscaling is activated
                    DeactivateOtherTools(nameof(IsUpscalingPanelOpen));
                }
                UpscaleImageCommand.NotifyCanExecuteChanged();
                DownloadUpscalingModelCommand.NotifyCanExecuteChanged();
                NotifyToolCommandsCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether AI upscaling is currently in progress.</summary>
    public bool IsUpscalingBusy
    {
        get => _isUpscalingBusy;
        private set
        {
            if (SetProperty(ref _isUpscalingBusy, value))
            {
                UpscaleImageCommand.NotifyCanExecuteChanged();
                DownloadUpscalingModelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Status message for upscaling operations.</summary>
    public string? UpscalingStatus
    {
        get => _upscalingStatus;
        private set => SetProperty(ref _upscalingStatus, value);
    }

    /// <summary>Progress percentage for upscaling/download (0-100).</summary>
    public int UpscalingProgress
    {
        get => _upscalingProgress;
        private set => SetProperty(ref _upscalingProgress, value);
    }

    /// <summary>Target scale factor for upscaling (1.1 to 4.0).</summary>
    public float UpscaleTargetScale
    {
        get => _upscaleTargetScale;
        set
        {
            var clamped = Math.Clamp(value, 1.1f, 4.0f);
            if (SetProperty(ref _upscaleTargetScale, clamped))
            {
                OnPropertyChanged(nameof(UpscaleTargetScaleText));
                OnPropertyChanged(nameof(UpscaleOutputDimensions));
            }
        }
    }

    /// <summary>Formatted target scale for display.</summary>
    public string UpscaleTargetScaleText => $"{_upscaleTargetScale:F1}x";

    /// <summary>Predicted output dimensions based on current scale.</summary>
    public string UpscaleOutputDimensions
    {
        get
        {
            if (!HasImage || ImageWidth == 0 || ImageHeight == 0)
                return "N/A";
            var targetWidth = (int)Math.Round(ImageWidth * _upscaleTargetScale);
            var targetHeight = (int)Math.Round(ImageHeight * _upscaleTargetScale);
            return $"{targetWidth} � {targetHeight} px";
        }
    }

    /// <summary>Whether the upscaling model is ready for use.</summary>
    public bool IsUpscalingModelReady => 
        _upscalingService?.GetModelStatus() == ModelStatus.Ready;

    /// <summary>Whether the upscaling model needs to be downloaded.</summary>
    public bool IsUpscalingModelMissing
    {
        get
        {
            var status = _upscalingService?.GetModelStatus() ?? ModelStatus.NotDownloaded;
            return status == ModelStatus.NotDownloaded || status == ModelStatus.Corrupted;
        }
    }

    /// <summary>Whether GPU acceleration is available for upscaling.</summary>
    public bool IsUpscalingGpuAvailable => _upscalingService?.IsGpuAvailable ?? false;

    /// <summary>Refreshes the upscaling model status properties.</summary>
    public void RefreshUpscalingModelStatus()
    {
        OnPropertyChanged(nameof(IsUpscalingModelReady));
        OnPropertyChanged(nameof(IsUpscalingModelMissing));
        OnPropertyChanged(nameof(IsUpscalingGpuAvailable));
        UpscaleImageCommand.NotifyCanExecuteChanged();
        DownloadUpscalingModelCommand.NotifyCanExecuteChanged();
    }

    #endregion

    #region Inpainting Properties

    private const string InpaintWorkflowPath = "Assets/Workflows/Inpaint-Qwen-2512.json";
    private const string InpaintLoadImageNodeId = "16";
    private const string InpaintPositivePromptNodeId = "5";
    private const string InpaintNegativePromptNodeId = "8";
    private const string InpaintKSamplerNodeId = "11";

    /// <summary>Whether the inpainting panel is open.</summary>
    public bool IsInpaintingPanelOpen
    {
        get => _isInpaintingPanelOpen;
        set
        {
            if (SetProperty(ref _isInpaintingPanelOpen, value))
            {
                if (value)
                {
                    // Deactivate other tools when inpainting is activated
                    DeactivateOtherTools(nameof(IsInpaintingPanelOpen));
                    StatusMessage = "Inpaint: Paint over areas to mark them for AI regeneration.";

                    // Auto-capture the current state as the inpaint base
                    if (!HasInpaintBase)
                    {
                        SetInpaintBaseRequested?.Invoke(this, EventArgs.Empty);
                    }
                }
                else
                {
                    StatusMessage = null;
                }
                InpaintToolActivated?.Invoke(this, value);
                ClearInpaintMaskCommand.NotifyCanExecuteChanged();
                NotifyToolCommandsCanExecuteChanged();
            }
        }
    }

    /// <summary>Inpainting brush size in display pixels (1-200).</summary>
    public float InpaintBrushSize
    {
        get => _inpaintBrushSize;
        set
        {
            var clamped = Math.Clamp(value, 1f, 200f);
            if (SetProperty(ref _inpaintBrushSize, clamped))
            {
                OnPropertyChanged(nameof(InpaintBrushSizeText));
                InpaintSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Formatted inpaint brush size for display.</summary>
    public string InpaintBrushSizeText => $"{(int)_inpaintBrushSize} px";

    /// <summary>Mask feather radius (0-20). Controls how softly the mask edges blend with the original image.</summary>
    public float InpaintMaskFeather
    {
        get => _inpaintMaskFeather;
        set
        {
            var clamped = Math.Clamp(value, 0f, 50f);
            if (SetProperty(ref _inpaintMaskFeather, clamped))
            {
                OnPropertyChanged(nameof(InpaintMaskFeatherText));
            }
        }
    }

    /// <summary>Formatted mask feather value for display.</summary>
    public string InpaintMaskFeatherText => _inpaintMaskFeather < 0.5f ? "Off" : $"{_inpaintMaskFeather:F0} px";

    /// <summary>Denoise strength (0.0-1.0). Controls how much of the masked area is regenerated. 1.0 = full replacement, lower = preserve more of the original.</summary>
    public float InpaintDenoise
    {
        get => _inpaintDenoise;
        set
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            if (SetProperty(ref _inpaintDenoise, clamped))
            {
                OnPropertyChanged(nameof(InpaintDenoiseText));
            }
        }
    }

    /// <summary>Formatted denoise value for display.</summary>
    public string InpaintDenoiseText => $"{_inpaintDenoise:F2}";

    /// <summary>Positive prompt describing what to generate in the masked areas.</summary>
    public string InpaintPositivePrompt
    {
        get => _inpaintPositivePrompt;
        set => SetProperty(ref _inpaintPositivePrompt, value ?? string.Empty);
    }

    /// <summary>Negative prompt for inpainting. Pre-filled with a sensible default.</summary>
    public string InpaintNegativePrompt
    {
        get => _inpaintNegativePrompt;
        set => SetProperty(ref _inpaintNegativePrompt, value ?? string.Empty);
    }

    /// <summary>Whether an inpainting operation is currently in progress.</summary>
    public bool IsInpaintingBusy
    {
        get => _isInpaintingBusy;
        private set => SetProperty(ref _isInpaintingBusy, value);
    }

    /// <summary>Status message for inpainting operations.</summary>
    public string? InpaintingStatus
    {
        get => _inpaintingStatus;
        private set => SetProperty(ref _inpaintingStatus, value);
    }

    /// <summary>Command to clear the inpaint mask layer.</summary>
    public RelayCommand ClearInpaintMaskCommand { get; private set; } = null!;

    /// <summary>Command to generate inpainting via ComfyUI.</summary>
    public IAsyncRelayCommand GenerateInpaintCommand { get; private set; } = null!;

    /// <summary>Command to generate inpainting and open both images in the Image Comparer.</summary>
    public IAsyncRelayCommand GenerateAndCompareInpaintCommand { get; private set; } = null!;

    /// <summary>Command to capture the current flattened state as the inpaint base.</summary>
    public RelayCommand UseCurrentAsInpaintBaseCommand { get; private set; } = null!;

    /// <summary>Whether an inpaint base image has been captured.</summary>
    public bool HasInpaintBase
    {
        get => _hasInpaintBase;
        private set => SetProperty(ref _hasInpaintBase, value);
    }

    /// <summary>Thumbnail preview of the current inpaint base image.</summary>
    public Avalonia.Media.Imaging.Bitmap? InpaintBaseThumbnail
    {
        get => _inpaintBaseThumbnail;
        private set => SetProperty(ref _inpaintBaseThumbnail, value);
    }

    /// <summary>
    /// Event raised when the ViewModel requests capturing the current state as the inpaint base.
    /// </summary>
    public event EventHandler? SetInpaintBaseRequested;

    /// <summary>
    /// Updates the inpaint base thumbnail after the EditorCore captures the base bitmap.
    /// Called by the View after wiring SetInpaintBaseRequested.
    /// </summary>
    public void UpdateInpaintBaseThumbnail(Avalonia.Media.Imaging.Bitmap? thumbnail)
    {
        var old = _inpaintBaseThumbnail;
        InpaintBaseThumbnail = thumbnail;
        HasInpaintBase = thumbnail is not null;
        if (old is not null && !ReferenceEquals(old, thumbnail))
        {
            old.Dispose();
        }
    }

    /// <summary>
    /// Event raised when the inpainting tool is activated or deactivated.
    /// The bool parameter indicates whether the tool is now active.
    /// </summary>
    public event EventHandler<bool>? InpaintToolActivated;

    /// <summary>
    /// Event raised when inpaint brush settings (size) change.
    /// </summary>
    public event EventHandler? InpaintSettingsChanged;

    /// <summary>
    /// Event raised when the ViewModel requests clearing the inpaint mask.
    /// </summary>
    public event EventHandler? ClearInpaintMaskRequested;

    /// <summary>
    /// Event raised when the user requests inpaint generation.
    /// The View should gather image + mask data and call ProcessInpaintAsync.
    /// </summary>
    public event EventHandler? GenerateInpaintRequested;

    /// <summary>
    /// Event raised when the inpaint result image bytes are ready.
    /// The View should apply the result to a new layer.
    /// </summary>
    public event EventHandler<byte[]>? InpaintResultReady;

    /// <summary>
    /// Event raised when a "Generate and Compare" inpainting completes.
    /// Contains the before and after temp image paths for the Image Comparer.
    /// </summary>
    public event EventHandler<InpaintCompareEventArgs>? InpaintCompareRequested;

    // TODO: Linux Implementation for Inpainting

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
    public string ImageDimensions => HasImage ? $"{ImageWidth} � {ImageHeight}" : string.Empty;

    /// <summary>Whether the crop tool is currently active.</summary>
    public bool IsCropToolActive
    {
        get => _isCropToolActive;
        set
        {
            if (SetProperty(ref _isCropToolActive, value))
            {
                if (value)
                {
                    // Deactivate other tools when crop is activated
                    DeactivateOtherTools(nameof(IsCropToolActive));
                }
                ColorTools.IsCropToolActive = value;
                ToggleCropToolCommand.NotifyCanExecuteChanged();
                ApplyCropCommand.NotifyCanExecuteChanged();
                CancelCropCommand.NotifyCanExecuteChanged();
                NotifyToolCommandsCanExecuteChanged();
                StatusMessage = value ? "Crop: Drag to select region. Press C or Enter to apply, Escape to cancel." : null;
            }
        }
    }

    /// <summary>Resolution text for the current crop region (e.g., "1920 x 1080").</summary>
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
        ? $"Size: {ImageWidth} � {ImageHeight} px\nResolution: {ImageDpi} DPI\nFile: {FileSizeText}"
        : string.Empty;

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
    public IRelayCommand MarkApprovedCommand { get; }
    public IRelayCommand MarkRejectedCommand { get; }
    public IRelayCommand ClearRatingCommand { get; }
    public IRelayCommand RotateLeftCommand { get; }
    public IRelayCommand RotateRightCommand { get; }
    public IRelayCommand Rotate180Command { get; }
    public IRelayCommand FlipHorizontalCommand { get; }
    public IRelayCommand FlipVerticalCommand { get; }
    public IAsyncRelayCommand RemoveBackgroundCommand { get; }
    public IAsyncRelayCommand RemoveBackgroundToLayerCommand { get; }
    public IAsyncRelayCommand DownloadBackgroundRemovalModelCommand { get; }
    public IRelayCommand ToggleBackgroundFillCommand { get; }
    public IRelayCommand ApplyBackgroundFillCommand { get; }
    public IRelayCommand<string> SetBackgroundFillPresetCommand { get; }
    public IAsyncRelayCommand UpscaleImageCommand { get; }
    public IAsyncRelayCommand DownloadUpscalingModelCommand { get; }

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
    
    /// <summary>
    /// Event raised to request the Save As dialog.
    /// The handler should show the dialog and call back with the result.
    /// </summary>
    public event Func<Task<SaveAsResult>>? SaveAsDialogRequested;
    
    /// <summary>
    /// Event raised when Save As dialog completes with a result.
    /// The handler should perform the actual save with the provided filename.
    /// </summary>
    public event EventHandler<SaveAsResult>? SaveAsRequested;
    
    public event Func<Task<bool>>? SaveOverwriteConfirmRequested;
    public event EventHandler? SaveOverwriteRequested;
    public event EventHandler? ZoomInRequested;
    public event EventHandler? ZoomOutRequested;
    public event EventHandler? ZoomToFitRequested;
    public event EventHandler? ZoomToActualRequested;
    public event EventHandler? RotateLeftRequested;
    public event EventHandler? RotateRightRequested;
    public event EventHandler? Rotate180Requested;
    public event EventHandler? FlipHorizontalRequested;
    public event EventHandler? FlipVerticalRequested;
    public event EventHandler<ColorBalanceSettings>? ApplyColorBalanceRequested;
    public event EventHandler<ColorBalanceSettings>? ColorBalancePreviewRequested;
    public event EventHandler? CancelColorBalancePreviewRequested;
    public event EventHandler<BrightnessContrastSettings>? ApplyBrightnessContrastRequested;
    public event EventHandler<BrightnessContrastSettings>? BrightnessContrastPreviewRequested;
    public event EventHandler? CancelBrightnessContrastPreviewRequested;

    /// <summary>
    /// Event raised to request image data for background removal.
    /// The View should respond by calling ProcessBackgroundRemovalAsync with the image data.
    /// </summary>
    public event EventHandler? RemoveBackgroundRequested;

    /// <summary>
    /// Event raised to request layer-based background removal.
    /// The View should respond by calling ProcessBackgroundRemovalToLayerAsync with the image data.
    /// </summary>
    public event EventHandler? RemoveBackgroundToLayerRequested;

    /// <summary>
    /// Event raised when background removal completes with result.
    /// The View should apply the mask to the image editor.
    /// </summary>
    public event EventHandler<BackgroundRemovalResult>? BackgroundRemovalCompleted;

    /// <summary>
    /// Event raised when layer-based background removal completes with result.
    /// The View should apply the mask as layers to the image editor.
    /// </summary>
    public event EventHandler<BackgroundRemovalResult>? BackgroundRemovalToLayerCompleted;

    /// <summary>
    /// Event raised to request background fill preview.
    /// The View should apply the preview to the image editor.
    /// </summary>
    public event EventHandler<ImageEditor.BackgroundFillSettings>? BackgroundFillPreviewRequested;

    /// <summary>
    /// Event raised to cancel background fill preview.
    /// </summary>
    public event EventHandler? CancelBackgroundFillPreviewRequested;

    /// <summary>
    /// Event raised to apply background fill permanently.
    /// </summary>
    public event EventHandler<ImageEditor.BackgroundFillSettings>? ApplyBackgroundFillRequested;

    /// <summary>
    /// Event raised when an image save completes successfully.
    /// The string parameter contains the saved file path.
    /// </summary>
    public event EventHandler<string>? ImageSaved;

    /// <summary>
    /// Event raised to request image data for upscaling.
    /// The View should respond by calling ProcessUpscalingAsync with the image data.
    /// </summary>
    public event EventHandler? UpscaleImageRequested;

    /// <summary>
    /// Event raised when upscaling completes with result.
    /// The View should apply the upscaled image to the image editor.
    /// </summary>
    public event EventHandler<ImageUpscalingResult>? UpscalingCompleted;

    /// <summary>
    /// Event raised to request export of the current image to an external location.
    /// </summary>
    public event EventHandler<ExportEventArgs>? ExportRequested;

    /// <summary>
    /// Event raised when drawing tool is activated or deactivated.
    /// The bool parameter indicates whether the tool is now active.
    /// </summary>
    public event EventHandler<bool>? DrawingToolActivated;

    /// <summary>
    /// Event raised when drawing settings (color, size, shape) change.
    /// </summary>
    public event EventHandler<ImageEditor.DrawingSettings>? DrawingSettingsChanged;

    #endregion

    /// <summary>
    /// Deactivates all tools except the one specified.
    /// Ensures mutual exclusion - only one tool can be active at a time.
    /// </summary>
    /// <param name="exceptTool">The name of the tool property to keep active.</param>
    private void DeactivateOtherTools(string exceptTool)
    {
        if (exceptTool != nameof(IsCropToolActive) && _isCropToolActive)
        {
            _isCropToolActive = false;
            OnPropertyChanged(nameof(IsCropToolActive));
            CropToolDeactivated?.Invoke(this, EventArgs.Empty);
        }

        if (exceptTool != nameof(IsColorBalancePanelOpen) && _isColorBalancePanelOpen)
        {
            _isColorBalancePanelOpen = false;
            ResetColorBalanceSliders();
            CancelColorBalancePreviewRequested?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(IsColorBalancePanelOpen));
        }

        if (exceptTool != nameof(IsBrightnessContrastPanelOpen) && _isBrightnessContrastPanelOpen)
        {
            _isBrightnessContrastPanelOpen = false;
            ResetBrightnessContrastSliders();
            CancelBrightnessContrastPreviewRequested?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(IsBrightnessContrastPanelOpen));
        }

        if (exceptTool != nameof(IsBackgroundRemovalPanelOpen) && _isBackgroundRemovalPanelOpen)
        {
            _isBackgroundRemovalPanelOpen = false;
            OnPropertyChanged(nameof(IsBackgroundRemovalPanelOpen));
        }

        if (exceptTool != nameof(IsBackgroundFillPanelOpen) && _isBackgroundFillPanelOpen)
        {
            _isBackgroundFillPanelOpen = false;
            CancelBackgroundFillPreviewRequested?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(IsBackgroundFillPanelOpen));
        }

        if (exceptTool != nameof(IsUpscalingPanelOpen) && _isUpscalingPanelOpen)
        {
            _isUpscalingPanelOpen = false;
            OnPropertyChanged(nameof(IsUpscalingPanelOpen));
        }

        if (exceptTool != nameof(IsDrawingToolActive) && _isDrawingToolActive)
        {
            _isDrawingToolActive = false;
            OnPropertyChanged(nameof(IsDrawingToolActive));
            DrawingToolActivated?.Invoke(this, false);
        }

        if (exceptTool != nameof(IsInpaintingPanelOpen) && _isInpaintingPanelOpen)
        {
            _isInpaintingPanelOpen = false;
            OnPropertyChanged(nameof(IsInpaintingPanelOpen));
            InpaintToolActivated?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Notifies all tool-related commands that their CanExecute state may have changed.
    /// </summary>
    private void NotifyToolCommandsCanExecuteChanged()
    {
        ToggleCropToolCommand.NotifyCanExecuteChanged();
        ApplyCropCommand.NotifyCanExecuteChanged();
        CancelCropCommand.NotifyCanExecuteChanged();
        FitCropCommand.NotifyCanExecuteChanged();
        FillCropCommand.NotifyCanExecuteChanged();
        SetCropAspectRatioCommand.NotifyCanExecuteChanged();
        SwitchCropAspectRatioCommand.NotifyCanExecuteChanged();
        ToggleColorBalanceCommand.NotifyCanExecuteChanged();
        ApplyColorBalanceCommand.NotifyCanExecuteChanged();
        ResetColorBalanceRangeCommand.NotifyCanExecuteChanged();
        ApplyBrightnessContrastCommand.NotifyCanExecuteChanged();
        ResetBrightnessContrastCommand.NotifyCanExecuteChanged();
        RemoveBackgroundCommand.NotifyCanExecuteChanged();
        DownloadBackgroundRemovalModelCommand.NotifyCanExecuteChanged();
        ToggleBackgroundFillCommand.NotifyCanExecuteChanged();
        ApplyBackgroundFillCommand.NotifyCanExecuteChanged();
        UpscaleImageCommand.NotifyCanExecuteChanged();
        DownloadUpscalingModelCommand.NotifyCanExecuteChanged();
        ToggleDrawingToolCommand.NotifyCanExecuteChanged();
        ClearInpaintMaskCommand.NotifyCanExecuteChanged();
        GenerateInpaintCommand.NotifyCanExecuteChanged();
        GenerateAndCompareInpaintCommand.NotifyCanExecuteChanged();
        UseCurrentAsInpaintBaseCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();

        // Notify sub-ViewModels
        ColorTools.RefreshCommandStates();
        DrawingTools.RefreshCommandStates();
        LayerPanel.NotifyCommandsCanExecuteChanged();
    }

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

    private bool CanExecuteRemoveBackground() => 
        HasImage && !IsBackgroundRemovalBusy;

    private bool CanExecuteDownloadModel() => 
        IsBackgroundRemovalModelMissing && !IsBackgroundRemovalBusy;

    private void NotifyCommandsCanExecuteChanged()
    {
        ClearImageCommand.NotifyCanExecuteChanged();
        ResetImageCommand.NotifyCanExecuteChanged();
        ToggleCropToolCommand.NotifyCanExecuteChanged();
        ApplyCropCommand.NotifyCanExecuteChanged();
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
        ToggleColorBalanceCommand.NotifyCanExecuteChanged();
        ApplyColorBalanceCommand.NotifyCanExecuteChanged();
        ResetColorBalanceRangeCommand.NotifyCanExecuteChanged();
        ToggleBrightnessContrastCommand.NotifyCanExecuteChanged();
        ApplyBrightnessContrastCommand.NotifyCanExecuteChanged();
        ResetBrightnessContrastCommand.NotifyCanExecuteChanged();
        RemoveBackgroundCommand.NotifyCanExecuteChanged();
        RemoveBackgroundToLayerCommand.NotifyCanExecuteChanged();
        DownloadBackgroundRemovalModelCommand.NotifyCanExecuteChanged();
        ToggleBackgroundFillCommand.NotifyCanExecuteChanged();
        ApplyBackgroundFillCommand.NotifyCanExecuteChanged();
        UpscaleImageCommand.NotifyCanExecuteChanged();
        DownloadUpscalingModelCommand.NotifyCanExecuteChanged();
        ToggleDrawingToolCommand.NotifyCanExecuteChanged();
        NotifyRatingCommandsCanExecuteChanged();
        NotifyLayerCommandsCanExecuteChanged();
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

    /// <summary>
    /// Creates a new ImageEditorViewModel with event aggregator integration.
    /// </summary>
    /// <param name="eventAggregator">The event aggregator for publishing events.</param>
    /// <param name="backgroundRemovalService">Optional background removal service.</param>
    /// <param name="upscalingService">Optional image upscaling service.</param>
    /// <param name="comfyUiService">Optional ComfyUI wrapper service for inpainting.</param>
    public ImageEditorViewModel(
        IDatasetEventAggregator? eventAggregator = null,
        IBackgroundRemovalService? backgroundRemovalService = null,
        IImageUpscalingService? upscalingService = null,
        IComfyUIWrapperService? comfyUiService = null,
        EditorServices? services = null)
    {
        _eventAggregator = eventAggregator;
        _backgroundRemovalService = backgroundRemovalService;
        _upscalingService = upscalingService;
        _comfyUiService = comfyUiService;
        _services = services ?? EditorServiceFactory.Create();

        // Initialize sub-ViewModels
        LayerPanel = new LayerPanelViewModel(() => HasImage);
        ColorTools = new ColorToolsViewModel(() => HasImage, DeactivateOtherTools);
        DrawingTools = new DrawingToolsViewModel(() => HasImage, DeactivateOtherTools);

        // Wire sub-ViewModel events to parent
        WireSubViewModelEvents();

        // Subscribe to service events for cross-cutting concerns
        _services.Viewport.Changed += (_, _) =>
        {
            ZoomPercentage = _services.Viewport.ZoomPercentage;
            IsFitMode = _services.Viewport.IsFitMode;
        };

        _services.Tools.ActiveToolChanged += (_, e) =>
        {
            NotifyToolCommandsCanExecuteChanged();
        };

        ClearImageCommand = new RelayCommand(ExecuteClearImage, () => HasImage);
        ResetImageCommand = new RelayCommand(ExecuteResetImage, () => HasImage);
        ToggleCropToolCommand = new RelayCommand(ExecuteToggleCropTool, () => HasImage && !IsColorBalancePanelOpen);
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
        
        MarkApprovedCommand = new RelayCommand(ExecuteMarkApproved, () => HasImage && _selectedDatasetImage is not null);
        MarkRejectedCommand = new RelayCommand(ExecuteMarkRejected, () => HasImage && _selectedDatasetImage is not null);
        ClearRatingCommand = new RelayCommand(ExecuteClearRating, () => HasImage && _selectedDatasetImage is not null && !IsUnrated);

        // Transform commands
        RotateLeftCommand = new RelayCommand(ExecuteRotateLeft, () => HasImage);
        RotateRightCommand = new RelayCommand(ExecuteRotateRight, () => HasImage);
        Rotate180Command = new RelayCommand(ExecuteRotate180, () => HasImage);
        FlipHorizontalCommand = new RelayCommand(ExecuteFlipHorizontal, () => HasImage);
        FlipVerticalCommand = new RelayCommand(ExecuteFlipVertical, () => HasImage);

        // Color Balance commands
        ToggleColorBalanceCommand = new RelayCommand(ExecuteToggleColorBalance, () => HasImage && !IsCropToolActive);
        ApplyColorBalanceCommand = new RelayCommand(ExecuteApplyColorBalance, () => HasImage && IsColorBalancePanelOpen && HasColorBalanceAdjustments);
        ResetColorBalanceRangeCommand = new RelayCommand(ExecuteResetColorBalanceRange, () => IsColorBalancePanelOpen && HasColorBalanceAdjustments);

        // Brightness/Contrast commands
        ToggleBrightnessContrastCommand = new RelayCommand(ExecuteToggleBrightnessContrast, () => HasImage && !IsColorBalancePanelOpen);
        ApplyBrightnessContrastCommand = new RelayCommand(ExecuteApplyBrightnessContrast, () => HasImage && IsBrightnessContrastPanelOpen && HasBrightnessContrastAdjustments);
        ResetBrightnessContrastCommand = new RelayCommand(ExecuteResetBrightnessContrast, () => IsBrightnessContrastPanelOpen && HasBrightnessContrastAdjustments);

        // Background Removal commands
        RemoveBackgroundCommand = new AsyncRelayCommand(ExecuteRemoveBackgroundAsync, CanExecuteRemoveBackground);
        RemoveBackgroundToLayerCommand = new AsyncRelayCommand(ExecuteRemoveBackgroundToLayerAsync, CanExecuteRemoveBackground);
        DownloadBackgroundRemovalModelCommand = new AsyncRelayCommand(ExecuteDownloadBackgroundRemovalModelAsync, CanExecuteDownloadModel);

        // Background Fill commands
        ToggleBackgroundFillCommand = new RelayCommand(ExecuteToggleBackgroundFill, () => HasImage);
        ApplyBackgroundFillCommand = new RelayCommand(ExecuteApplyBackgroundFill, () => HasImage && IsBackgroundFillPanelOpen);
        SetBackgroundFillPresetCommand = new RelayCommand<string>(SetBackgroundFillPreset);


        // AI Upscaling commands
        UpscaleImageCommand = new AsyncRelayCommand(ExecuteUpscaleImageAsync, CanExecuteUpscaleImage);
        DownloadUpscalingModelCommand = new AsyncRelayCommand(ExecuteDownloadUpscalingModelAsync, CanExecuteDownloadUpscalingModel);

        // Drawing tool commands
        ToggleDrawingToolCommand = new RelayCommand(ExecuteToggleDrawingTool, () => HasImage);
        SetDrawingColorPresetCommand = new RelayCommand<string>(SetDrawingColorPreset);

        // Shape tool commands
        SetShapeFillPresetCommand = new RelayCommand<string>(SetShapeFillPreset);
        SetShapeStrokePresetCommand = new RelayCommand<string>(SetShapeStrokePreset);
        CommitPlacedShapeCommand = new RelayCommand(
            () => CommitPlacedShapeRequested?.Invoke(this, EventArgs.Empty),
            () => HasPlacedShape);
        CancelPlacedShapeCommand = new RelayCommand(
            () => CancelPlacedShapeRequested?.Invoke(this, EventArgs.Empty),
            () => HasPlacedShape);

        // Inpainting commands
        ClearInpaintMaskCommand = new RelayCommand(
            () => ClearInpaintMaskRequested?.Invoke(this, EventArgs.Empty),
            () => HasImage && IsInpaintingPanelOpen);
        GenerateInpaintCommand = new AsyncRelayCommand(
            ExecuteGenerateInpaintAsync,
            () => HasImage && IsInpaintingPanelOpen && !IsInpaintingBusy);
        GenerateAndCompareInpaintCommand = new AsyncRelayCommand(
            ExecuteGenerateAndCompareInpaintAsync,
            () => HasImage && IsInpaintingPanelOpen && !IsInpaintingBusy);
        UseCurrentAsInpaintBaseCommand = new RelayCommand(
            () => SetInpaintBaseRequested?.Invoke(this, EventArgs.Empty),
            () => HasImage && IsInpaintingPanelOpen);

        // Layer commands (layers are always enabled when an image is loaded)
        ToggleLayerModeCommand = new RelayCommand(ExecuteToggleLayerMode, () => HasImage);
        AddLayerCommand = new RelayCommand(ExecuteAddLayer, () => HasImage);
        DeleteLayerCommand = new RelayCommand(ExecuteDeleteLayer, () => HasImage && SelectedLayer != null && Layers.Count > 1 && !SelectedLayer.Layer.IsInpaintMask);
        DuplicateLayerCommand = new RelayCommand(ExecuteDuplicateLayer, () => HasImage && SelectedLayer != null && !SelectedLayer.Layer.IsInpaintMask);
        MoveLayerUpCommand = new RelayCommand(ExecuteMoveLayerUp, () => HasImage && SelectedLayer != null && CanMoveLayerUp);
        MoveLayerDownCommand = new RelayCommand(ExecuteMoveLayerDown, () => HasImage && SelectedLayer != null && CanMoveLayerDown);
        MergeLayerDownCommand = new RelayCommand(ExecuteMergeLayerDown, () => HasImage && SelectedLayer != null && CanMergeDown);
        MergeVisibleLayersCommand = new RelayCommand(ExecuteMergeVisibleLayers, () => HasImage && Layers.Count > 1);
        FlattenLayersCommand = new RelayCommand(ExecuteFlattenLayers, () => HasImage && Layers.Count > 1);
        SaveLayeredTiffCommand = new AsyncRelayCommand(ExecuteSaveLayeredTiffAsync, () => HasImage);
    }

    /// <summary>
    /// Wires events from sub-ViewModels to parent ViewModel for event forwarding
    /// and cross-cutting concerns during the incremental migration.
    /// </summary>
    private void WireSubViewModelEvents()
    {
        // LayerPanel: forward events that the View currently subscribes to on the parent
        LayerPanel.LayerSelectionChanged += (_, layer) => LayerSelectionChanged?.Invoke(this, layer);
        LayerPanel.SyncLayersRequested += (_, _) => SyncLayersRequested?.Invoke(this, EventArgs.Empty);
        LayerPanel.SaveLayeredTiffRequested += path => SaveLayeredTiffRequested?.Invoke(path) ?? Task.FromResult(false);
        LayerPanel.EnableLayerModeRequested += (_, enable) => EnableLayerModeRequested?.Invoke(this, enable);
        LayerPanel.AddLayerRequested += (_, _) => AddLayerRequested?.Invoke(this, EventArgs.Empty);
        LayerPanel.DeleteLayerRequested += (_, layer) => DeleteLayerRequested?.Invoke(this, layer);
        LayerPanel.DuplicateLayerRequested += (_, layer) => DuplicateLayerRequested?.Invoke(this, layer);
        LayerPanel.MoveLayerUpRequested += (_, layer) => MoveLayerUpRequested?.Invoke(this, layer);
        LayerPanel.MoveLayerDownRequested += (_, layer) => MoveLayerDownRequested?.Invoke(this, layer);
        LayerPanel.MergeLayerDownRequested += (_, layer) => MergeLayerDownRequested?.Invoke(this, layer);
        LayerPanel.MergeVisibleLayersRequested += (_, _) => MergeVisibleLayersRequested?.Invoke(this, EventArgs.Empty);
        LayerPanel.FlattenLayersRequested += (_, _) => FlattenLayersRequested?.Invoke(this, EventArgs.Empty);
        LayerPanel.SaveCompleted += (_, msg) => StatusMessage = msg;

        // ColorTools: forward events
        ColorTools.ApplyColorBalanceRequested += (_, s) => ApplyColorBalanceRequested?.Invoke(this, s);
        ColorTools.ColorBalancePreviewRequested += (_, s) => ColorBalancePreviewRequested?.Invoke(this, s);
        ColorTools.CancelColorBalancePreviewRequested += (_, _) => CancelColorBalancePreviewRequested?.Invoke(this, EventArgs.Empty);
        ColorTools.ApplyBrightnessContrastRequested += (_, s) => ApplyBrightnessContrastRequested?.Invoke(this, s);
        ColorTools.BrightnessContrastPreviewRequested += (_, s) => BrightnessContrastPreviewRequested?.Invoke(this, s);
        ColorTools.CancelBrightnessContrastPreviewRequested += (_, _) => CancelBrightnessContrastPreviewRequested?.Invoke(this, EventArgs.Empty);
        ColorTools.ToolStateChanged += (_, _) => NotifyToolCommandsCanExecuteChanged();
        ColorTools.ToolToggled += (_, args) =>
        {
            if (args.IsActive)
                _services.Tools.Activate(args.ToolId);
            else
                _services.Tools.Deactivate(args.ToolId);
        };

        // DrawingTools: forward events
        DrawingTools.DrawingToolActivated += (_, isActive) => DrawingToolActivated?.Invoke(this, isActive);
        DrawingTools.DrawingSettingsChanged += (_, settings) => DrawingSettingsChanged?.Invoke(this, settings);
        DrawingTools.ShapeSettingsChanged += (_, _) => ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
        DrawingTools.ToolStateChanged += (_, _) => NotifyToolCommandsCanExecuteChanged();
        DrawingTools.StatusMessageChanged += (_, msg) => StatusMessage = msg;
        DrawingTools.CommitPlacedShapeRequested += (_, _) => CommitPlacedShapeRequested?.Invoke(this, EventArgs.Empty);
        DrawingTools.CancelPlacedShapeRequested += (_, _) => CancelPlacedShapeRequested?.Invoke(this, EventArgs.Empty);
        DrawingTools.ToolToggled += (_, args) =>
        {
            if (args.IsActive)
                _services.Tools.Activate(args.ToolId);
            else
                _services.Tools.Deactivate(args.ToolId);
        };
    }

    #region Public Methods (View wiring)

    /// <summary>
    /// Loads an image from the specified file path.
    /// </summary>
    /// <param name="imagePath">The path to the image file.</param>
    public void LoadImage(string imagePath)
    {
        CurrentImagePath = imagePath;
    }

    /// <summary>
    /// Updates the image dimensions displayed in the ViewModel.
    /// Called by the View when the image changes.
    /// </summary>
    public void UpdateDimensions(int width, int height)
    {
        ImageWidth = width;
        ImageHeight = height;
        OnPropertyChanged(nameof(UpscaleOutputDimensions));
    }

    /// <summary>
    /// Updates file information displayed in the ViewModel.
    /// Called by the View when the image changes.
    /// </summary>
    public void UpdateFileInfo(int dpi, long fileSizeBytes)
    {
        ImageDpi = dpi;
        FileSizeBytes = fileSizeBytes;
    }

    /// <summary>
    /// Updates zoom information displayed in the ViewModel.
    /// Called by the View when zoom changes.
    /// </summary>
    public void UpdateZoomInfo(int zoomPercentage, bool isFitMode)
    {
        ZoomPercentage = zoomPercentage;
        IsFitMode = isFitMode;
    }

    /// <summary>
    /// Called when crop is applied successfully.
    /// </summary>
    public void OnCropApplied()
    {
        IsCropToolActive = false;
        CropResolutionText = string.Empty;
        StatusMessage = "Crop applied";
    }

    /// <summary>
    /// Called when color balance is applied successfully.
    /// </summary>
    public void OnColorBalanceApplied()
    {
        IsColorBalancePanelOpen = false;
        StatusMessage = "Color balance applied";
    }

    /// <summary>
    /// Called when brightness/contrast is applied successfully.
    /// </summary>
    public void OnBrightnessContrastApplied()
    {
        IsBrightnessContrastPanelOpen = false;
        StatusMessage = "Brightness/Contrast applied";
    }

    /// <summary>
    /// Called when background removal is applied successfully.
    /// </summary>
    public void OnBackgroundRemovalApplied()
    {
        StatusMessage = "Background removed";
    }

    /// <summary>
    /// Called when layer-based background removal is applied successfully.
    /// Triggers layer synchronization.
    /// </summary>
    public void OnBackgroundRemovalToLayerApplied()
    {
        StatusMessage = "Background separated to layers";
        // Request synchronization of layer ViewModels
        SyncLayersRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called when background fill is applied successfully.
    /// </summary>
    public void OnBackgroundFillApplied()
    {
        IsBackgroundFillPanelOpen = false;
        StatusMessage = "Background filled";
    }

    /// <summary>
    /// Called when upscaling is applied successfully.
    /// </summary>
    public void OnUpscalingApplied()
    {
        StatusMessage = "Image upscaled";
        OnPropertyChanged(nameof(UpscaleOutputDimensions));
    }

    /// <summary>
    /// Called when Save As New completes successfully.
    /// </summary>
    /// <param name="newPath">The path where the image was saved.</param>
    /// <param name="rating">The rating applied to the saved image.</param>
    public void OnSaveAsNewCompleted(string newPath, ImageRatingStatus rating)
    {
        FileLogger.LogEntry($"newPath={newPath}, rating={rating}");
        
        try
        {
            StatusMessage = $"Saved as: {Path.GetFileName(newPath)}";
            FileLogger.Log("Invoking ImageSaved event...");
            ImageSaved?.Invoke(this, newPath);
            FileLogger.Log("ImageSaved event invoked");

            // Publish event via aggregator
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

    /// <summary>
    /// Called when Save Overwrite completes successfully.
    /// </summary>
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

                // Publish event via aggregator
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

    /// <summary>
    /// Called when export completes successfully.
    /// </summary>
    /// <param name="exportPath">The path where the image was exported.</param>
    public void OnExportCompleted(string exportPath)
    {
        FileLogger.LogEntry($"exportPath={exportPath}");
        StatusMessage = $"Exported to: {Path.GetFileName(exportPath)}";
        FileLogger.LogExit();
    }

    /// <summary>
    /// Processes background removal with the provided image data.
    /// Called by the View after responding to RemoveBackgroundRequested.
    /// </summary>
    /// <param name="imageData">Raw RGBA image data.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    public async Task ProcessBackgroundRemovalAsync(byte[] imageData, int width, int height)
    {
        if (_backgroundRemovalService is null)
        {
            StatusMessage = "Background removal service not available";
            IsBackgroundRemovalBusy = false;
            return;
        }

        IsBackgroundRemovalBusy = true;
        BackgroundRemovalStatus = "Processing...";
        BackgroundRemovalProgress = 0;

        try
        {
            var result = await _backgroundRemovalService.RemoveBackgroundAsync(imageData, width, height);

            if (result.Success)
            {
                BackgroundRemovalCompleted?.Invoke(this, result);
            }
            else
            {
                StatusMessage = result.ErrorMessage ?? "Background removal failed";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Background removal cancelled";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Background removal failed: {ex.Message}";
        }
        finally
        {
            BackgroundRemovalStatus = null;
            BackgroundRemovalProgress = 0;
            IsBackgroundRemovalBusy = false;
        }
    }

    /// <summary>
    /// Processes layer-based background removal with the provided image data.
    /// Called by the View after responding to RemoveBackgroundToLayerRequested.
    /// </summary>
    /// <param name="imageData">Raw RGBA image data.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    public async Task ProcessBackgroundRemovalToLayerAsync(byte[] imageData, int width, int height)
    {
        if (_backgroundRemovalService is null)
        {
            StatusMessage = "Background removal service not available";
            IsBackgroundRemovalBusy = false;
            return;
        }

        IsBackgroundRemovalBusy = true;
        BackgroundRemovalStatus = "Processing for layers...";
        BackgroundRemovalProgress = 0;

        try
        {
            var result = await _backgroundRemovalService.RemoveBackgroundAsync(imageData, width, height);

            if (result.Success)
            {
                BackgroundRemovalToLayerCompleted?.Invoke(this, result);
            }
            else
            {
                StatusMessage = result.ErrorMessage ?? "Background removal failed";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Background removal cancelled";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Background removal failed: {ex.Message}";
        }
        finally
        {
            BackgroundRemovalStatus = null;
            BackgroundRemovalProgress = 0;
            IsBackgroundRemovalBusy = false;
        }
    }

    /// <summary>
    /// Processes upscaling with the provided image data.
    /// Called by the View after responding to UpscaleImageRequested.
    /// </summary>
    /// <param name="imageData">Raw RGBA image data.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    public async Task ProcessUpscalingAsync(byte[] imageData, int width, int height)
    {
        if (_upscalingService is null)
        {
            StatusMessage = "Upscaling service not available";
            IsUpscalingBusy = false;
            return;
        }

        try
        {
            var progress = new Progress<UpscalingProgress>(p =>
            {
                UpscalingStatus = p.Message;
                if (p.Percentage >= 0)
                    UpscalingProgress = p.Percentage;
            });

            var result = await _upscalingService.UpscaleImageAsync(
                imageData, width, height, _upscaleTargetScale, progress);

            if (result.Success)
            {
                StatusMessage = $"Upscaled to {result.Width}x{result.Height}";
                UpscalingCompleted?.Invoke(this, result);
            }
            else
            {
                StatusMessage = result.ErrorMessage ?? "Upscaling failed";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Upscaling cancelled";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Upscaling failed: {ex.Message}";
        }
        finally
        {
            UpscalingStatus = null;
            UpscalingProgress = 0;
            IsUpscalingBusy = false;
        }
    }

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

    private void ExecuteApplyCrop()
    {
        ApplyCropRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteCancelCrop()
    {
        IsCropToolActive = false;
        CropResolutionText = string.Empty;
        CancelCropRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteFitCrop()
    {
        FitCropRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteFillCrop()
    {
        FillCropRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteSetCropAspectRatio(string? ratio)
    {
        if (string.IsNullOrWhiteSpace(ratio)) return;

        var parts = ratio.Split(':');
        if (parts.Length != 2 ||
            !float.TryParse(parts[0], out var w) ||
            !float.TryParse(parts[1], out var h))
            return;

        if (_cropAspectInverted)
            (w, h) = (h, w);

        SetCropAspectRatioRequested?.Invoke(this, (w, h));
    }

    private void ExecuteSwitchCropAspectRatio()
    {
        CropAspectInverted = !CropAspectInverted;
        SwitchCropAspectRatioRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Updates the crop resolution text from the current crop dimensions.
    /// Called by the View when the crop region changes.
    /// </summary>
    public void UpdateCropResolution(int width, int height)
    {
        CropResolutionText = width > 0 && height > 0 ? $"{width} x {height}" : string.Empty;
    }

    private async Task ExecuteSaveAsNewAsync()
    {
        FileLogger.LogEntry();
        
        if (SaveAsDialogRequested is null)
        {
            FileLogger.LogWarning("SaveAsDialogRequested is null");
            return;
        }

        FileLogger.Log("Invoking SaveAsDialogRequested...");
        var result = await SaveAsDialogRequested.Invoke();
        FileLogger.Log($"Dialog result: IsCancelled={result.IsCancelled}, FileName={result.FileName ?? "(null)"}");
        
        if (!result.IsCancelled)
        {
            FileLogger.Log("Invoking SaveAsRequested event...");
            SaveAsRequested?.Invoke(this, result);
            FileLogger.Log("SaveAsRequested event completed");
        }
        
        FileLogger.LogExit();
    }

    private async Task ExecuteSaveOverwriteAsync()
    {
        FileLogger.LogEntry();
        
        if (SaveOverwriteConfirmRequested is null)
        {
            FileLogger.Log("No confirmation requested, invoking SaveOverwriteRequested directly");
            SaveOverwriteRequested?.Invoke(this, EventArgs.Empty);
            FileLogger.LogExit();
            return;
        }


        FileLogger.Log("Requesting confirmation...");
        var confirmed = await SaveOverwriteConfirmRequested.Invoke();
        FileLogger.Log($"Confirmation result: {confirmed}");
        
        if (confirmed)
        {
            FileLogger.Log("Invoking SaveOverwriteRequested...");
            SaveOverwriteRequested?.Invoke(this, EventArgs.Empty);
            FileLogger.Log("SaveOverwriteRequested completed");
        }
        
        FileLogger.LogExit();
    }

    private Task ExecuteExportAsync()
    {
        if (CurrentImagePath is null) return Task.CompletedTask;

        var extension = Path.GetExtension(CurrentImagePath);
        var fileName = Path.GetFileNameWithoutExtension(CurrentImagePath);
        var suggestedFileName = $"{fileName}_export{extension}";

        ExportRequested?.Invoke(this, new ExportEventArgs
        {
            SuggestedFileName = suggestedFileName,
            FileExtension = extension
        });

        return Task.CompletedTask;
    }

    private void ExecuteZoomIn()
    {
        _services.Viewport.ZoomIn();
        ZoomInRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteZoomOut()
    {
        _services.Viewport.ZoomOut();
        ZoomOutRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteZoomToFit()
    {
        _services.Viewport.ZoomToFit();
        ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteZoomToActual()
    {
        _services.Viewport.ZoomToActual();
        ZoomToActualRequested?.Invoke(this, EventArgs.Empty);
    }
    private void ExecuteRotateLeft() => RotateLeftRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteRotateRight() => RotateRightRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteRotate180() => Rotate180Requested?.Invoke(this, EventArgs.Empty);
    private void ExecuteFlipHorizontal() => FlipHorizontalRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteFlipVertical() => FlipVerticalRequested?.Invoke(this, EventArgs.Empty);

    private async Task ExecuteRemoveBackgroundAsync()
    {
        if (_backgroundRemovalService is null)
        {
            StatusMessage = "Background removal service not available";
            return;
        }

        // Check if model is ready - if not, notify user to download
        if (!IsBackgroundRemovalModelReady)
        {
            StatusMessage = "Please download the RMBG-1.4 model first";
            return;
        }

        IsBackgroundRemovalBusy = true;
        BackgroundRemovalStatus = "Preparing image...";
        BackgroundRemovalProgress = 0;

        try
        {
            // Request image data from the View
            RemoveBackgroundRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Background removal failed: {ex.Message}";
            BackgroundRemovalStatus = null;
            BackgroundRemovalProgress = 0;
            IsBackgroundRemovalBusy = false;
        }
    }

    private async Task ExecuteRemoveBackgroundToLayerAsync()
    {
        if (_backgroundRemovalService is null)
        {
            StatusMessage = "Background removal service not available";
            return;
        }

        // Check if model is ready - if not, notify user to download
        if (!IsBackgroundRemovalModelReady)
        {
            StatusMessage = "Please download the RMBG-1.4 model first";
            return;
        }

        IsBackgroundRemovalBusy = true;
        BackgroundRemovalStatus = "Preparing image for layer-based removal...";
        BackgroundRemovalProgress = 0;

        try
        {
            // Request image data from the View for layer-based removal
            RemoveBackgroundToLayerRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Background removal failed: {ex.Message}";
            BackgroundRemovalStatus = null;
            BackgroundRemovalProgress = 0;
            IsBackgroundRemovalBusy = false;
        }
    }

    private async Task ExecuteDownloadBackgroundRemovalModelAsync()
    {
        if (_backgroundRemovalService is null)
        {
            StatusMessage = "Background removal service not available";
            return;
        }

        IsBackgroundRemovalBusy = true;
        BackgroundRemovalProgress = 0;
        BackgroundRemovalStatus = "Downloading model...";

        try
        {
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                if (p.Percentage >= 0)
                    BackgroundRemovalProgress = (int)p.Percentage;
                BackgroundRemovalStatus = p.Status;
            });

            var success = await _backgroundRemovalService.DownloadModelAsync(progress);

            if (success)
            {
                StatusMessage = "RMBG-1.4 model downloaded successfully";
                RefreshBackgroundRemovalModelStatus();
            }
            else
            {
                StatusMessage = "Failed to download background removal model";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Model download cancelled";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Model download failed: {ex.Message}";
        }
        finally
        {
            BackgroundRemovalStatus = null;
            BackgroundRemovalProgress = 0;
            IsBackgroundRemovalBusy = false;
        }
    }

    private void ExecuteToggleBackgroundFill()
    {
        IsBackgroundFillPanelOpen = !IsBackgroundFillPanelOpen;
        if (IsBackgroundFillPanelOpen)
            _services.Tools.Activate(ToolIds.BackgroundFill);
        else
            _services.Tools.Deactivate(ToolIds.BackgroundFill);
    }

    private void ExecuteApplyBackgroundFill()
    {
        ApplyBackgroundFillRequested?.Invoke(this, CurrentBackgroundFillSettings);
    }

    private void ExecuteToggleDrawingTool()
    {
        IsDrawingToolActive = !IsDrawingToolActive;
        if (IsDrawingToolActive)
            _services.Tools.Activate(ToolIds.Drawing);
        else
            _services.Tools.Deactivate(ToolIds.Drawing);
    }

    private bool CanExecuteUpscaleImage() => 
        HasImage && !IsUpscalingBusy && IsUpscalingModelReady;

    private bool CanExecuteDownloadUpscalingModel() => 
        IsUpscalingModelMissing && !IsUpscalingBusy;

    private async Task ExecuteUpscaleImageAsync()
    {
        if (_upscalingService is null)
        {
            StatusMessage = "Upscaling service not available";
            return;
        }

        // Check if model is ready - if not, notify user to download
        if (!IsUpscalingModelReady)
        {
            StatusMessage = "Please download the 4x-UltraSharp model first";
            return;
        }

        IsUpscalingBusy = true;
        UpscalingStatus = "Preparing image...";
        UpscalingProgress = 0;

        try
        {
            // Request image data from the View
            UpscaleImageRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Upscaling failed: {ex.Message}";
            UpscalingStatus = null;
            UpscalingProgress = 0;
            IsUpscalingBusy = false;
        }
    }

    private async Task ExecuteDownloadUpscalingModelAsync()
    {
        if (_upscalingService is null)
        {
            StatusMessage = "Upscaling service not available";
            return;
        }

        IsUpscalingBusy = true;
        UpscalingProgress = 0;
        UpscalingStatus = "Downloading model...";

        try
        {
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                if (p.Percentage >= 0)
                    UpscalingProgress = (int)p.Percentage;
                UpscalingStatus = p.Status;
            });

            var success = await _upscalingService.DownloadModelAsync(progress);

            if (success)
            {
                StatusMessage = "4x-UltraSharp model downloaded successfully";
                RefreshUpscalingModelStatus();
            }
            else
            {
                StatusMessage = "Failed to download upscaling model";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Model download cancelled";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Model download failed: {ex.Message}";
        }
        finally
        {
            UpscalingStatus = null;
            UpscalingProgress = 0;
            IsUpscalingBusy = false;
        }
    }

    /// <summary>
    /// Closes all active tools and resets their state.
    /// Should be called before loading a new image or clearing the current image.
    /// </summary>
    private void CloseAllTools()
    {
        // Close crop tool
        if (_isCropToolActive)
        {
            _isCropToolActive = false;
            OnPropertyChanged(nameof(IsCropToolActive));
            CropToolDeactivated?.Invoke(this, EventArgs.Empty);
        }

        // Delegate to sub-ViewModels
        ColorTools.CloseAllPanels();
        DrawingTools.CloseAll();

        // Close color balance panel and cancel any preview
        if (_isColorBalancePanelOpen)
        {
            _isColorBalancePanelOpen = false;
            ResetColorBalanceSliders();
            CancelColorBalancePreviewRequested?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(IsColorBalancePanelOpen));
        }

        // Close brightness/contrast panel and cancel any preview
        if (_isBrightnessContrastPanelOpen)
        {
            _isBrightnessContrastPanelOpen = false;
            ResetBrightnessContrastSliders();
            CancelBrightnessContrastPreviewRequested?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(IsBrightnessContrastPanelOpen));
        }

        // Close background removal panel
        if (_isBackgroundRemovalPanelOpen)
        {
            _isBackgroundRemovalPanelOpen = false;
            OnPropertyChanged(nameof(IsBackgroundRemovalPanelOpen));
        }

        // Close background fill panel and cancel any preview
        if (_isBackgroundFillPanelOpen)
        {
            _isBackgroundFillPanelOpen = false;
            CancelBackgroundFillPreviewRequested?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(IsBackgroundFillPanelOpen));
        }

        // Close upscaling panel
        if (_isUpscalingPanelOpen)
        {
            _isUpscalingPanelOpen = false;
            OnPropertyChanged(nameof(IsUpscalingPanelOpen));
        }

        // Close drawing tool
        if (_isDrawingToolActive)
        {
            _isDrawingToolActive = false;
            OnPropertyChanged(nameof(IsDrawingToolActive));
            DrawingToolActivated?.Invoke(this, false);
        }

        // Add more tools here as they are added in the future
    }

    private async Task ExecuteGenerateInpaintAsync()
    {
        if (string.IsNullOrWhiteSpace(_inpaintPositivePrompt))
        {
            StatusMessage = "Please enter a prompt describing what to generate.";
            return;
        }

        if (_comfyUiService is null)
        {
            StatusMessage = "ComfyUI service not available. Check ComfyUI server settings.";
            return;
        }

        _pendingCompareBeforeImagePath = null;
        IsInpaintingBusy = true;
        InpaintingStatus = "Preparing image and mask...";
        NotifyInpaintCommandsCanExecuteChanged();

        // Fire event so the View can gather image data and call ProcessInpaintAsync
        GenerateInpaintRequested?.Invoke(this, EventArgs.Empty);

        await Task.CompletedTask;
    }

    private async Task ExecuteGenerateAndCompareInpaintAsync()
    {
        if (string.IsNullOrWhiteSpace(_inpaintPositivePrompt))
        {
            StatusMessage = "Please enter a prompt describing what to generate.";
            return;
        }

        if (_comfyUiService is null)
        {
            StatusMessage = "ComfyUI service not available. Check ComfyUI server settings.";
            return;
        }

        // Mark compare mode — the View will set the before-image path via SetCompareBeforeImagePath
        _pendingCompareBeforeImagePath = string.Empty;
        IsInpaintingBusy = true;
        InpaintingStatus = "Preparing image and mask...";
        NotifyInpaintCommandsCanExecuteChanged();

        // Fire the same event — the View handler will detect IsCompareMode and save the before image
        GenerateInpaintRequested?.Invoke(this, EventArgs.Empty);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Processes the inpainting workflow via ComfyUI.
    /// Called by the View after it prepares the masked image file.
    /// </summary>
    /// <param name="maskedImagePath">Path to the PNG with mask in alpha channel.</param>
    public async Task ProcessInpaintAsync(string maskedImagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(maskedImagePath);

        if (_comfyUiService is null)
        {
            StatusMessage = "ComfyUI service not available.";
            OnInpaintingFinished();
            return;
        }

        try
        {
            InpaintingStatus = "Uploading image to ComfyUI...";
            var uploadedFilename = await _comfyUiService.UploadImageAsync(maskedImagePath);

            InpaintingStatus = "Queuing inpainting workflow...";

            var workflowPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                InpaintWorkflowPath);

            if (!File.Exists(workflowPath))
            {
                StatusMessage = $"Inpainting workflow not found: {workflowPath}";
                OnInpaintingFinished();
                return;
            }

            var random = new Random();
            var seed = (long)(random.NextDouble() * long.MaxValue);

            var promptId = await _comfyUiService.QueueWorkflowAsync(workflowPath,
                new Dictionary<string, Action<System.Text.Json.Nodes.JsonNode>>
                {
                    [InpaintLoadImageNodeId] = node =>
                    {
                        node["inputs"]!["image"] = uploadedFilename;
                    },
                    [InpaintPositivePromptNodeId] = node =>
                    {
                        node["inputs"]!["text"] = _inpaintPositivePrompt;
                    },
                    [InpaintNegativePromptNodeId] = node =>
                    {
                        node["inputs"]!["text"] = _inpaintNegativePrompt;
                    },
                    [InpaintKSamplerNodeId] = node =>
                    {
                        node["inputs"]!["seed"] = seed;
                        node["inputs"]!["denoise"] = _inpaintDenoise;
                    }
                });

            InpaintingStatus = "Generating (this may take a while)...";
            var progress = new Progress<string>(msg => InpaintingStatus = msg);
            await _comfyUiService.WaitForCompletionAsync(promptId, progress);

            InpaintingStatus = "Downloading result...";
            var result = await _comfyUiService.GetResultAsync(promptId);

            if (result.Images.Count > 0)
            {
                var imageBytes = await _comfyUiService.DownloadImageAsync(result.Images[0]);
                InpaintResultReady?.Invoke(this, imageBytes);
                StatusMessage = "Inpainting completed successfully.";

                // If compare mode is active, save the result and navigate to comparer
                if (!string.IsNullOrEmpty(_pendingCompareBeforeImagePath))
                {
                    var afterPath = Path.Combine(Path.GetTempPath(), $"diffnexus_inpaint_after_{Guid.NewGuid():N}.png");
                    await File.WriteAllBytesAsync(afterPath, imageBytes);

                    InpaintCompareRequested?.Invoke(this, new InpaintCompareEventArgs
                    {
                        BeforeImagePath = _pendingCompareBeforeImagePath,
                        AfterImagePath = afterPath
                    });

                    _eventAggregator?.PublishNavigateToImageComparer(
                        new NavigateToImageComparerEventArgs
                        {
                            ImagePaths = [_pendingCompareBeforeImagePath, afterPath]
                        });
                }
            }
            else
            {
                StatusMessage = "Inpainting completed but no output image was returned.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Inpainting was cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Inpainting failed: {ex.Message}";
        }
        finally
        {
            OnInpaintingFinished();
        }
    }

    /// <summary>
    /// Whether a "Generate and Compare" operation is pending.
    /// The View checks this to decide whether to save the before-image.
    /// </summary>
    public bool IsCompareModePending => _pendingCompareBeforeImagePath is not null;

    /// <summary>
    /// Sets the path to the "before" image saved by the View for compare mode.
    /// </summary>
    public void SetCompareBeforeImagePath(string path)
    {
        _pendingCompareBeforeImagePath = path;
    }

    private void OnInpaintingFinished()
    {
        _pendingCompareBeforeImagePath = null;
        IsInpaintingBusy = false;
        InpaintingStatus = null;
        NotifyInpaintCommandsCanExecuteChanged();
    }

    private void NotifyInpaintCommandsCanExecuteChanged()
    {
        GenerateInpaintCommand.NotifyCanExecuteChanged();
        GenerateAndCompareInpaintCommand.NotifyCanExecuteChanged();
    }


    #endregion

    #region Layer Command Implementations

    private void ExecuteToggleLayerMode()
    {
        IsLayerMode = !IsLayerMode;
        EnableLayerModeRequested?.Invoke(this, IsLayerMode);
    }

    private void ExecuteAddLayer()
    {
        AddLayerRequested?.Invoke(this, EventArgs.Empty);
    }


    private void ExecuteDeleteLayer()
    {
        if (SelectedLayer == null) return;
        DeleteLayerRequested?.Invoke(this, SelectedLayer.Layer);
    }

    private void ExecuteDuplicateLayer()
    {
        if (SelectedLayer == null) return;
        DuplicateLayerRequested?.Invoke(this, SelectedLayer.Layer);
    }

    private void ExecuteMoveLayerUp()
    {
        if (SelectedLayer == null) return;
        MoveLayerUpRequested?.Invoke(this, SelectedLayer.Layer);
    }

    private void ExecuteMoveLayerDown()
    {
        if (SelectedLayer == null) return;
        MoveLayerDownRequested?.Invoke(this, SelectedLayer.Layer);
    }

    private void ExecuteMergeLayerDown()
    {
        if (SelectedLayer == null) return;
        MergeLayerDownRequested?.Invoke(this, SelectedLayer.Layer);
    }

    private void ExecuteMergeVisibleLayers()
    {
        MergeVisibleLayersRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteFlattenLayers()
    {
        FlattenLayersRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task ExecuteSaveLayeredTiffAsync()
    {
        if (CurrentImagePath == null) return;

        var directory = Path.GetDirectoryName(CurrentImagePath);
        var fileName = Path.GetFileNameWithoutExtension(CurrentImagePath);
        var suggestedPath = Path.Combine(directory ?? "", $"{fileName}_layered.tif");

        if (SaveLayeredTiffRequested != null)
        {
            var success = await SaveLayeredTiffRequested.Invoke(suggestedPath);
            StatusMessage = success ? "Layered TIFF saved successfully" : "Failed to save layered TIFF";
        }
    }

    // Layer events for View wiring
    public event EventHandler<bool>? EnableLayerModeRequested;
    public event EventHandler? AddLayerRequested;
    public event EventHandler<Layer>? DeleteLayerRequested;
    public event EventHandler<Layer>? DuplicateLayerRequested;
    public event EventHandler<Layer>? MoveLayerUpRequested;
    public event EventHandler<Layer>? MoveLayerDownRequested;
    public event EventHandler<Layer>? MergeLayerDownRequested;
    public event EventHandler? MergeVisibleLayersRequested;
    public event EventHandler? FlattenLayersRequested;

    #endregion
}

/// <summary>
/// Event arguments for the export request.
/// </summary>
public class ExportEventArgs : EventArgs
{
    /// <summary>
    /// Suggested filename for the export (with extension).
    /// </summary>
    public string SuggestedFileName { get; init; } = string.Empty;

    /// <summary>
    /// The file extension (e.g., ".png").
    /// </summary>
    public string FileExtension { get; init; } = ".png";
}

/// <summary>
/// Event arguments for the "Generate and Compare" inpainting result.
/// Contains paths to both the before and after images for the Image Comparer.
/// </summary>
public class InpaintCompareEventArgs : EventArgs
{
    /// <summary>
    /// Path to the "before" image (flattened original before inpainting).
    /// </summary>
    public required string BeforeImagePath { get; init; }

    /// <summary>
    /// Path to the "after" image (inpainting result).
    /// </summary>
    public required string AfterImagePath { get; init; }
}
