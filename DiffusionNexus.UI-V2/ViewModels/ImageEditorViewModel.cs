using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.ImageEditor;
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
    /// Event raised when an image save completes successfully.
    /// The string parameter contains the saved file path.
    /// </summary>
    public event EventHandler<string>? ImageSaved;

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
        ToggleColorBalanceCommand.NotifyCanExecuteChanged();
        ApplyColorBalanceCommand.NotifyCanExecuteChanged();
        ResetColorBalanceRangeCommand.NotifyCanExecuteChanged();
        ApplyBrightnessContrastCommand.NotifyCanExecuteChanged();
        ResetBrightnessContrastCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Creates a new ImageEditorViewModel with event aggregator integration.
    /// </summary>
    /// <param name="eventAggregator">The event aggregator for publishing events.</param>
    public ImageEditorViewModel(IDatasetEventAggregator? eventAggregator = null)
    {
        _eventAggregator = eventAggregator;

        ClearImageCommand = new RelayCommand(ExecuteClearImage, () => HasImage);
        ResetImageCommand = new RelayCommand(ExecuteResetImage, () => HasImage);
        ToggleCropToolCommand = new RelayCommand(ExecuteToggleCropTool, () => HasImage && !IsColorBalancePanelOpen);
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
    }

    /// <summary>Loads an image by path.</summary>
    public void LoadImage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            StatusMessage = "Image file not found.";
            return;
        }

        // Close any active tools before loading a new image
        CloseAllTools();

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

        NotifyToolCommandsCanExecuteChanged();
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
        // Close any active tools first
        CloseAllTools();
        
        CurrentImagePath = null;
        SelectedDatasetImage = null;
        ImageWidth = 0;
        ImageHeight = 0;
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

    #region Transform Command Implementations

    private void ExecuteRotateLeft()
    {
        RotateLeftRequested?.Invoke(this, EventArgs.Empty);
        StatusMessage = "Rotated 90° left";
    }

    private void ExecuteRotateRight()
    {
        RotateRightRequested?.Invoke(this, EventArgs.Empty);
        StatusMessage = "Rotated 90° right";
    }

    private void ExecuteRotate180()
    {
        Rotate180Requested?.Invoke(this, EventArgs.Empty);
        StatusMessage = "Rotated 180°";
    }

    private void ExecuteFlipHorizontal()
    {
        FlipHorizontalRequested?.Invoke(this, EventArgs.Empty);
        StatusMessage = "Flipped horizontally";
    }

    private void ExecuteFlipVertical()
    {
        FlipVerticalRequested?.Invoke(this, EventArgs.Empty);
        StatusMessage = "Flipped vertically";
    }

    #endregion

    #region Color Balance Command Implementations

    private void ExecuteToggleColorBalance()
    {
        IsColorBalancePanelOpen = !IsColorBalancePanelOpen;
        if (IsColorBalancePanelOpen)
        {
            StatusMessage = "Color Balance: Adjust sliders and click Apply";
        }
        else
        {
            StatusMessage = null;
        }
        ApplyColorBalanceCommand.NotifyCanExecuteChanged();
        ResetColorBalanceRangeCommand.NotifyCanExecuteChanged();
    }

    private void ExecuteApplyColorBalance()
    {
        var settings = CurrentColorBalanceSettings;
        ApplyColorBalanceRequested?.Invoke(this, settings);
        StatusMessage = $"Color balance applied to {_selectedDatasetImage?.FileName}";
        ResetColorBalanceSliders();
    }

    private void ExecuteResetColorBalanceRange()
    {
        ResetCurrentRangeSliders();
        StatusMessage = $"Reset {_selectedColorBalanceRange} color balance";
    }

    /// <summary>Called when color balance is successfully applied.</summary>
    public void OnColorBalanceApplied()
    {
        StatusMessage = "Color balance applied";
    }

    #endregion

    #region Brightness and Contrast Command Implementations

    private void ExecuteToggleBrightnessContrast()
    {
        IsBrightnessContrastPanelOpen = !IsBrightnessContrastPanelOpen;
        if (IsBrightnessContrastPanelOpen)
        {
            StatusMessage = "Brightness/Contrast: Adjust sliders and click Apply";
        }
        else
        {
            StatusMessage = null;
        }
        ApplyBrightnessContrastCommand.NotifyCanExecuteChanged();
        ResetBrightnessContrastCommand.NotifyCanExecuteChanged();
    }

    private void ExecuteApplyBrightnessContrast()
    {
        var settings = CurrentBrightnessContrastSettings;
        ApplyBrightnessContrastRequested?.Invoke(this, settings);
        StatusMessage = "Brightness and contrast applied";
        ResetBrightnessContrastSliders();
    }

    private void ExecuteResetBrightnessContrast()
    {
        ResetBrightnessContrastSliders();
        StatusMessage = "Reset brightness and contrast";
    }

    /// <summary>Called when brightness/contrast is successfully applied.</summary>
    public void OnBrightnessContrastApplied()
    {
        StatusMessage = "Brightness and contrast applied";
    }

    #endregion

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
