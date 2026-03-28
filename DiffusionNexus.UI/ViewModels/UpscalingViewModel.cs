using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Sub-ViewModel for the (deprecated) AI Upscaling panel in the Image Editor.
/// The built-in 4x-UltraSharp upscaler has been replaced by the Batch Upscale tab
/// which uses ComfyUI-powered workflows with prompt guidance.
/// This class now only provides the panel toggle and a "Send to Batch Upscale" navigation command.
/// </summary>
[Obsolete("The built-in upscaler is deprecated. Use the Batch Upscale tab for ComfyUI-powered upscaling.")]
public partial class UpscalingViewModel : ObservableObject
{
    private readonly Func<bool> _hasImage;
    private readonly Func<string?> _getImagePath;
    private readonly Action<string> _deactivateOtherTools;
    private readonly IDatasetEventAggregator? _eventAggregator;

    private bool _isPanelOpen;

    public UpscalingViewModel(
        Func<bool> hasImage,
        Func<string?> getImagePath,
        Action<string> deactivateOtherTools,
        IDatasetEventAggregator? eventAggregator)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(getImagePath);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);
        _hasImage = hasImage;
        _getImagePath = getImagePath;
        _deactivateOtherTools = deactivateOtherTools;
        _eventAggregator = eventAggregator;

        SendToBatchUpscaleCommand = new RelayCommand(ExecuteSendToBatchUpscale, CanExecuteSendToBatchUpscale);
    }

    #region Properties

    /// <summary>Whether the AI upscaling panel is open.</summary>
    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        set
        {
            if (SetProperty(ref _isPanelOpen, value))
            {
                if (value)
                    _deactivateOtherTools(ImageEditor.Services.ToolIds.Upscaling);
                ToolToggled?.Invoke(this, (ImageEditor.Services.ToolIds.Upscaling, value));
                ToolStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    #endregion

    #region Commands

    /// <summary>Navigates to the Batch Upscale tab with the current image loaded as a single image.</summary>
    public IRelayCommand SendToBatchUpscaleCommand { get; }

    #endregion

    #region Events

    /// <summary>Event raised when tool state changes (panel open/close).</summary>
    public event EventHandler? ToolStateChanged;

    /// <summary>Raised when the tool is toggled, for ToolManager coordination.</summary>
    public event EventHandler<(string ToolId, bool IsActive)>? ToolToggled;

    /// <summary>Event raised when a status message should be shown.</summary>
    public event EventHandler<string?>? StatusMessageChanged;

    // TODO: Linux Implementation for AI Upscaling

    #endregion

    #region Public Methods

    /// <summary>Notifies all commands that their CanExecute state may have changed.</summary>
    public void RefreshCommandStates()
    {
        SendToBatchUpscaleCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Closes the panel without triggering deactivation of other tools.</summary>
    public void ClosePanel()
    {
        if (_isPanelOpen)
        {
            _isPanelOpen = false;
            OnPropertyChanged(nameof(IsPanelOpen));
        }
    }

    #endregion

    #region Command Implementations

    private bool CanExecuteSendToBatchUpscale() =>
        _hasImage() && !string.IsNullOrEmpty(_getImagePath());

    private void ExecuteSendToBatchUpscale()
    {
        var imagePath = _getImagePath();
        if (string.IsNullOrEmpty(imagePath)) return;

        _eventAggregator?.PublishNavigateToBatchUpscale(
            new NavigateToBatchUpscaleEventArgs { ImagePath = imagePath });
    }

    #endregion
}
