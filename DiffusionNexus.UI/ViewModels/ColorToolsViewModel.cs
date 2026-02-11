using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.ImageEditor;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Sub-ViewModel managing color balance and brightness/contrast tool state.
/// Extracted from <see cref="ImageEditorViewModel"/> to reduce its size.
/// </summary>
public partial class ColorToolsViewModel : ObservableObject
{
    private readonly Func<bool> _hasImage;
    private readonly Action<string> _deactivateOtherTools;

    // Color Balance fields
    private bool _isColorBalancePanelOpen;
    private ColorBalanceRange _selectedColorBalanceRange = ColorBalanceRange.Midtones;
    private bool _preserveLuminosity = true;
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

    public ColorToolsViewModel(Func<bool> hasImage, Action<string> deactivateOtherTools)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);
        _hasImage = hasImage;
        _deactivateOtherTools = deactivateOtherTools;

        // Color Balance commands
        ToggleColorBalanceCommand = new RelayCommand(ExecuteToggleColorBalance, () => _hasImage() && !IsCropToolActive);
        ApplyColorBalanceCommand = new RelayCommand(ExecuteApplyColorBalance, () => _hasImage() && IsColorBalancePanelOpen && HasColorBalanceAdjustments);
        ResetColorBalanceRangeCommand = new RelayCommand(ExecuteResetColorBalanceRange, () => IsColorBalancePanelOpen && HasColorBalanceAdjustments);

        // Brightness/Contrast commands
        ToggleBrightnessContrastCommand = new RelayCommand(ExecuteToggleBrightnessContrast, () => _hasImage() && !IsColorBalancePanelOpen);
        ApplyBrightnessContrastCommand = new RelayCommand(ExecuteApplyBrightnessContrast, () => _hasImage() && IsBrightnessContrastPanelOpen && HasBrightnessContrastAdjustments);
        ResetBrightnessContrastCommand = new RelayCommand(ExecuteResetBrightnessContrast, () => IsBrightnessContrastPanelOpen && HasBrightnessContrastAdjustments);
    }

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
                    _deactivateOtherTools(nameof(IsColorBalancePanelOpen));
                }
                else
                {
                    ResetColorBalanceSliders();
                    CancelColorBalancePreviewRequested?.Invoke(this, EventArgs.Empty);
                }
                ApplyColorBalanceCommand.NotifyCanExecuteChanged();
                ResetColorBalanceRangeCommand.NotifyCanExecuteChanged();
                ToolStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The currently selected tonal range to adjust.</summary>
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
            if (changed) OnSliderChanged();
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
            if (changed) OnSliderChanged();
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
            if (changed) OnSliderChanged();
        }
    }

    /// <summary>Whether to preserve luminosity when adjusting colors.</summary>
    public bool PreserveLuminosity
    {
        get => _preserveLuminosity;
        set
        {
            if (SetProperty(ref _preserveLuminosity, value))
                RequestColorBalancePreview();
        }
    }

    /// <summary>Whether any color balance slider has a non-zero value (across all ranges).</summary>
    public bool HasColorBalanceAdjustments =>
        _shadowsCyanRed != 0 || _shadowsMagentaGreen != 0 || _shadowsYellowBlue != 0 ||
        _midtonesCyanRed != 0 || _midtonesMagentaGreen != 0 || _midtonesYellowBlue != 0 ||
        _highlightsCyanRed != 0 || _highlightsMagentaGreen != 0 || _highlightsYellowBlue != 0;

    /// <summary>Gets the current color balance settings from all ranges.</summary>
    public ColorBalanceSettings CurrentColorBalanceSettings => new()
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

    #endregion

    #region Brightness/Contrast Properties

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
                    _deactivateOtherTools(nameof(IsBrightnessContrastPanelOpen));
                }
                else
                {
                    ResetBrightnessContrastSliders();
                    CancelBrightnessContrastPreviewRequested?.Invoke(this, EventArgs.Empty);
                }
                ApplyBrightnessContrastCommand.NotifyCanExecuteChanged();
                ResetBrightnessContrastCommand.NotifyCanExecuteChanged();
                ToolStateChanged?.Invoke(this, EventArgs.Empty);
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
    public BrightnessContrastSettings CurrentBrightnessContrastSettings => new(_brightness, _contrast);

    #endregion

    #region Commands

    public IRelayCommand ToggleColorBalanceCommand { get; }
    public IRelayCommand ApplyColorBalanceCommand { get; }
    public IRelayCommand ResetColorBalanceRangeCommand { get; }
    public IRelayCommand ToggleBrightnessContrastCommand { get; }
    public IRelayCommand ApplyBrightnessContrastCommand { get; }
    public IRelayCommand ResetBrightnessContrastCommand { get; }

    #endregion

    #region Events

    /// <summary>Raised when color balance should be applied permanently.</summary>
    public event EventHandler<ColorBalanceSettings>? ApplyColorBalanceRequested;

    /// <summary>Raised to request a color balance live preview.</summary>
    public event EventHandler<ColorBalanceSettings>? ColorBalancePreviewRequested;

    /// <summary>Raised to cancel the color balance preview.</summary>
    public event EventHandler? CancelColorBalancePreviewRequested;

    /// <summary>Raised when brightness/contrast should be applied permanently.</summary>
    public event EventHandler<BrightnessContrastSettings>? ApplyBrightnessContrastRequested;

    /// <summary>Raised to request a brightness/contrast live preview.</summary>
    public event EventHandler<BrightnessContrastSettings>? BrightnessContrastPreviewRequested;

    /// <summary>Raised to cancel the brightness/contrast preview.</summary>
    public event EventHandler? CancelBrightnessContrastPreviewRequested;

    /// <summary>Raised when tool activation state changes (for parent ViewModel notification).</summary>
    public event EventHandler? ToolStateChanged;

    /// <summary>Raised when a tool is toggled via the ToolManager.</summary>
    public event EventHandler<(string ToolId, bool IsActive)>? ToolToggled;

    #endregion

    #region Public State for Parent

    /// <summary>
    /// Whether the crop tool is active. Set by the parent ViewModel so the
    /// ToggleColorBalance command can check mutual exclusion.
    /// </summary>
    internal bool IsCropToolActive { get; set; }

    /// <summary>
    /// Notifies all commands that their CanExecute state may have changed.
    /// </summary>
    public void RefreshCommandStates()
    {
        ToggleColorBalanceCommand.NotifyCanExecuteChanged();
        ApplyColorBalanceCommand.NotifyCanExecuteChanged();
        ResetColorBalanceRangeCommand.NotifyCanExecuteChanged();
        ToggleBrightnessContrastCommand.NotifyCanExecuteChanged();
        ApplyBrightnessContrastCommand.NotifyCanExecuteChanged();
        ResetBrightnessContrastCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Called when color balance is applied successfully.
    /// </summary>
    public void OnColorBalanceApplied()
    {
        IsColorBalancePanelOpen = false;
    }

    /// <summary>
    /// Called when brightness/contrast is applied successfully.
    /// </summary>
    public void OnBrightnessContrastApplied()
    {
        IsBrightnessContrastPanelOpen = false;
    }

    /// <summary>
    /// Closes any open color tool panels. Called by the parent when clearing/resetting.
    /// </summary>
    public void CloseAllPanels()
    {
        if (_isColorBalancePanelOpen)
        {
            _isColorBalancePanelOpen = false;
            ResetColorBalanceSliders();
            CancelColorBalancePreviewRequested?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(IsColorBalancePanelOpen));
        }

        if (_isBrightnessContrastPanelOpen)
        {
            _isBrightnessContrastPanelOpen = false;
            ResetBrightnessContrastSliders();
            CancelBrightnessContrastPreviewRequested?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(IsBrightnessContrastPanelOpen));
        }
    }

    #endregion

    #region Private Methods

    private void OnSliderChanged()
    {
        OnPropertyChanged(nameof(HasColorBalanceAdjustments));
        ApplyColorBalanceCommand.NotifyCanExecuteChanged();
        ResetColorBalanceRangeCommand.NotifyCanExecuteChanged();
        RequestColorBalancePreview();
    }

    private void RequestColorBalancePreview()
    {
        if (IsColorBalancePanelOpen && _hasImage())
            ColorBalancePreviewRequested?.Invoke(this, CurrentColorBalanceSettings);
    }

    private void RequestBrightnessContrastPreview()
    {
        if (IsBrightnessContrastPanelOpen && _hasImage())
            BrightnessContrastPreviewRequested?.Invoke(this, CurrentBrightnessContrastSettings);
    }

    private void ResetColorBalanceSliders()
    {
        _shadowsCyanRed = 0; _shadowsMagentaGreen = 0; _shadowsYellowBlue = 0;
        _midtonesCyanRed = 0; _midtonesMagentaGreen = 0; _midtonesYellowBlue = 0;
        _highlightsCyanRed = 0; _highlightsMagentaGreen = 0; _highlightsYellowBlue = 0;

        OnPropertyChanged(nameof(ColorBalanceCyanRed));
        OnPropertyChanged(nameof(ColorBalanceMagentaGreen));
        OnPropertyChanged(nameof(ColorBalanceYellowBlue));
        OnPropertyChanged(nameof(HasColorBalanceAdjustments));
        ApplyColorBalanceCommand.NotifyCanExecuteChanged();
        ResetColorBalanceRangeCommand.NotifyCanExecuteChanged();
    }

    private void ResetCurrentRangeSliders()
    {
        switch (_selectedColorBalanceRange)
        {
            case ColorBalanceRange.Shadows:
                _shadowsCyanRed = 0; _shadowsMagentaGreen = 0; _shadowsYellowBlue = 0;
                break;
            case ColorBalanceRange.Midtones:
                _midtonesCyanRed = 0; _midtonesMagentaGreen = 0; _midtonesYellowBlue = 0;
                break;
            case ColorBalanceRange.Highlights:
                _highlightsCyanRed = 0; _highlightsMagentaGreen = 0; _highlightsYellowBlue = 0;
                break;
        }

        OnPropertyChanged(nameof(ColorBalanceCyanRed));
        OnPropertyChanged(nameof(ColorBalanceMagentaGreen));
        OnPropertyChanged(nameof(ColorBalanceYellowBlue));
        OnPropertyChanged(nameof(HasColorBalanceAdjustments));
        ApplyColorBalanceCommand.NotifyCanExecuteChanged();
        ResetColorBalanceRangeCommand.NotifyCanExecuteChanged();
        RequestColorBalancePreview();
    }

    private void ResetBrightnessContrastSliders()
    {
        _brightness = 0; _contrast = 0;
        OnPropertyChanged(nameof(Brightness));
        OnPropertyChanged(nameof(Contrast));
        OnPropertyChanged(nameof(HasBrightnessContrastAdjustments));
        ApplyBrightnessContrastCommand.NotifyCanExecuteChanged();
        ResetBrightnessContrastCommand.NotifyCanExecuteChanged();
    }

    #endregion

    #region Command Implementations

    private void ExecuteToggleColorBalance()
    {
        IsColorBalancePanelOpen = !IsColorBalancePanelOpen;
        ToolToggled?.Invoke(this, (ImageEditor.Services.ToolIds.ColorBalance, IsColorBalancePanelOpen));
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
        ToolToggled?.Invoke(this, (ImageEditor.Services.ToolIds.BrightnessContrast, IsBrightnessContrastPanelOpen));
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

    #endregion
}
