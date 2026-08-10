using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Sub-ViewModel managing background removal tool state, model download, and processing.
/// Extracted from <see cref="ImageEditorViewModel"/> to reduce its size.
/// </summary>
public partial class BackgroundRemovalViewModel : ObservableObject
{
    private readonly Func<bool> _hasImage;
    private readonly Action<string> _deactivateOtherTools;
    private readonly IBackgroundRemovalService? _service;
    private readonly IDownloadCoordinator? _downloadCoordinator;

    private bool _isPanelOpen;
    private bool _isBusy;
    private string? _status;
    private int _progress;

    public BackgroundRemovalViewModel(
        Func<bool> hasImage,
        Action<string> deactivateOtherTools,
        IBackgroundRemovalService? service,
        IDownloadCoordinator? downloadCoordinator = null)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);
        _hasImage = hasImage;
        _deactivateOtherTools = deactivateOtherTools;
        _service = service;
        _downloadCoordinator = downloadCoordinator;

        RemoveBackgroundCommand = new AsyncRelayCommand(ExecuteRemoveBackgroundAsync, CanExecuteRemoveBackground);
        RemoveBackgroundToLayerCommand = new AsyncRelayCommand(ExecuteRemoveBackgroundToLayerAsync, CanExecuteRemoveBackground);
        DownloadModelCommand = new AsyncRelayCommand(ExecuteDownloadModelAsync, CanExecuteDownloadModel);
    }

    #region Properties

    /// <summary>Whether the background removal panel is open.</summary>
    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        set
        {
            if (SetProperty(ref _isPanelOpen, value))
            {
                if (value)
                    _deactivateOtherTools(ImageEditor.Services.ToolIds.BackgroundRemoval);
                RemoveBackgroundCommand.NotifyCanExecuteChanged();
                DownloadModelCommand.NotifyCanExecuteChanged();
                ToolToggled?.Invoke(this, (ImageEditor.Services.ToolIds.BackgroundRemoval, value));
                ToolStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Whether background removal is currently in progress.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RemoveBackgroundCommand.NotifyCanExecuteChanged();
                DownloadModelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Status message for background removal operations.</summary>
    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Progress percentage for model download (0-100).</summary>
    public int Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    /// <summary>Whether the background removal model is ready for use.</summary>
    public bool IsModelReady =>
        _service?.GetModelStatus() == ModelStatus.Ready;

    /// <summary>Whether the background removal model needs to be downloaded.</summary>
    public bool IsModelMissing
    {
        get
        {
            var status = _service?.GetModelStatus() ?? ModelStatus.NotDownloaded;
            return status == ModelStatus.NotDownloaded || status == ModelStatus.Corrupted;
        }
    }

    /// <summary>Whether GPU acceleration is available for background removal.</summary>
    public bool IsGpuAvailable => _service?.IsGpuAvailable ?? false;

    #endregion

    #region Commands

    public IAsyncRelayCommand RemoveBackgroundCommand { get; }
    public IAsyncRelayCommand RemoveBackgroundToLayerCommand { get; }
    public IAsyncRelayCommand DownloadModelCommand { get; }

    #endregion

    #region Events

    /// <summary>Event raised when tool state changes (panel open/close).</summary>
    public event EventHandler? ToolStateChanged;

    /// <summary>Raised when the tool is toggled, for ToolManager coordination.</summary>
    public event EventHandler<(string ToolId, bool IsActive)>? ToolToggled;

    /// <summary>Event raised to request image data for background removal.</summary>
    public event EventHandler? RemoveBackgroundRequested;

    /// <summary>Event raised to request layer-based background removal.</summary>
    public event EventHandler? RemoveBackgroundToLayerRequested;

    /// <summary>Event raised when background removal completes with result.</summary>
    public event EventHandler<BackgroundRemovalResult>? BackgroundRemovalCompleted;

    /// <summary>Event raised when layer-based background removal completes with result.</summary>
    public event EventHandler<BackgroundRemovalResult>? BackgroundRemovalToLayerCompleted;

    /// <summary>Event raised when a status message should be shown.</summary>
    public event EventHandler<string?>? StatusMessageChanged;

    // TODO: Linux Implementation for Background Removal

    #endregion

    #region Public Methods

    /// <summary>Refreshes the background removal model status properties.</summary>
    public void RefreshModelStatus()
    {
        OnPropertyChanged(nameof(IsModelReady));
        OnPropertyChanged(nameof(IsModelMissing));
        RemoveBackgroundCommand.NotifyCanExecuteChanged();
        DownloadModelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Notifies all commands that their CanExecute state may have changed.</summary>
    public void RefreshCommandStates()
    {
        RemoveBackgroundCommand.NotifyCanExecuteChanged();
        RemoveBackgroundToLayerCommand.NotifyCanExecuteChanged();
        DownloadModelCommand.NotifyCanExecuteChanged();
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

    /// <summary>Called when background removal is applied successfully.</summary>
    public void OnBackgroundRemovalApplied()
    {
        StatusMessageChanged?.Invoke(this, "Background removed");
    }

    /// <summary>Called when layer-based background removal is applied successfully.</summary>
    public void OnBackgroundRemovalToLayerApplied()
    {
        StatusMessageChanged?.Invoke(this, "Background separated to layers");
    }

    /// <summary>
    /// Processes background removal with the provided image data.
    /// Called by the View after responding to RemoveBackgroundRequested.
    /// </summary>
    public async Task ProcessBackgroundRemovalAsync(byte[] imageData, int width, int height)
    {
        if (_service is null)
        {
            StatusMessageChanged?.Invoke(this, "Background removal service not available");
            IsBusy = false;
            return;
        }

        IsBusy = true;
        Status = "Processing...";
        Progress = 0;

        try
        {
            var result = await _service.RemoveBackgroundAsync(imageData, width, height);

            if (result.Success)
            {
                BackgroundRemovalCompleted?.Invoke(this, result);
            }
            else
            {
                StatusMessageChanged?.Invoke(this, result.ErrorMessage ?? "Background removal failed");
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessageChanged?.Invoke(this, "Background removal cancelled");
        }
        catch (Exception ex)
        {
            StatusMessageChanged?.Invoke(this, $"Background removal failed: {ex.Message}");
        }
        finally
        {
            Status = null;
            Progress = 0;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Processes layer-based background removal with the provided image data.
    /// Called by the View after responding to RemoveBackgroundToLayerRequested.
    /// </summary>
    public async Task ProcessBackgroundRemovalToLayerAsync(byte[] imageData, int width, int height)
    {
        if (_service is null)
        {
            StatusMessageChanged?.Invoke(this, "Background removal service not available");
            IsBusy = false;
            return;
        }

        IsBusy = true;
        Status = "Processing for layers...";
        Progress = 0;

        try
        {
            var result = await _service.RemoveBackgroundAsync(imageData, width, height);

            if (result.Success)
            {
                BackgroundRemovalToLayerCompleted?.Invoke(this, result);
            }
            else
            {
                StatusMessageChanged?.Invoke(this, result.ErrorMessage ?? "Background removal failed");
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessageChanged?.Invoke(this, "Background removal cancelled");
        }
        catch (Exception ex)
        {
            StatusMessageChanged?.Invoke(this, $"Background removal failed: {ex.Message}");
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

    private bool CanExecuteRemoveBackground() =>
        _hasImage() && !IsBusy;

    private bool CanExecuteDownloadModel() =>
        IsModelMissing && !IsBusy;

    private async Task ExecuteRemoveBackgroundAsync()
    {
        if (_service is null)
        {
            StatusMessageChanged?.Invoke(this, "Background removal service not available");
            return;
        }

        if (!IsModelReady)
        {
            StatusMessageChanged?.Invoke(this, "Please download the RMBG-1.4 model first");
            return;
        }

        IsBusy = true;
        Status = "Preparing image...";
        Progress = 0;

        try
        {
            RemoveBackgroundRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessageChanged?.Invoke(this, $"Background removal failed: {ex.Message}");
            Status = null;
            Progress = 0;
            IsBusy = false;
        }
    }

    private async Task ExecuteRemoveBackgroundToLayerAsync()
    {
        if (_service is null)
        {
            StatusMessageChanged?.Invoke(this, "Background removal service not available");
            return;
        }

        if (!IsModelReady)
        {
            StatusMessageChanged?.Invoke(this, "Please download the RMBG-1.4 model first");
            return;
        }

        IsBusy = true;
        Status = "Preparing image for layer-based removal...";
        Progress = 0;

        try
        {
            RemoveBackgroundToLayerRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessageChanged?.Invoke(this, $"Background removal failed: {ex.Message}");
            Status = null;
            Progress = 0;
            IsBusy = false;
        }
    }

    private async Task ExecuteDownloadModelAsync()
    {
        if (_service is null)
        {
            StatusMessageChanged?.Invoke(this, "Background removal service not available");
            return;
        }

        IsBusy = true;
        Progress = 0;
        Status = "Downloading model...";

        try
        {
            bool success;

            // The coordinator catches every exception inside EnqueueAsync and
            // surfaces only a bool (its contract: "exceptions are caught and
            // surfaced as a failed task"), so the catches below never fire on
            // this path. Capture the cause ourselves — otherwise disk-full,
            // network failure and a deliberate cancel all collapse into one
            // indistinguishable "did not complete".
            Exception? downloadError = null;

            if (_downloadCoordinator is not null)
            {
                success = await _downloadCoordinator.EnqueueAsync(
                    "RMBG-1.4 background removal model",
                    async (taskProgress, ct) =>
                    {
                        var fileProgress = new Progress<ModelDownloadProgress>(p =>
                        {
                            ReportPanelProgress(p);
                            taskProgress.Report(p.ToDownloadTaskProgress());
                        });

                        try
                        {
                            return await _service.DownloadModelAsync(fileProgress, ct);
                        }
                        catch (Exception ex)
                        {
                            downloadError = ex;
                            throw;
                        }
                    });
            }
            else
            {
                var progress = new Progress<ModelDownloadProgress>(ReportPanelProgress);

                success = await _service.DownloadModelAsync(progress);
            }

            if (success)
            {
                StatusMessageChanged?.Invoke(this, "RMBG-1.4 model downloaded successfully");
                RefreshModelStatus();
            }
            else
            {
                StatusMessageChanged?.Invoke(this, downloadError switch
                {
                    OperationCanceledException => "Model download cancelled",
                    not null => $"Model download failed: {downloadError.Message}",
                    _ => "Model download did not complete",
                });
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

    /// <summary>
    /// Pushes download progress into the panel's own bar. Guarded the same
    /// way GenerationGalleryViewModel guards its dispatcher use: with no
    /// Avalonia application (unit tests) the static dispatcher has no pump,
    /// so a Post would silently never run — execute inline instead.
    /// </summary>
    private void ReportPanelProgress(ModelDownloadProgress p)
    {
        if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            ApplyPanelProgress(p);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ApplyPanelProgress(p));
        }
    }

    private void ApplyPanelProgress(ModelDownloadProgress p)
    {
        // Percentage is -1 when the server sent no usable size — leave the
        // bar where it was rather than snapping it to 0.
        if (p.Percentage >= 0)
            Progress = (int)p.Percentage;
        Status = p.Status;
    }

    #endregion
}
