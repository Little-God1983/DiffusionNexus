using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Sub-ViewModel managing AI upscaling tool state, model download, and processing.
/// Extracted from <see cref="ImageEditorViewModel"/> to reduce its size.
/// </summary>
public partial class UpscalingViewModel : ObservableObject
{
    private readonly Func<bool> _hasImage;
    private readonly Func<int> _getImageWidth;
    private readonly Func<int> _getImageHeight;
    private readonly Action<string> _deactivateOtherTools;
    private readonly IImageUpscalingService? _service;

    private bool _isPanelOpen;
    private bool _isBusy;
    private string? _status;
    private int _progress;
    private float _targetScale = 2.0f;

    public UpscalingViewModel(
        Func<bool> hasImage,
        Func<int> getImageWidth,
        Func<int> getImageHeight,
        Action<string> deactivateOtherTools,
        IImageUpscalingService? service)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(getImageWidth);
        ArgumentNullException.ThrowIfNull(getImageHeight);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);
        _hasImage = hasImage;
        _getImageWidth = getImageWidth;
        _getImageHeight = getImageHeight;
        _deactivateOtherTools = deactivateOtherTools;
        _service = service;

        UpscaleCommand = new AsyncRelayCommand(ExecuteUpscaleAsync, CanExecuteUpscale);
        DownloadModelCommand = new AsyncRelayCommand(ExecuteDownloadModelAsync, CanExecuteDownloadModel);
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
                    _deactivateOtherTools(nameof(IsPanelOpen));
                UpscaleCommand.NotifyCanExecuteChanged();
                DownloadModelCommand.NotifyCanExecuteChanged();
                ToolStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Whether AI upscaling is currently in progress.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                UpscaleCommand.NotifyCanExecuteChanged();
                DownloadModelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Status message for upscaling operations.</summary>
    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Progress percentage for upscaling/download (0-100).</summary>
    public int Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    /// <summary>Target scale factor for upscaling (1.1 to 4.0).</summary>
    public float TargetScale
    {
        get => _targetScale;
        set
        {
            var clamped = Math.Clamp(value, 1.1f, 4.0f);
            if (SetProperty(ref _targetScale, clamped))
            {
                OnPropertyChanged(nameof(TargetScaleText));
                OnPropertyChanged(nameof(OutputDimensions));
            }
        }
    }

    /// <summary>Formatted target scale for display.</summary>
    public string TargetScaleText => $"{_targetScale:F1}x";

    /// <summary>Predicted output dimensions based on current scale.</summary>
    public string OutputDimensions
    {
        get
        {
            if (!_hasImage() || _getImageWidth() == 0 || _getImageHeight() == 0)
                return "N/A";
            var targetWidth = (int)Math.Round(_getImageWidth() * _targetScale);
            var targetHeight = (int)Math.Round(_getImageHeight() * _targetScale);
            return $"{targetWidth} × {targetHeight} px";
        }
    }

    /// <summary>Whether the upscaling model is ready for use.</summary>
    public bool IsModelReady =>
        _service?.GetModelStatus() == ModelStatus.Ready;

    /// <summary>Whether the upscaling model needs to be downloaded.</summary>
    public bool IsModelMissing
    {
        get
        {
            var status = _service?.GetModelStatus() ?? ModelStatus.NotDownloaded;
            return status == ModelStatus.NotDownloaded || status == ModelStatus.Corrupted;
        }
    }

    /// <summary>Whether GPU acceleration is available for upscaling.</summary>
    public bool IsGpuAvailable => _service?.IsGpuAvailable ?? false;

    #endregion

    #region Commands

    public IAsyncRelayCommand UpscaleCommand { get; }
    public IAsyncRelayCommand DownloadModelCommand { get; }

    #endregion

    #region Events

    /// <summary>Event raised when tool state changes (panel open/close).</summary>
    public event EventHandler? ToolStateChanged;

    /// <summary>Event raised to request image data for upscaling.</summary>
    public event EventHandler? UpscaleRequested;

    /// <summary>Event raised when upscaling completes with result.</summary>
    public event EventHandler<ImageUpscalingResult>? UpscalingCompleted;

    /// <summary>Event raised when a status message should be shown.</summary>
    public event EventHandler<string?>? StatusMessageChanged;

    // TODO: Linux Implementation for AI Upscaling

    #endregion

    #region Public Methods

    /// <summary>Refreshes the upscaling model status properties.</summary>
    public void RefreshModelStatus()
    {
        OnPropertyChanged(nameof(IsModelReady));
        OnPropertyChanged(nameof(IsModelMissing));
        OnPropertyChanged(nameof(IsGpuAvailable));
        UpscaleCommand.NotifyCanExecuteChanged();
        DownloadModelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Notifies all commands that their CanExecute state may have changed.</summary>
    public void RefreshCommandStates()
    {
        UpscaleCommand.NotifyCanExecuteChanged();
        DownloadModelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Refreshes the output dimensions display.</summary>
    public void RefreshOutputDimensions()
    {
        OnPropertyChanged(nameof(OutputDimensions));
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

    /// <summary>Called when upscaling is applied successfully.</summary>
    public void OnUpscalingApplied()
    {
        StatusMessageChanged?.Invoke(this, "Image upscaled");
        OnPropertyChanged(nameof(OutputDimensions));
    }

    /// <summary>
    /// Processes upscaling with the provided image data.
    /// Called by the View after responding to UpscaleRequested.
    /// </summary>
    public async Task ProcessUpscalingAsync(byte[] imageData, int width, int height)
    {
        if (_service is null)
        {
            StatusMessageChanged?.Invoke(this, "Upscaling service not available");
            IsBusy = false;
            return;
        }

        try
        {
            var progress = new Progress<UpscalingProgress>(p =>
            {
                Status = p.Message;
                if (p.Percentage >= 0)
                    Progress = p.Percentage;
            });

            var result = await _service.UpscaleImageAsync(
                imageData, width, height, _targetScale, progress);

            if (result.Success)
            {
                StatusMessageChanged?.Invoke(this, $"Upscaled to {result.Width}x{result.Height}");
                UpscalingCompleted?.Invoke(this, result);
            }
            else
            {
                StatusMessageChanged?.Invoke(this, result.ErrorMessage ?? "Upscaling failed");
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessageChanged?.Invoke(this, "Upscaling cancelled");
        }
        catch (Exception ex)
        {
            StatusMessageChanged?.Invoke(this, $"Upscaling failed: {ex.Message}");
        }
        finally
        {
            Status = null;
            Progress = 0;
            IsBusy = false;
        }
    }

    #endregion

    #region Command Implementations

    private bool CanExecuteUpscale() =>
        _hasImage() && !IsBusy && IsModelReady;

    private bool CanExecuteDownloadModel() =>
        IsModelMissing && !IsBusy;

    private async Task ExecuteUpscaleAsync()
    {
        if (_service is null)
        {
            StatusMessageChanged?.Invoke(this, "Upscaling service not available");
            return;
        }

        if (!IsModelReady)
        {
            StatusMessageChanged?.Invoke(this, "Please download the 4x-UltraSharp model first");
            return;
        }

        IsBusy = true;
        Status = "Preparing image...";
        Progress = 0;

        try
        {
            UpscaleRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessageChanged?.Invoke(this, $"Upscaling failed: {ex.Message}");
            Status = null;
            Progress = 0;
            IsBusy = false;
        }
    }

    private async Task ExecuteDownloadModelAsync()
    {
        if (_service is null)
        {
            StatusMessageChanged?.Invoke(this, "Upscaling service not available");
            return;
        }

        IsBusy = true;
        Progress = 0;
        Status = "Downloading model...";

        try
        {
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                if (p.Percentage >= 0)
                    Progress = (int)p.Percentage;
                Status = p.Status;
            });

            var success = await _service.DownloadModelAsync(progress);

            if (success)
            {
                StatusMessageChanged?.Invoke(this, "4x-UltraSharp model downloaded successfully");
                RefreshModelStatus();
            }
            else
            {
                StatusMessageChanged?.Invoke(this, "Failed to download upscaling model");
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessageChanged?.Invoke(this, "Model download cancelled");
        }
        catch (Exception ex)
        {
            StatusMessageChanged?.Invoke(this, $"Model download failed: {ex.Message}");
        }
        finally
        {
            Status = null;
            Progress = 0;
            IsBusy = false;
        }
    }

    #endregion
}
