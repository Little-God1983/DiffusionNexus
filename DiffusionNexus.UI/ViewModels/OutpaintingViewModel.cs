using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.ImageEditor.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Sub-ViewModel managing the outpainting tool state and aspect ratio presets.
/// Allows extending the canvas in any direction with aspect ratio presets.
/// </summary>
public partial class OutpaintingViewModel : ObservableObject
{
    private readonly Func<bool> _hasImage;
    private readonly Func<int> _getImageWidth;
    private readonly Func<int> _getImageHeight;
    private readonly Action<string> _deactivateOtherTools;

    private bool _isPanelOpen;
    private string _outpaintResolutionText = string.Empty;

    public OutpaintingViewModel(
        Func<bool> hasImage,
        Func<int> getImageWidth,
        Func<int> getImageHeight,
        Action<string> deactivateOtherTools)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(getImageWidth);
        ArgumentNullException.ThrowIfNull(getImageHeight);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);

        _hasImage = hasImage;
        _getImageWidth = getImageWidth;
        _getImageHeight = getImageHeight;
        _deactivateOtherTools = deactivateOtherTools;

        ToggleCommand = new RelayCommand(ExecuteToggle, () => _hasImage());
        ResetCommand = new RelayCommand(ExecuteReset, () => _hasImage() && IsPanelOpen);
        CancelCommand = new RelayCommand(ExecuteCancel, () => IsPanelOpen);
        SetAspectRatioCommand = new RelayCommand<string>(ExecuteSetAspectRatio, _ => _hasImage() && IsPanelOpen);
    }

    #region Properties

    /// <summary>Whether the outpainting panel is open.</summary>
    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        set
        {
            if (SetProperty(ref _isPanelOpen, value))
            {
                if (value)
                {
                    _deactivateOtherTools(ToolIds.Outpainting);
                    OutpaintToolActivated?.Invoke(this, EventArgs.Empty);
                    StatusMessageChanged?.Invoke(this, "Outpaint: Drag arrows to extend the canvas. Use aspect ratio presets on the right.");
                }
                else
                {
                    OutpaintToolDeactivated?.Invoke(this, EventArgs.Empty);
                    StatusMessageChanged?.Invoke(this, null);
                }

                ResetCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
                SetAspectRatioCommand.NotifyCanExecuteChanged();
                ToolToggled?.Invoke(this, (ToolIds.Outpainting, value));
                ToolStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Resolution text for the outpaint result (e.g. "1920 x 1080").</summary>
    public string OutpaintResolutionText
    {
        get => _outpaintResolutionText;
        set => SetProperty(ref _outpaintResolutionText, value);
    }

    #endregion

    #region Commands

    /// <summary>Toggles the outpainting panel open/closed.</summary>
    public IRelayCommand ToggleCommand { get; }

    /// <summary>Resets all extensions to zero.</summary>
    public IRelayCommand ResetCommand { get; }

    /// <summary>Cancels outpainting and closes the panel.</summary>
    public IRelayCommand CancelCommand { get; }

    /// <summary>Sets the extension to match a given aspect ratio string (e.g. "16:9").</summary>
    public IRelayCommand<string> SetAspectRatioCommand { get; }

    #endregion

    #region Events

    /// <summary>Raised when tool state changes (panel open/close).</summary>
    public event EventHandler? ToolStateChanged;

    /// <summary>Raised when the tool is toggled, for ToolManager coordination.</summary>
    public event EventHandler<(string ToolId, bool IsActive)>? ToolToggled;

    /// <summary>Raised when the outpaint tool is activated.</summary>
    public event EventHandler? OutpaintToolActivated;

    /// <summary>Raised when the outpaint tool is deactivated.</summary>
    public event EventHandler? OutpaintToolDeactivated;

    /// <summary>Raised to reset the outpaint extension.</summary>
    public event EventHandler? ResetRequested;

    /// <summary>Raised when an aspect ratio preset is selected.</summary>
    public event EventHandler<(float W, float H)>? SetAspectRatioRequested;

    /// <summary>Raised when a status message should be shown.</summary>
    public event EventHandler<string?>? StatusMessageChanged;

    #endregion

    #region Public Methods

    /// <summary>Notifies all commands that their CanExecute state may have changed.</summary>
    public void RefreshCommandStates()
    {
        ToggleCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        SetAspectRatioCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Closes the panel without triggering deactivation of other tools.</summary>
    public void ClosePanel()
    {
        if (_isPanelOpen)
        {
            _isPanelOpen = false;
            OnPropertyChanged(nameof(IsPanelOpen));
            OutpaintToolDeactivated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Updates the resolution text from the current outpaint dimensions.</summary>
    public void UpdateResolution(int width, int height)
    {
        OutpaintResolutionText = width > 0 && height > 0 ? $"{width} x {height}" : string.Empty;
    }

    #endregion

    #region Command Implementations

    private void ExecuteToggle()
    {
        IsPanelOpen = !IsPanelOpen;
    }

    private void ExecuteReset()
    {
        OutpaintResolutionText = string.Empty;
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteCancel()
    {
        OutpaintResolutionText = string.Empty;
        IsPanelOpen = false;
    }

    private void ExecuteSetAspectRatio(string? ratio)
    {
        if (string.IsNullOrWhiteSpace(ratio)) return;

        var parts = ratio.Split(':');
        if (parts.Length != 2 ||
            !float.TryParse(parts[0], out var w) ||
            !float.TryParse(parts[1], out var h))
            return;

        SetAspectRatioRequested?.Invoke(this, (w, h));
    }

    #endregion
}
