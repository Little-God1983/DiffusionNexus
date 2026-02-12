using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.ImageEditor;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Sub-ViewModel managing background fill tool state and color settings.
/// Extracted from <see cref="ImageEditorViewModel"/> to reduce its size.
/// </summary>
public partial class BackgroundFillViewModel : ObservableObject
{
    private readonly Func<bool> _hasImage;
    private readonly Action<string> _deactivateOtherTools;

    private bool _isPanelOpen;
    private byte _fillRed = 255;
    private byte _fillGreen = 255;
    private byte _fillBlue = 255;

    public BackgroundFillViewModel(Func<bool> hasImage, Action<string> deactivateOtherTools)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);
        _hasImage = hasImage;
        _deactivateOtherTools = deactivateOtherTools;

        ToggleCommand = new RelayCommand(ExecuteToggle, () => _hasImage());
        ApplyCommand = new RelayCommand(ExecuteApply, () => _hasImage() && IsPanelOpen);
        SetPresetCommand = new RelayCommand<string>(SetPreset);
    }

    #region Properties

    /// <summary>Whether the background fill panel is open.</summary>
    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        set
        {
            if (SetProperty(ref _isPanelOpen, value))
            {
                if (value)
                {
                    _deactivateOtherTools(nameof(IsPanelOpen));
                    RequestPreview();
                }
                else
                {
                    CancelPreviewRequested?.Invoke(this, EventArgs.Empty);
                }
                ApplyCommand.NotifyCanExecuteChanged();
                ToolStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Red component of the fill color (0-255).</summary>
    public byte FillRed
    {
        get => _fillRed;
        set
        {
            if (SetProperty(ref _fillRed, value))
            {
                OnPropertyChanged(nameof(FillColor));
                OnPropertyChanged(nameof(FillColorHex));
                RequestPreview();
            }
        }
    }

    /// <summary>Green component of the fill color (0-255).</summary>
    public byte FillGreen
    {
        get => _fillGreen;
        set
        {
            if (SetProperty(ref _fillGreen, value))
            {
                OnPropertyChanged(nameof(FillColor));
                OnPropertyChanged(nameof(FillColorHex));
                RequestPreview();
            }
        }
    }

    /// <summary>Blue component of the fill color (0-255).</summary>
    public byte FillBlue
    {
        get => _fillBlue;
        set
        {
            if (SetProperty(ref _fillBlue, value))
            {
                OnPropertyChanged(nameof(FillColor));
                OnPropertyChanged(nameof(FillColorHex));
                RequestPreview();
            }
        }
    }

    /// <summary>The current fill color as an Avalonia Color.</summary>
    public Avalonia.Media.Color FillColor
    {
        get => Avalonia.Media.Color.FromRgb(_fillRed, _fillGreen, _fillBlue);
        set
        {
            if (_fillRed != value.R || _fillGreen != value.G || _fillBlue != value.B)
            {
                _fillRed = value.R;
                _fillGreen = value.G;
                _fillBlue = value.B;
                OnPropertyChanged(nameof(FillRed));
                OnPropertyChanged(nameof(FillGreen));
                OnPropertyChanged(nameof(FillBlue));
                OnPropertyChanged(nameof(FillColor));
                OnPropertyChanged(nameof(FillColorHex));
                RequestPreview();
            }
        }
    }

    /// <summary>Hex string representation of the fill color.</summary>
    public string FillColorHex => $"#{_fillRed:X2}{_fillGreen:X2}{_fillBlue:X2}";

    /// <summary>Gets the current background fill settings.</summary>
    public BackgroundFillSettings CurrentSettings =>
        new(_fillRed, _fillGreen, _fillBlue);

    #endregion

    #region Commands

    public IRelayCommand ToggleCommand { get; }
    public IRelayCommand ApplyCommand { get; }
    public IRelayCommand<string> SetPresetCommand { get; }

    #endregion

    #region Events

    /// <summary>Event raised when tool state changes (panel open/close).</summary>
    public event EventHandler? ToolStateChanged;

    /// <summary>Event raised to request background fill preview.</summary>
    public event EventHandler<BackgroundFillSettings>? PreviewRequested;

    /// <summary>Event raised to cancel background fill preview.</summary>
    public event EventHandler? CancelPreviewRequested;

    /// <summary>Event raised to apply background fill permanently.</summary>
    public event EventHandler<BackgroundFillSettings>? ApplyRequested;

    /// <summary>Event raised when a status message should be shown.</summary>
    public event EventHandler<string?>? StatusMessageChanged;

    #endregion

    #region Public Methods

    /// <summary>Notifies all commands that their CanExecute state may have changed.</summary>
    public void RefreshCommandStates()
    {
        ToggleCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Closes the panel without triggering deactivation of other tools.</summary>
    public void ClosePanel()
    {
        if (_isPanelOpen)
        {
            _isPanelOpen = false;
            CancelPreviewRequested?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(IsPanelOpen));
        }
    }

    /// <summary>Called when background fill is applied successfully.</summary>
    public void OnFillApplied()
    {
        IsPanelOpen = false;
        StatusMessageChanged?.Invoke(this, "Background filled");
    }

    /// <summary>Sets the fill color from a preset name.</summary>
    public void SetPreset(string? preset)
    {
        if (preset is null) return;

        var settings = preset.ToUpperInvariant() switch
        {
            "WHITE" => BackgroundFillSettings.Presets.White,
            "BLACK" => BackgroundFillSettings.Presets.Black,
            "GRAY" or "GREY" => BackgroundFillSettings.Presets.Gray,
            "GREEN" => BackgroundFillSettings.Presets.Green,
            "BLUE" => BackgroundFillSettings.Presets.Blue,
            _ => null
        };

        if (settings is not null)
        {
            FillRed = settings.Red;
            FillGreen = settings.Green;
            FillBlue = settings.Blue;
        }
    }

    #endregion

    #region Private Methods

    private void RequestPreview()
    {
        if (IsPanelOpen && _hasImage())
        {
            PreviewRequested?.Invoke(this, CurrentSettings);
        }
    }

    private void ExecuteToggle()
    {
        IsPanelOpen = !IsPanelOpen;
    }

    private void ExecuteApply()
    {
        ApplyRequested?.Invoke(this, CurrentSettings);
    }

    #endregion
}
