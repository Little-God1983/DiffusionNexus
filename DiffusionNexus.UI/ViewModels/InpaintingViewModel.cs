using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Sub-ViewModel managing inpainting tool state, ComfyUI workflow execution, and mask settings.
/// Extracted from <see cref="ImageEditorViewModel"/> to reduce its size.
/// </summary>
public partial class InpaintingViewModel : ObservableObject
{
    private readonly Func<bool> _hasImage;
    private readonly Action<string> _deactivateOtherTools;
    private readonly IComfyUIWrapperService? _comfyUiService;
    private readonly IDatasetEventAggregator? _eventAggregator;

    private const string InpaintWorkflowPath = "Assets/Workflows/Inpaint-Qwen-2512.json";
    private const string InpaintLoadImageNodeId = "16";
    private const string InpaintPositivePromptNodeId = "5";
    private const string InpaintNegativePromptNodeId = "8";
    private const string InpaintKSamplerNodeId = "11";
    private const string DefaultNegativePrompt = "blurry, low quality, artifacts, distorted, deformed, ugly, bad anatomy, watermark, text";

    private bool _isPanelOpen;
    private float _brushSize = 40f;
    private float _maskFeather = 10f;
    private float _denoise = 1.0f;
    private bool _isBusy;
    private string? _status;
    private string _positivePrompt = string.Empty;
    private string _negativePrompt = DefaultNegativePrompt;
    private Avalonia.Media.Imaging.Bitmap? _baseThumbnail;
    private bool _hasBase;
    private string? _pendingCompareBeforeImagePath;

    public InpaintingViewModel(
        Func<bool> hasImage,
        Action<string> deactivateOtherTools,
        IComfyUIWrapperService? comfyUiService,
        IDatasetEventAggregator? eventAggregator)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);
        _hasImage = hasImage;
        _deactivateOtherTools = deactivateOtherTools;
        _comfyUiService = comfyUiService;
        _eventAggregator = eventAggregator;

        ClearMaskCommand = new RelayCommand(
            () => ClearMaskRequested?.Invoke(this, EventArgs.Empty),
            () => _hasImage() && IsPanelOpen);
        GenerateCommand = new AsyncRelayCommand(
            ExecuteGenerateAsync,
            () => _hasImage() && IsPanelOpen && !IsBusy);
        GenerateAndCompareCommand = new AsyncRelayCommand(
            ExecuteGenerateAndCompareAsync,
            () => _hasImage() && IsPanelOpen && !IsBusy);
        UseCurrentAsBaseCommand = new RelayCommand(
            () => SetBaseRequested?.Invoke(this, EventArgs.Empty),
            () => _hasImage() && IsPanelOpen);
    }

    #region Properties

    /// <summary>Whether the inpainting panel is open.</summary>
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
                    StatusMessageChanged?.Invoke(this, "Inpaint: Paint over areas to mark them for AI regeneration.");

                    if (!HasBase)
                        SetBaseRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    StatusMessageChanged?.Invoke(this, null);
                }
                ToolActivated?.Invoke(this, value);
                ClearMaskCommand.NotifyCanExecuteChanged();
                ToolToggled?.Invoke(this, (ImageEditor.Services.ToolIds.Inpainting, value));
                ToolStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Inpainting brush size in display pixels (1-200).</summary>
    public float BrushSize
    {
        get => _brushSize;
        set
        {
            var clamped = Math.Clamp(value, 1f, 200f);
            if (SetProperty(ref _brushSize, clamped))
            {
                OnPropertyChanged(nameof(BrushSizeText));
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Formatted inpaint brush size for display.</summary>
    public string BrushSizeText => $"{(int)_brushSize} px";

    /// <summary>Mask feather radius (0-50). Controls how softly the mask edges blend.</summary>
    public float MaskFeather
    {
        get => _maskFeather;
        set
        {
            var clamped = Math.Clamp(value, 0f, 50f);
            if (SetProperty(ref _maskFeather, clamped))
                OnPropertyChanged(nameof(MaskFeatherText));
        }
    }

    /// <summary>Formatted mask feather value for display.</summary>
    public string MaskFeatherText => _maskFeather < 0.5f ? "Off" : $"{_maskFeather:F0} px";

    /// <summary>Denoise strength (0.0-1.0). 1.0 = full replacement.</summary>
    public float Denoise
    {
        get => _denoise;
        set
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            if (SetProperty(ref _denoise, clamped))
                OnPropertyChanged(nameof(DenoiseText));
        }
    }

    /// <summary>Formatted denoise value for display.</summary>
    public string DenoiseText => $"{_denoise:F2}";

    /// <summary>Positive prompt describing what to generate in the masked areas.</summary>
    public string PositivePrompt
    {
        get => _positivePrompt;
        set => SetProperty(ref _positivePrompt, value ?? string.Empty);
    }

    /// <summary>Negative prompt for inpainting.</summary>
    public string NegativePrompt
    {
        get => _negativePrompt;
        set => SetProperty(ref _negativePrompt, value ?? string.Empty);
    }

    /// <summary>Whether an inpainting operation is currently in progress.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Status message for inpainting operations.</summary>
    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Whether an inpaint base image has been captured.</summary>
    public bool HasBase
    {
        get => _hasBase;
        private set => SetProperty(ref _hasBase, value);
    }

    /// <summary>Thumbnail preview of the current inpaint base image.</summary>
    public Avalonia.Media.Imaging.Bitmap? BaseThumbnail
    {
        get => _baseThumbnail;
        private set => SetProperty(ref _baseThumbnail, value);
    }

    /// <summary>Whether a "Generate and Compare" operation is pending.</summary>
    public bool IsCompareModePending => _pendingCompareBeforeImagePath is not null;

    #endregion

    #region Commands

    /// <summary>Command to clear the inpaint mask layer.</summary>
    public RelayCommand ClearMaskCommand { get; }

    /// <summary>Command to generate inpainting via ComfyUI.</summary>
    public IAsyncRelayCommand GenerateCommand { get; }

    /// <summary>Command to generate inpainting and open both images in the Image Comparer.</summary>
    public IAsyncRelayCommand GenerateAndCompareCommand { get; }

    /// <summary>Command to capture the current flattened state as the inpaint base.</summary>
    public RelayCommand UseCurrentAsBaseCommand { get; }

    #endregion

    #region Events

    /// <summary>Event raised when tool state changes.</summary>
    public event EventHandler? ToolStateChanged;

    /// <summary>Raised when the tool is toggled, for ToolManager coordination.</summary>
    public event EventHandler<(string ToolId, bool IsActive)>? ToolToggled;

    /// <summary>Event raised when the inpainting tool is activated or deactivated.</summary>
    public event EventHandler<bool>? ToolActivated;

    /// <summary>Event raised when inpaint brush settings change.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Event raised when the ViewModel requests clearing the inpaint mask.</summary>
    public event EventHandler? ClearMaskRequested;

    /// <summary>Event raised when the user requests inpaint generation.</summary>
    public event EventHandler? GenerateRequested;

    /// <summary>Event raised when the inpaint result image bytes are ready.</summary>
    public event EventHandler<byte[]>? ResultReady;

    /// <summary>Event raised when a "Generate and Compare" inpainting completes.</summary>
    public event EventHandler<InpaintCompareEventArgs>? CompareRequested;

    /// <summary>Event raised when the ViewModel requests capturing the current state as the inpaint base.</summary>
    public event EventHandler? SetBaseRequested;

    /// <summary>Event raised when a status message should be shown.</summary>
    public event EventHandler<string?>? StatusMessageChanged;

    // TODO: Linux Implementation for Inpainting

    #endregion

    #region Public Methods

    /// <summary>Notifies all commands that their CanExecute state may have changed.</summary>
    public void RefreshCommandStates()
    {
        ClearMaskCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
        GenerateAndCompareCommand.NotifyCanExecuteChanged();
        UseCurrentAsBaseCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Closes the panel without triggering deactivation of other tools.</summary>
    public void ClosePanel()
    {
        if (_isPanelOpen)
        {
            _isPanelOpen = false;
            OnPropertyChanged(nameof(IsPanelOpen));
            ToolActivated?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Updates the inpaint base thumbnail after the EditorCore captures the base bitmap.
    /// Called by the View after wiring SetBaseRequested.
    /// </summary>
    public void UpdateBaseThumbnail(Avalonia.Media.Imaging.Bitmap? thumbnail)
    {
        var old = _baseThumbnail;
        BaseThumbnail = thumbnail;
        HasBase = thumbnail is not null;
        if (old is not null && !ReferenceEquals(old, thumbnail))
        {
            old.Dispose();
        }
    }

    /// <summary>Sets the path to the "before" image saved by the View for compare mode.</summary>
    public void SetCompareBeforeImagePath(string path)
    {
        _pendingCompareBeforeImagePath = path;
    }

    /// <summary>
    /// Processes the inpainting workflow via ComfyUI.
    /// Called by the View after it prepares the masked image file.
    /// </summary>
    public async Task ProcessInpaintAsync(string maskedImagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(maskedImagePath);

        if (_comfyUiService is null)
        {
            StatusMessageChanged?.Invoke(this, "ComfyUI service not available.");
            OnFinished();
            return;
        }

        try
        {
            Status = "Uploading image to ComfyUI...";
            var uploadedFilename = await _comfyUiService.UploadImageAsync(maskedImagePath);

            Status = "Queuing inpainting workflow...";

            var workflowPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                InpaintWorkflowPath);

            if (!File.Exists(workflowPath))
            {
                StatusMessageChanged?.Invoke(this, $"Inpainting workflow not found: {workflowPath}");
                OnFinished();
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
                        node["inputs"]!["text"] = _positivePrompt;
                    },
                    [InpaintNegativePromptNodeId] = node =>
                    {
                        node["inputs"]!["text"] = _negativePrompt;
                    },
                    [InpaintKSamplerNodeId] = node =>
                    {
                        node["inputs"]!["seed"] = seed;
                        node["inputs"]!["denoise"] = _denoise;
                    }
                });

            Status = "Generating (this may take a while)...";
            var progress = new Progress<string>(msg => Status = msg);
            await _comfyUiService.WaitForCompletionAsync(promptId, progress);

            Status = "Downloading result...";
            var result = await _comfyUiService.GetResultAsync(promptId);

            if (result.Images.Count > 0)
            {
                var imageBytes = await _comfyUiService.DownloadImageAsync(result.Images[0]);
                ResultReady?.Invoke(this, imageBytes);
                StatusMessageChanged?.Invoke(this, "Inpainting completed successfully.");

                if (!string.IsNullOrEmpty(_pendingCompareBeforeImagePath))
                {
                    var afterPath = Path.Combine(Path.GetTempPath(), $"diffnexus_inpaint_after_{Guid.NewGuid():N}.png");
                    await File.WriteAllBytesAsync(afterPath, imageBytes);

                    CompareRequested?.Invoke(this, new InpaintCompareEventArgs
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
                StatusMessageChanged?.Invoke(this, "Inpainting completed but no output image was returned.");
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessageChanged?.Invoke(this, "Inpainting was cancelled.");
        }
        catch (Exception ex)
        {
            StatusMessageChanged?.Invoke(this, $"Inpainting failed: {ex.Message}");
        }
        finally
        {
            OnFinished();
        }
    }

    #endregion

    #region Private Methods

    private async Task ExecuteGenerateAsync()
    {
        if (string.IsNullOrWhiteSpace(_positivePrompt))
        {
            StatusMessageChanged?.Invoke(this, "Please enter a prompt describing what to generate.");
            return;
        }

        if (_comfyUiService is null)
        {
            StatusMessageChanged?.Invoke(this, "ComfyUI service not available. Check ComfyUI server settings.");
            return;
        }

        _pendingCompareBeforeImagePath = null;
        IsBusy = true;
        Status = "Preparing image and mask...";
        NotifyGenerateCommandsCanExecuteChanged();

        GenerateRequested?.Invoke(this, EventArgs.Empty);

        await Task.CompletedTask;
    }

    private async Task ExecuteGenerateAndCompareAsync()
    {
        if (string.IsNullOrWhiteSpace(_positivePrompt))
        {
            StatusMessageChanged?.Invoke(this, "Please enter a prompt describing what to generate.");
            return;
        }

        if (_comfyUiService is null)
        {
            StatusMessageChanged?.Invoke(this, "ComfyUI service not available. Check ComfyUI server settings.");
            return;
        }

        _pendingCompareBeforeImagePath = string.Empty;
        IsBusy = true;
        Status = "Preparing image and mask...";
        NotifyGenerateCommandsCanExecuteChanged();

        GenerateRequested?.Invoke(this, EventArgs.Empty);

        await Task.CompletedTask;
    }

    private void OnFinished()
    {
        _pendingCompareBeforeImagePath = null;
        IsBusy = false;
        Status = null;
        NotifyGenerateCommandsCanExecuteChanged();
    }

    private void NotifyGenerateCommandsCanExecuteChanged()
    {
        GenerateCommand.NotifyCanExecuteChanged();
        GenerateAndCompareCommand.NotifyCanExecuteChanged();
    }

    #endregion
}
