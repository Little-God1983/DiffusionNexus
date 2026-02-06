using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.ImageEditor;
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

    // Color Balance fields
    private bool _isColorBalancePanelOpen;
    private ColorBalanceRange _selectedColorBalanceRange = ColorBalanceRange.Midtones;
    private bool _preserveLuminosity = true;
    
    // Store color balance values for each range separately
    private float _shadowsCyanRed;
    private float _shadowsMagentaGreen;
    private float _shadowsYellowBlue;
    private float _midtonesCyanRed;
    private float _midtonesMagentaGreen;
    private float _midtonesYellowBlue;
    private float _highlightsCyanRed;
    private float _highlightsMagentaGreen;
    private float _highlightsYellowBlue;

    // Brightness/Contrast fields
    private bool _isBrightnessContrastPanelOpen;
    private float _brightness;
    private float _contrast;

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

    // Drawing tool fields
    private bool _isDrawingToolActive;
    private byte _drawingBrushRed = 255;
    private byte _drawingBrushGreen = 255;
    private byte _drawingBrushBlue = 255;
    private float _drawingBrushSize = 10f;
    private ImageEditor.BrushShape _drawingBrushShape = ImageEditor.BrushShape.Round;

    // Shape tool fields
    private ImageEditor.ShapeType _selectedShapeType = ImageEditor.ShapeType.Freehand;
    private ImageEditor.ShapeFillMode _shapeFillMode = ImageEditor.ShapeFillMode.Stroke;
    private byte _shapeFillRed = 255;
    private byte _shapeFillGreen = 255;
    private byte _shapeFillBlue = 255;
    private byte _shapeStrokeRed = 255;
    private byte _shapeStrokeGreen = 255;
    private byte _shapeStrokeBlue = 255;
    private float _shapeStrokeWidth = 3f;
    private bool _hasPlacedShape;

    // Layer fields
    private bool _isLayerPanelOpen;
    private bool _isLayerMode;
    private LayerViewModel? _selectedLayer;
    private ObservableCollection<LayerViewModel> _layers = new();

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

    #region Color Balance Properties

    /// <summary>Whether the color balance panel is open.</summary>
    public bool IsColorBalancePanelOpen
    {
        get => _isColorBalancePanelOpen;
        set
        {
            if (SetProperty(ref _isColorBalancePanelOpen, value))
            {
                if (value)
                {
                    // Deactivate other tools when color balance is activated
                    DeactivateOtherTools(nameof(IsColorBalancePanelOpen));
                }
                else
                {
                    // Reset sliders and cancel preview when panel closes
                    ResetColorBalanceSliders();
                    CancelColorBalancePreviewRequested?.Invoke(this, EventArgs.Empty);
                }
                ApplyColorBalanceCommand.NotifyCanExecuteChanged();
                ResetColorBalanceRangeCommand.NotifyCanExecuteChanged();
                NotifyToolCommandsCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// The currently selected tonal range to adjust.
    /// </summary>
    public ColorBalanceRange SelectedColorBalanceRange
    {
        get => _selectedColorBalanceRange;
        set
        {
            if (SetProperty(ref _selectedColorBalanceRange, value))
            {
                OnPropertyChanged(nameof(IsShadowsSelected));
                OnPropertyChanged(nameof(IsMidtonesSelected));
                OnPropertyChanged(nameof(IsHighlightsSelected));
                // Notify that the displayed slider values have changed
                OnPropertyChanged(nameof(ColorBalanceCyanRed));
                OnPropertyChanged(nameof(ColorBalanceMagentaGreen));
                OnPropertyChanged(nameof(ColorBalanceYellowBlue));
                OnPropertyChanged(nameof(HasColorBalanceAdjustments));
                ApplyColorBalanceCommand.NotifyCanExecuteChanged();
                ResetColorBalanceRangeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether Shadows range is selected.</summary>
    public bool IsShadowsSelected
    {
        get => _selectedColorBalanceRange == ColorBalanceRange.Shadows;
        set { if (value) SelectedColorBalanceRange = ColorBalanceRange.Shadows; }
    }

    /// <summary>Whether Midtones range is selected.</summary>
    public bool IsMidtonesSelected
    {
        get => _selectedColorBalanceRange == ColorBalanceRange.Midtones;
        set { if (value) SelectedColorBalanceRange = ColorBalanceRange.Midtones; }
    }

    /// <summary>Whether Highlights range is selected.</summary>
    public bool IsHighlightsSelected
    {
        get => _selectedColorBalanceRange == ColorBalanceRange.Highlights;
        set { if (value) SelectedColorBalanceRange = ColorBalanceRange.Highlights; }
    }

    /// <summary>Cyan (-100) to Red (+100) adjustment for the current range.</summary>
    public float ColorBalanceCyanRed
    {
        get => _selectedColorBalanceRange switch
        {
            ColorBalanceRange.Shadows => _shadowsCyanRed,
            ColorBalanceRange.Midtones => _midtonesCyanRed,
            ColorBalanceRange.Highlights => _highlightsCyanRed,
            _ => 0
        };
        set
        {
            var clamped = Math.Clamp(value, -100f, 100f);
            var changed = _selectedColorBalanceRange switch
            {
                ColorBalanceRange.Shadows => SetProperty(ref _shadowsCyanRed, clamped, nameof(ColorBalanceCyanRed)),
                ColorBalanceRange.Midtones => SetProperty(ref _midtonesCyanRed, clamped, nameof(ColorBalanceCyanRed)),
                ColorBalanceRange.Highlights => SetProperty(ref _highlightsCyanRed, clamped, nameof(ColorBalanceCyanRed)),
                _ => false
            };
            if (changed)
            {
                OnPropertyChanged(nameof(HasColorBalanceAdjustments));
                ApplyColorBalanceCommand.NotifyCanExecuteChanged();
                ResetColorBalanceRangeCommand.NotifyCanExecuteChanged();
                RequestColorBalancePreview();
            }
        }
    }

    /// <summary>Magenta (-100) to Green (+100) adjustment for the current range.</summary>
    public float ColorBalanceMagentaGreen
    {
        get => _selectedColorBalanceRange switch
        {
            ColorBalanceRange.Shadows => _shadowsMagentaGreen,
            ColorBalanceRange.Midtones => _midtonesMagentaGreen,
            ColorBalanceRange.Highlights => _highlightsMagentaGreen,
            _ => 0
        };
        set
        {
            var clamped = Math.Clamp(value, -100f, 100f);
            var changed = _selectedColorBalanceRange switch
            {
                ColorBalanceRange.Shadows => SetProperty(ref _shadowsMagentaGreen, clamped, nameof(ColorBalanceMagentaGreen)),
                ColorBalanceRange.Midtones => SetProperty(ref _midtonesMagentaGreen, clamped, nameof(ColorBalanceMagentaGreen)),
                ColorBalanceRange.Highlights => SetProperty(ref _highlightsMagentaGreen, clamped, nameof(ColorBalanceMagentaGreen)),
                _ => false
            };
            if (changed)
            {
                OnPropertyChanged(nameof(HasColorBalanceAdjustments));
                ApplyColorBalanceCommand.NotifyCanExecuteChanged();
                ResetColorBalanceRangeCommand.NotifyCanExecuteChanged();
                RequestColorBalancePreview();
            }
        }
    }

    /// <summary>Yellow (-100) to Blue (+100) adjustment for the current range.</summary>
    public float ColorBalanceYellowBlue
    {
        get => _selectedColorBalanceRange switch
        {
            ColorBalanceRange.Shadows => _shadowsYellowBlue,
            ColorBalanceRange.Midtones => _midtonesYellowBlue,
            ColorBalanceRange.Highlights => _highlightsYellowBlue,
            _ => 0
        };
        set
        {
            var clamped = Math.Clamp(value, -100f, 100f);
            var changed = _selectedColorBalanceRange switch
            {
                ColorBalanceRange.Shadows => SetProperty(ref _shadowsYellowBlue, clamped, nameof(ColorBalanceYellowBlue)),
                ColorBalanceRange.Midtones => SetProperty(ref _midtonesYellowBlue, clamped, nameof(ColorBalanceYellowBlue)),
                ColorBalanceRange.Highlights => SetProperty(ref _highlightsYellowBlue, clamped, nameof(ColorBalanceYellowBlue)),
                _ => false
            };
            if (changed)
            {
                OnPropertyChanged(nameof(HasColorBalanceAdjustments));
                ApplyColorBalanceCommand.NotifyCanExecuteChanged();
                ResetColorBalanceRangeCommand.NotifyCanExecuteChanged();
                RequestColorBalancePreview();
            }
        }
    }

    /// <summary>Whether to preserve luminosity when adjusting colors.</summary>
    public bool PreserveLuminosity
    {
        get => _preserveLuminosity;
        set
        {
            if (SetProperty(ref _preserveLuminosity, value))
            {
                RequestColorBalancePreview();
            }
        }
    }

    /// <summary>Whether any color balance slider has a non-zero value (across all ranges).</summary>
    public bool HasColorBalanceAdjustments =>
        _shadowsCyanRed != 0 || _shadowsMagentaGreen != 0 || _shadowsYellowBlue != 0 ||
        _midtonesCyanRed != 0 || _midtonesMagentaGreen != 0 || _midtonesYellowBlue != 0 ||
        _highlightsCyanRed != 0 || _highlightsMagentaGreen != 0 || _highlightsYellowBlue != 0;

    private void RequestColorBalancePreview()
    {
        if (IsColorBalancePanelOpen && HasImage)
        {
            ColorBalancePreviewRequested?.Invoke(this, CurrentColorBalanceSettings);
        }
    }

    /// <summary>Gets the current color balance settings from all ranges.</summary>
    public ColorBalanceSettings CurrentColorBalanceSettings
    {
        get
        {
            return new ColorBalanceSettings
            {
                PreserveLuminosity = PreserveLuminosity,
                ShadowsCyanRed = _shadowsCyanRed,
                ShadowsMagentaGreen = _shadowsMagentaGreen,
                ShadowsYellowBlue = _shadowsYellowBlue,
                MidtonesCyanRed = _midtonesCyanRed,
                MidtonesMagentaGreen = _midtonesMagentaGreen,
                MidtonesYellowBlue = _midtonesYellowBlue,
                HighlightsCyanRed = _highlightsCyanRed,
                HighlightsMagentaGreen = _highlightsMagentaGreen,
                HighlightsYellowBlue = _highlightsYellowBlue
            };
        }
    }

    /// <summary>Resets all color balance sliders for all ranges.</summary>
    private void ResetColorBalanceSliders()
    {
        _shadowsCyanRed = 0;
        _shadowsMagentaGreen = 0;
        _shadowsYellowBlue = 0;
        _midtonesCyanRed = 0;
        _midtonesMagentaGreen = 0;
        _midtonesYellowBlue = 0;
        _highlightsCyanRed = 0;
        _highlightsMagentaGreen = 0;
        _highlightsYellowBlue = 0;
        
        OnPropertyChanged(nameof(ColorBalanceCyanRed));
        OnPropertyChanged(nameof(ColorBalanceMagentaGreen));
        OnPropertyChanged(nameof(ColorBalanceYellowBlue));
        OnPropertyChanged(nameof(HasColorBalanceAdjustments));
        ApplyColorBalanceCommand.NotifyCanExecuteChanged();
        ResetColorBalanceRangeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Resets only the current range's sliders.</summary>
    private void ResetCurrentRangeSliders()
    {
        switch (_selectedColorBalanceRange)
        {
            case ColorBalanceRange.Shadows:
                _shadowsCyanRed = 0;
                _shadowsMagentaGreen = 0;
                _shadowsYellowBlue = 0;
                break;
            case ColorBalanceRange.Midtones:
                _midtonesCyanRed = 0;
                _midtonesMagentaGreen = 0;
                _midtonesYellowBlue = 0;
                break;
            case ColorBalanceRange.Highlights:
                _highlightsCyanRed = 0;
                _highlightsMagentaGreen = 0;
                _highlightsYellowBlue = 0;
                break;
        }
        
        OnPropertyChanged(nameof(ColorBalanceCyanRed));
        OnPropertyChanged(nameof(ColorBalanceMagentaGreen));
        OnPropertyChanged(nameof(ColorBalanceYellowBlue));
        OnPropertyChanged(nameof(HasColorBalanceAdjustments));
        ApplyColorBalanceCommand.NotifyCanExecuteChanged();
        ResetColorBalanceRangeCommand.NotifyCanExecuteChanged();
        
        // Update preview with remaining adjustments
        RequestColorBalancePreview();
    }

    #endregion

    #region Brightness and Contrast Properties

    /// <summary>Whether the brightness/contrast panel is open.</summary>
    public bool IsBrightnessContrastPanelOpen
    {
        get => _isBrightnessContrastPanelOpen;
        set
        {
            if (SetProperty(ref _isBrightnessContrastPanelOpen, value))
            {
                if (value)
                {
                    // Deactivate other tools when brightness/contrast is activated
                    DeactivateOtherTools(nameof(IsBrightnessContrastPanelOpen));
                }
                else
                {
                    // Reset sliders and cancel preview when panel closes
                    ResetBrightnessContrastSliders();
                    CancelBrightnessContrastPreviewRequested?.Invoke(this, EventArgs.Empty);
                }
                ApplyBrightnessContrastCommand.NotifyCanExecuteChanged();
                ResetBrightnessContrastCommand.NotifyCanExecuteChanged();
                NotifyToolCommandsCanExecuteChanged();
            }
        }
    }

    /// <summary>Brightness adjustment from -100 (darken) to +100 (brighten).</summary>
    public float Brightness
    {
        get => _brightness;
        set
        {
            if (SetProperty(ref _brightness, Math.Clamp(value, -100f, 100f)))
            {
                OnPropertyChanged(nameof(HasBrightnessContrastAdjustments));
                ApplyBrightnessContrastCommand.NotifyCanExecuteChanged();
                ResetBrightnessContrastCommand.NotifyCanExecuteChanged();
                RequestBrightnessContrastPreview();
            }
        }
    }

    /// <summary>Contrast adjustment from -100 (reduce) to +100 (increase).</summary>
    public float Contrast
    {
        get => _contrast;
        set
        {
            if (SetProperty(ref _contrast, Math.Clamp(value, -100f, 100f)))
            {
                OnPropertyChanged(nameof(HasBrightnessContrastAdjustments));
                ApplyBrightnessContrastCommand.NotifyCanExecuteChanged();
                ResetBrightnessContrastCommand.NotifyCanExecuteChanged();
                RequestBrightnessContrastPreview();
            }
        }
    }

    /// <summary>Whether any brightness/contrast slider has a non-zero value.</summary>
    public bool HasBrightnessContrastAdjustments => _brightness != 0 || _contrast != 0;

    /// <summary>Gets the current brightness/contrast settings.</summary>
    public BrightnessContrastSettings CurrentBrightnessContrastSettings =>
        new(_brightness, _contrast);

    private void RequestBrightnessContrastPreview()
    {
        if (IsBrightnessContrastPanelOpen && HasImage)
        {
            BrightnessContrastPreviewRequested?.Invoke(this, CurrentBrightnessContrastSettings);
        }
    }

    private void ResetBrightnessContrastSliders()
    {
        _brightness = 0;
        _contrast = 0;
        OnPropertyChanged(nameof(Brightness));
        OnPropertyChanged(nameof(Contrast));
        OnPropertyChanged(nameof(HasBrightnessContrastAdjustments));
        ApplyBrightnessContrastCommand.NotifyCanExecuteChanged();
        ResetBrightnessContrastCommand.NotifyCanExecuteChanged();
    }

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

    #region Drawing Tool Properties

    /// <summary>Whether the drawing tool is active.</summary>
    public bool IsDrawingToolActive
    {
        get => _isDrawingToolActive;
        set
        {
            if (SetProperty(ref _isDrawingToolActive, value))
            {
                if (value)
                {
                    // Deactivate other tools when drawing is activated
                    DeactivateOtherTools(nameof(IsDrawingToolActive));
                }
                DrawingToolActivated?.Invoke(this, value);
                NotifyToolCommandsCanExecuteChanged();
                StatusMessage = value ? "Draw: Click and drag to draw. Hold Shift for straight lines." : null;
            }
        }
    }

    /// <summary>Red component of the brush color (0-255).</summary>
    public byte DrawingBrushRed
    {
        get => _drawingBrushRed;
        set
        {
            if (SetProperty(ref _drawingBrushRed, value))
            {
                OnPropertyChanged(nameof(DrawingBrushColor));
                OnPropertyChanged(nameof(DrawingBrushColorHex));
                DrawingSettingsChanged?.Invoke(this, CurrentDrawingSettings);
            }
        }
    }

    /// <summary>Green component of the brush color (0-255).</summary>
    public byte DrawingBrushGreen
    {
        get => _drawingBrushGreen;
        set
        {
            if (SetProperty(ref _drawingBrushGreen, value))
            {
                OnPropertyChanged(nameof(DrawingBrushColor));
                OnPropertyChanged(nameof(DrawingBrushColorHex));
                DrawingSettingsChanged?.Invoke(this, CurrentDrawingSettings);
            }
        }
    }

    /// <summary>Blue component of the brush color (0-255).</summary>
    public byte DrawingBrushBlue
    {
        get => _drawingBrushBlue;
        set
        {
            if (SetProperty(ref _drawingBrushBlue, value))
            {
                OnPropertyChanged(nameof(DrawingBrushColor));
                OnPropertyChanged(nameof(DrawingBrushColorHex));
                DrawingSettingsChanged?.Invoke(this, CurrentDrawingSettings);
            }
        }
    }

    /// <summary>The current brush color as an Avalonia Color.</summary>
    public Avalonia.Media.Color DrawingBrushColor
    {
        get => Avalonia.Media.Color.FromRgb(_drawingBrushRed, _drawingBrushGreen, _drawingBrushBlue);
        set
        {
            if (_drawingBrushRed != value.R || _drawingBrushGreen != value.G || _drawingBrushBlue != value.B)
            {
                _drawingBrushRed = value.R;
                _drawingBrushGreen = value.G;
                _drawingBrushBlue = value.B;
                OnPropertyChanged(nameof(DrawingBrushRed));
                OnPropertyChanged(nameof(DrawingBrushGreen));
                OnPropertyChanged(nameof(DrawingBrushBlue));
                OnPropertyChanged(nameof(DrawingBrushColor));
                OnPropertyChanged(nameof(DrawingBrushColorHex));
                DrawingSettingsChanged?.Invoke(this, CurrentDrawingSettings);
            }
        }
    }

    /// <summary>Hex string representation of the brush color.</summary>
    public string DrawingBrushColorHex => $"#{_drawingBrushRed:X2}{_drawingBrushGreen:X2}{_drawingBrushBlue:X2}";

    /// <summary>Brush size in pixels (1-100).</summary>
    public float DrawingBrushSize
    {
        get => _drawingBrushSize;
        set
        {
            var clamped = Math.Clamp(value, 1f, 100f);
            if (SetProperty(ref _drawingBrushSize, clamped))
            {
                OnPropertyChanged(nameof(DrawingBrushSizeText));
                DrawingSettingsChanged?.Invoke(this, CurrentDrawingSettings);
            }
        }
    }

    /// <summary>Formatted brush size for display.</summary>
    public string DrawingBrushSizeText => $"{(int)_drawingBrushSize} px";

    /// <summary>The current brush shape.</summary>
    public ImageEditor.BrushShape DrawingBrushShape
    {
        get => _drawingBrushShape;
        set
        {
            if (SetProperty(ref _drawingBrushShape, value))
            {
                OnPropertyChanged(nameof(IsRoundBrush));
                OnPropertyChanged(nameof(IsSquareBrush));
                DrawingSettingsChanged?.Invoke(this, CurrentDrawingSettings);
            }
        }
    }

    /// <summary>Whether the round brush shape is selected.</summary>
    public bool IsRoundBrush
    {
        get => _drawingBrushShape == ImageEditor.BrushShape.Round;
        set { if (value) DrawingBrushShape = ImageEditor.BrushShape.Round; }
    }

    /// <summary>Whether the square brush shape is selected.</summary>
    public bool IsSquareBrush
    {
        get => _drawingBrushShape == ImageEditor.BrushShape.Square;
        set { if (value) DrawingBrushShape = ImageEditor.BrushShape.Square; }
    }

    /// <summary>Gets the current drawing settings.</summary>
    public ImageEditor.DrawingSettings CurrentDrawingSettings => new()
    {
        Color = SkiaSharp.SKColor.FromHsl(0, 0, 0).WithRed(_drawingBrushRed).WithGreen(_drawingBrushGreen).WithBlue(_drawingBrushBlue),
        Size = _drawingBrushSize,
        Shape = _drawingBrushShape
    };

    /// <summary>Sets the brush color from a preset.</summary>
    public void SetDrawingColorPreset(string? preset)
    {
        if (preset is null) return;

        (byte r, byte g, byte b) = preset.ToUpperInvariant() switch
        {
            "WHITE" => ((byte)255, (byte)255, (byte)255),
            "BLACK" => ((byte)0, (byte)0, (byte)0),
            "RED" => ((byte)255, (byte)0, (byte)0),
            "GREEN" => ((byte)0, (byte)255, (byte)0),
            "BLUE" => ((byte)0, (byte)0, (byte)255),
            "YELLOW" => ((byte)255, (byte)255, (byte)0),
            _ => (_drawingBrushRed, _drawingBrushGreen, _drawingBrushBlue)
        };

        DrawingBrushRed = r;
        DrawingBrushGreen = g;
        DrawingBrushBlue = b;
    }

    #endregion

    #region Shape Tool Properties

    /// <summary>The currently selected shape type.</summary>
    public ImageEditor.ShapeType SelectedShapeType
    {
        get => _selectedShapeType;
        set
        {
            if (SetProperty(ref _selectedShapeType, value))
            {
                OnPropertyChanged(nameof(IsShapeFreehand));
                OnPropertyChanged(nameof(IsShapeRectangle));
                OnPropertyChanged(nameof(IsShapeEllipse));
                OnPropertyChanged(nameof(IsShapeArrow));
                OnPropertyChanged(nameof(IsShapeLine));
                OnPropertyChanged(nameof(IsShapeCross));
                OnPropertyChanged(nameof(IsShapeMode));
                ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
                UpdateDrawingModeStatus();
            }
        }
    }

    /// <summary>Whether freehand drawing is selected.</summary>
    public bool IsShapeFreehand
    {
        get => _selectedShapeType == ImageEditor.ShapeType.Freehand;
        set { if (value) SelectedShapeType = ImageEditor.ShapeType.Freehand; }
    }

    /// <summary>Whether rectangle shape is selected.</summary>
    public bool IsShapeRectangle
    {
        get => _selectedShapeType == ImageEditor.ShapeType.Rectangle;
        set { if (value) SelectedShapeType = ImageEditor.ShapeType.Rectangle; }
    }

    /// <summary>Whether ellipse shape is selected.</summary>
    public bool IsShapeEllipse
    {
        get => _selectedShapeType == ImageEditor.ShapeType.Ellipse;
        set { if (value) SelectedShapeType = ImageEditor.ShapeType.Ellipse; }
    }

    /// <summary>Whether arrow shape is selected.</summary>
    public bool IsShapeArrow
    {
        get => _selectedShapeType == ImageEditor.ShapeType.Arrow;
        set { if (value) SelectedShapeType = ImageEditor.ShapeType.Arrow; }
    }

    /// <summary>Whether line shape is selected.</summary>
    public bool IsShapeLine
    {
        get => _selectedShapeType == ImageEditor.ShapeType.Line;
        set { if (value) SelectedShapeType = ImageEditor.ShapeType.Line; }
    }

    /// <summary>Whether cross/X shape is selected.</summary>
    public bool IsShapeCross
    {
        get => _selectedShapeType == ImageEditor.ShapeType.Cross;
        set { if (value) SelectedShapeType = ImageEditor.ShapeType.Cross; }
    }

    /// <summary>Whether a shape mode (not freehand) is selected.</summary>
    public bool IsShapeMode => _selectedShapeType != ImageEditor.ShapeType.Freehand;

    /// <summary>The shape fill mode.</summary>
    public ImageEditor.ShapeFillMode ShapeFillMode
    {
        get => _shapeFillMode;
        set
        {
            if (SetProperty(ref _shapeFillMode, value))
            {
                OnPropertyChanged(nameof(IsShapeStrokeOnly));
                OnPropertyChanged(nameof(IsShapeFillOnly));
                OnPropertyChanged(nameof(IsShapeFillAndStroke));
                ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Whether stroke only mode is selected.</summary>
    public bool IsShapeStrokeOnly
    {
        get => _shapeFillMode == ImageEditor.ShapeFillMode.Stroke;
        set { if (value) ShapeFillMode = ImageEditor.ShapeFillMode.Stroke; }
    }

    /// <summary>Whether fill only mode is selected.</summary>
    public bool IsShapeFillOnly
    {
        get => _shapeFillMode == ImageEditor.ShapeFillMode.Fill;
        set { if (value) ShapeFillMode = ImageEditor.ShapeFillMode.Fill; }
    }

    /// <summary>Whether fill and stroke mode is selected.</summary>
    public bool IsShapeFillAndStroke
    {
        get => _shapeFillMode == ImageEditor.ShapeFillMode.FillAndStroke;
        set { if (value) ShapeFillMode = ImageEditor.ShapeFillMode.FillAndStroke; }
    }

    /// <summary>Red component of the shape fill color (0-255).</summary>
    public byte ShapeFillRed
    {
        get => _shapeFillRed;
        set
        {
            if (SetProperty(ref _shapeFillRed, value))
            {
                OnPropertyChanged(nameof(ShapeFillColor));
                OnPropertyChanged(nameof(ShapeFillColorHex));
                ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Green component of the shape fill color (0-255).</summary>
    public byte ShapeFillGreen
    {
        get => _shapeFillGreen;
        set
        {
            if (SetProperty(ref _shapeFillGreen, value))
            {
                OnPropertyChanged(nameof(ShapeFillColor));
                OnPropertyChanged(nameof(ShapeFillColorHex));
                ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Blue component of the shape fill color (0-255).</summary>
    public byte ShapeFillBlue
    {
        get => _shapeFillBlue;
        set
        {
            if (SetProperty(ref _shapeFillBlue, value))
            {
                OnPropertyChanged(nameof(ShapeFillColor));
                OnPropertyChanged(nameof(ShapeFillColorHex));
                ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The shape fill color as an Avalonia Color.</summary>
    public Avalonia.Media.Color ShapeFillColor
    {
        get => Avalonia.Media.Color.FromRgb(_shapeFillRed, _shapeFillGreen, _shapeFillBlue);
        set
        {
            if (_shapeFillRed != value.R || _shapeFillGreen != value.G || _shapeFillBlue != value.B)
            {
                _shapeFillRed = value.R;
                _shapeFillGreen = value.G;
                _shapeFillBlue = value.B;
                OnPropertyChanged(nameof(ShapeFillRed));
                OnPropertyChanged(nameof(ShapeFillGreen));
                OnPropertyChanged(nameof(ShapeFillBlue));
                OnPropertyChanged(nameof(ShapeFillColor));
                OnPropertyChanged(nameof(ShapeFillColorHex));
                ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Hex string representation of the fill color.</summary>
    public string ShapeFillColorHex => $"#{_shapeFillRed:X2}{_shapeFillGreen:X2}{_shapeFillBlue:X2}";

    /// <summary>Red component of the shape stroke color (0-255).</summary>
    public byte ShapeStrokeRed
    {
        get => _shapeStrokeRed;
        set
        {
            if (SetProperty(ref _shapeStrokeRed, value))
            {
                OnPropertyChanged(nameof(ShapeStrokeColor));
                OnPropertyChanged(nameof(ShapeStrokeColorHex));
                ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Green component of the shape stroke color (0-255).</summary>
    public byte ShapeStrokeGreen
    {
        get => _shapeStrokeGreen;
        set
        {
            if (SetProperty(ref _shapeStrokeGreen, value))
            {
                OnPropertyChanged(nameof(ShapeStrokeColor));
                OnPropertyChanged(nameof(ShapeStrokeColorHex));
                ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Blue component of the shape stroke color (0-255).</summary>
    public byte ShapeStrokeBlue
    {
        get => _shapeStrokeBlue;
        set
        {
            if (SetProperty(ref _shapeStrokeBlue, value))
            {
                OnPropertyChanged(nameof(ShapeStrokeColor));
                OnPropertyChanged(nameof(ShapeStrokeColorHex));
                ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The shape stroke color as an Avalonia Color.</summary>
    public Avalonia.Media.Color ShapeStrokeColor
    {
        get => Avalonia.Media.Color.FromRgb(_shapeStrokeRed, _shapeStrokeGreen, _shapeStrokeBlue);
        set
        {
            if (_shapeStrokeRed != value.R || _shapeStrokeGreen != value.G || _shapeStrokeBlue != value.B)
            {
                _shapeStrokeRed = value.R;
                _shapeStrokeGreen = value.G;
                _shapeStrokeBlue = value.B;
                OnPropertyChanged(nameof(ShapeStrokeRed));
                OnPropertyChanged(nameof(ShapeStrokeGreen));
                OnPropertyChanged(nameof(ShapeStrokeBlue));
                OnPropertyChanged(nameof(ShapeStrokeColor));
                OnPropertyChanged(nameof(ShapeStrokeColorHex));
                ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Hex string representation of the stroke color.</summary>
    public string ShapeStrokeColorHex => $"#{_shapeStrokeRed:X2}{_shapeStrokeGreen:X2}{_shapeStrokeBlue:X2}";

    /// <summary>Shape stroke width in pixels (1-50).</summary>
    public float ShapeStrokeWidth
    {
        get => _shapeStrokeWidth;
        set
        {
            var clamped = Math.Clamp(value, 1f, 50f);
            if (SetProperty(ref _shapeStrokeWidth, clamped))
            {
                OnPropertyChanged(nameof(ShapeStrokeWidthText));
                ShapeSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Formatted stroke width for display.</summary>
    public string ShapeStrokeWidthText => $"{(int)_shapeStrokeWidth} px";

    /// <summary>Sets the shape fill color from a preset.</summary>
    public void SetShapeFillPreset(string? preset)
    {
        if (preset is null) return;

        (byte r, byte g, byte b) = preset.ToUpperInvariant() switch
        {
            "WHITE" => ((byte)255, (byte)255, (byte)255),
            "BLACK" => ((byte)0, (byte)0, (byte)0),
            "RED" => ((byte)255, (byte)0, (byte)0),
            "GREEN" => ((byte)0, (byte)255, (byte)0),
            "BLUE" => ((byte)0, (byte)0, (byte)255),
            "YELLOW" => ((byte)255, (byte)255, (byte)0),
            "ORANGE" => ((byte)255, (byte)165, (byte)0),
            "PURPLE" => ((byte)128, (byte)0, (byte)128),
            "CYAN" => ((byte)0, (byte)255, (byte)255),
            "MAGENTA" => ((byte)255, (byte)0, (byte)255),
            "GRAY" or "GREY" => ((byte)128, (byte)128, (byte)128),
            "TRANSPARENT" or "NONE" => ((byte)0, (byte)0, (byte)0),
            _ => (_shapeFillRed, _shapeFillGreen, _shapeFillBlue)
        };

        ShapeFillRed = r;
        ShapeFillGreen = g;
        ShapeFillBlue = b;
    }

    /// <summary>Sets the shape stroke color from a preset.</summary>
    public void SetShapeStrokePreset(string? preset)
    {
        if (preset is null) return;

        (byte r, byte g, byte b) = preset.ToUpperInvariant() switch
        {
            "WHITE" => ((byte)255, (byte)255, (byte)255),
            "BLACK" => ((byte)0, (byte)0, (byte)0),
            "RED" => ((byte)255, (byte)0, (byte)0),
            "GREEN" => ((byte)0, (byte)255, (byte)0),
            "BLUE" => ((byte)0, (byte)0, (byte)255),
            "YELLOW" => ((byte)255, (byte)255, (byte)0),
            "ORANGE" => ((byte)255, (byte)165, (byte)0),
            "PURPLE" => ((byte)128, (byte)0, (byte)128),
            "CYAN" => ((byte)0, (byte)255, (byte)255),
            "MAGENTA" => ((byte)255, (byte)0, (byte)255),
            "GRAY" or "GREY" => ((byte)128, (byte)128, (byte)128),
            _ => (_shapeStrokeRed, _shapeStrokeGreen, _shapeStrokeBlue)
        };

        ShapeStrokeRed = r;
        ShapeStrokeGreen = g;
        ShapeStrokeBlue = b;
    }

    /// <summary>Event raised when shape settings change.</summary>
    public event EventHandler? ShapeSettingsChanged;

    /// <summary>Whether a shape is currently placed and awaiting commit/cancel.</summary>
    public bool HasPlacedShape
    {
        get => _hasPlacedShape;
        set
        {
            if (SetProperty(ref _hasPlacedShape, value))
            {
                CommitPlacedShapeCommand.NotifyCanExecuteChanged();
                CancelPlacedShapeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Event raised when the ViewModel requests committing the placed shape.</summary>
    public event EventHandler? CommitPlacedShapeRequested;

    /// <summary>Event raised when the ViewModel requests cancelling the placed shape.</summary>
    public event EventHandler? CancelPlacedShapeRequested;

    /// <summary>Command to commit the placed shape to the image.</summary>
    public RelayCommand CommitPlacedShapeCommand { get; private set; } = null!;

    /// <summary>Command to cancel the placed shape.</summary>
    public RelayCommand CancelPlacedShapeCommand { get; private set; } = null!;

    private void UpdateDrawingModeStatus()
    {
        if (!IsDrawingToolActive) return;

        StatusMessage = _selectedShapeType switch
        {
            ImageEditor.ShapeType.Freehand => "Draw: Click and drag to draw. Hold Shift for straight lines.",
            ImageEditor.ShapeType.Rectangle => "Rectangle: Click and drag to draw a rectangle.",
            ImageEditor.ShapeType.Ellipse => "Ellipse: Click and drag to draw an ellipse.",
            ImageEditor.ShapeType.Arrow => "Arrow: Click and drag to draw an arrow.",
            ImageEditor.ShapeType.Line => "Line: Click and drag to draw a straight line.",
            _ => null
        };
    }

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

    #region Layer Properties

    /// <summary>Whether the layer panel is open (always true - panel is always visible).</summary>
    public bool IsLayerPanelOpen
    {
        get => true;
        set { } // No-op, panel is always visible
    }

    /// <summary>Whether layer mode is enabled.</summary>
    public bool IsLayerMode
    {
        get => _isLayerMode;
        set
        {
            if (SetProperty(ref _isLayerMode, value))
            {
                NotifyLayerCommandsCanExecuteChanged();
            }
        }
    }

    /// <summary>Collection of layer view models.</summary>
    public ObservableCollection<LayerViewModel> Layers
    {
        get => _layers;
        set => SetProperty(ref _layers, value);
    }

    /// <summary>Currently selected layer.</summary>
    public LayerViewModel? SelectedLayer
    {
        get => _selectedLayer;
        set
        {
            if (SetProperty(ref _selectedLayer, value))
            {
                // Update selection state on all layers
                foreach (var layer in _layers)
                {
                    layer.IsSelected = layer == value;
                }
                NotifyLayerCommandsCanExecuteChanged();
                LayerSelectionChanged?.Invoke(this, value?.Layer);
            }
        }
    }

    /// <summary>Whether the selected layer can be moved up (towards the top of the visual list).</summary>
    public bool CanMoveLayerUp
    {
        get
        {
            if (_selectedLayer == null) return false;
            var index = _layers.IndexOf(_selectedLayer);
            // In the UI, "up" means towards index 0 (top of list)
            return index > 0;
        }
    }

    /// <summary>Whether the selected layer can be moved down (towards the bottom of the visual list).</summary>
    public bool CanMoveLayerDown
    {
        get
        {
            if (_selectedLayer == null) return false;
            var index = _layers.IndexOf(_selectedLayer);
            // In the UI, "down" means towards higher indices (bottom of list)
            return index < _layers.Count - 1;
        }
    }

    /// <summary>Whether the selected layer can be merged down (with the layer below in the visual list).</summary>
    public bool CanMergeDown
    {
        get
        {
            if (_selectedLayer == null) return false;
            var index = _layers.IndexOf(_selectedLayer);
            // In the UI, "down" means towards higher ViewModel indices (bottom of visual list)
            // A layer can merge down if there's a layer below it
            return index < _layers.Count - 1;
        }
    }

    /// <summary>Event raised when layer selection changes.</summary>
    public event EventHandler<Layer?>? LayerSelectionChanged;

    /// <summary>Event raised when layers need to be synchronized.</summary>
    public event EventHandler? SyncLayersRequested;

    /// <summary>Event raised when a layered TIFF save is requested.</summary>
    public event Func<string, Task<bool>>? SaveLayeredTiffRequested;

    private void NotifyLayerCommandsCanExecuteChanged()
    {
        AddLayerCommand?.NotifyCanExecuteChanged();
        DeleteLayerCommand?.NotifyCanExecuteChanged();
        DuplicateLayerCommand?.NotifyCanExecuteChanged();
        MoveLayerUpCommand?.NotifyCanExecuteChanged();
        MoveLayerDownCommand?.NotifyCanExecuteChanged();
        MergeLayerDownCommand?.NotifyCanExecuteChanged();
        MergeVisibleLayersCommand?.NotifyCanExecuteChanged();
        FlattenLayersCommand?.NotifyCanExecuteChanged();
        SaveLayeredTiffCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanMoveLayerUp));
        OnPropertyChanged(nameof(CanMoveLayerDown));
        OnPropertyChanged(nameof(CanMergeDown));
    }

    /// <summary>
    /// Synchronizes the layer view models with the editor core's layer stack.
    /// </summary>
    public void SyncLayers(LayerStack? layerStack)
    {
        // Clear existing
        foreach (var vm in _layers)
        {
            vm.Dispose();
        }
        _layers.Clear();

        if (layerStack == null || layerStack.Count == 0)
        {
            SelectedLayer = null;
            return;
        }

        // Create view models for each layer (reversed for UI - top layer first)
        for (var i = layerStack.Count - 1; i >= 0; i--)
        {
            var layer = layerStack[i];
            var vm = new LayerViewModel(layer, OnLayerSelectionRequested, OnLayerDeleteRequested);
            _layers.Add(vm);
        }

        // Select active layer
        if (layerStack.ActiveLayer != null)
        {
            var activeVm = _layers.FirstOrDefault(vm => vm.Layer == layerStack.ActiveLayer);
            SelectedLayer = activeVm;
        }
        else if (_layers.Count > 0)
        {
            SelectedLayer = _layers[0];
        }
    }

    private void OnLayerSelectionRequested(LayerViewModel vm)
    {
        SelectedLayer = vm;
    }

    private void OnLayerDeleteRequested(LayerViewModel vm)
    {
        if (_layers.Count <= 1) return;
        ExecuteDeleteLayer();
    }

    #endregion Layer Properties

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
    public IRelayCommand ToggleColorBalanceCommand { get; }
    public IRelayCommand ApplyColorBalanceCommand { get; }
    public IRelayCommand ResetColorBalanceRangeCommand { get; }
    public IRelayCommand ToggleBrightnessContrastCommand { get; }
    public IRelayCommand ApplyBrightnessContrastCommand { get; }
    public IRelayCommand ResetBrightnessContrastCommand { get; }
    public IAsyncRelayCommand RemoveBackgroundCommand { get; }
    public IAsyncRelayCommand RemoveBackgroundToLayerCommand { get; }
    public IAsyncRelayCommand DownloadBackgroundRemovalModelCommand { get; }
    public IRelayCommand ToggleBackgroundFillCommand { get; }
    public IRelayCommand ApplyBackgroundFillCommand { get; }
    public IRelayCommand<string> SetBackgroundFillPresetCommand { get; }
    public IAsyncRelayCommand UpscaleImageCommand { get; }
    public IAsyncRelayCommand DownloadUpscalingModelCommand { get; }
    public IRelayCommand ToggleDrawingToolCommand { get; }
    public IRelayCommand<string> SetDrawingColorPresetCommand { get; }
    public IRelayCommand<string> SetShapeFillPresetCommand { get; }
    public IRelayCommand<string> SetShapeStrokePresetCommand { get; }

    // Layer commands
    public IRelayCommand ToggleLayerModeCommand { get; }
    public IRelayCommand AddLayerCommand { get; }
    public IRelayCommand DeleteLayerCommand { get; }
    public IRelayCommand DuplicateLayerCommand { get; }
    public IRelayCommand MoveLayerUpCommand { get; }
    public IRelayCommand MoveLayerDownCommand { get; }
    public IRelayCommand MergeLayerDownCommand { get; }
    public IRelayCommand MergeVisibleLayersCommand { get; }
    public IRelayCommand FlattenLayersCommand { get; }
    public IAsyncRelayCommand SaveLayeredTiffCommand { get; }

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

        // Add more tools here as they are added in the future
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
        ExportCommand.NotifyCanExecuteChanged();
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
    public ImageEditorViewModel(
        IDatasetEventAggregator? eventAggregator = null,
        IBackgroundRemovalService? backgroundRemovalService = null,
        IImageUpscalingService? upscalingService = null)
    {
        _eventAggregator = eventAggregator;
        _backgroundRemovalService = backgroundRemovalService;
        _upscalingService = upscalingService;

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

        // Layer commands (layers are always enabled when an image is loaded)
        ToggleLayerModeCommand = new RelayCommand(ExecuteToggleLayerMode, () => HasImage);
        AddLayerCommand = new RelayCommand(ExecuteAddLayer, () => HasImage);
        DeleteLayerCommand = new RelayCommand(ExecuteDeleteLayer, () => HasImage && SelectedLayer != null && Layers.Count > 1);
        DuplicateLayerCommand = new RelayCommand(ExecuteDuplicateLayer, () => HasImage && SelectedLayer != null);
        MoveLayerUpCommand = new RelayCommand(ExecuteMoveLayerUp, () => HasImage && SelectedLayer != null && CanMoveLayerUp);
        MoveLayerDownCommand = new RelayCommand(ExecuteMoveLayerDown, () => HasImage && SelectedLayer != null && CanMoveLayerDown);
        MergeLayerDownCommand = new RelayCommand(ExecuteMergeLayerDown, () => HasImage && SelectedLayer != null && CanMergeDown);
        MergeVisibleLayersCommand = new RelayCommand(ExecuteMergeVisibleLayers, () => HasImage && Layers.Count > 1);
        FlattenLayersCommand = new RelayCommand(ExecuteFlattenLayers, () => HasImage && Layers.Count > 1);
        SaveLayeredTiffCommand = new AsyncRelayCommand(ExecuteSaveLayeredTiffAsync, () => HasImage);
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

    private void ExecuteZoomIn() => ZoomInRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteZoomOut() => ZoomOutRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteZoomToFit() => ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteZoomToActual() => ZoomToActualRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteRotateLeft() => RotateLeftRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteRotateRight() => RotateRightRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteRotate180() => Rotate180Requested?.Invoke(this, EventArgs.Empty);
    private void ExecuteFlipHorizontal() => FlipHorizontalRequested?.Invoke(this, EventArgs.Empty);
    private void ExecuteFlipVertical() => FlipVerticalRequested?.Invoke(this, EventArgs.Empty);

    private void ExecuteToggleColorBalance()
    {
        IsColorBalancePanelOpen = !IsColorBalancePanelOpen;
    }

    private void ExecuteApplyColorBalance()
    {
        ApplyColorBalanceRequested?.Invoke(this, CurrentColorBalanceSettings);
    }

    private void ExecuteResetColorBalanceRange()
    {
        ResetCurrentRangeSliders();
    }

    private void ExecuteToggleBrightnessContrast()
    {
        IsBrightnessContrastPanelOpen = !IsBrightnessContrastPanelOpen;
    }

    private void ExecuteApplyBrightnessContrast()
    {
        ApplyBrightnessContrastRequested?.Invoke(this, CurrentBrightnessContrastSettings);
    }

    private void ExecuteResetBrightnessContrast()
    {
        ResetBrightnessContrastSliders();
        CancelBrightnessContrastPreviewRequested?.Invoke(this, EventArgs.Empty);
    }

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
    }

    private void ExecuteApplyBackgroundFill()
    {
        ApplyBackgroundFillRequested?.Invoke(this, CurrentBackgroundFillSettings);
    }

    private void ExecuteToggleDrawingTool()
    {
        IsDrawingToolActive = !IsDrawingToolActive;
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
