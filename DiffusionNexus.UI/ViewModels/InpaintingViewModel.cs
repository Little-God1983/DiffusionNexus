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

    private static readonly string[] FunProgressMessages =
    [
        "Rolling dice for seed…",
        "Starting diffusion warp core…",
        "Looking if no one is looking…",
        "Consulting the pixel oracle…",
        "Summoning latent space spirits…",
        "Negotiating with the noise schedule…",
        "Calibrating the creativity dial…",
        "Warming up the denoiser…",
        "Sprinkling magic attention dust…",
        "Asking the VAE nicely…",
        "Blending dimensions carefully…",
        "Teaching neurons new tricks…",
        "Herding stochastic butterflies…",
        "Polishing latent embeddings…",
        "Shaking the token bag…",
        "Aligning cross-attention beams…",
        "Feeding the U-Net hamsters…",
        "Distilling creativity from chaos…",
        "Whispering prompts to the model…",
        "Painting with invisible brushes…"
    ];
    private readonly Random _random = new();

    private bool _isPanelOpen;
    private float _brushSize = 40f;
    private float _maskFeather = 10f;
    private float _denoise = 1.0f;
    private bool _isBusy;
    private string? _status;
    private int _inpaintProgress;
    private bool _isProgressIndeterminate;
    private bool _hasError;
    private string? _progressDisplayText;
    private int _lastDisplayTextIndex = -1;
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
                    _deactivateOtherTools(ImageEditor.Services.ToolIds.Inpainting);
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
        private set
        {
            if (SetProperty(ref _isBusy, value))
                OnPropertyChanged(nameof(IsProgressVisible));
        }
    }

    /// <summary>Status message for inpainting operations.</summary>
    public string? Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
                ParseProgress(value);
        }
    }

    /// <summary>Progress percentage (0-100) for the current inpainting operation, or -1 when indeterminate.</summary>
    public int InpaintProgress
    {
        get => _inpaintProgress;
        private set => SetProperty(ref _inpaintProgress, value);
    }

    /// <summary>Whether the progress bar should show an indeterminate animation.</summary>
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    /// <summary>Whether the current inpainting operation encountered an error (turns progress bar red).</summary>
    public bool HasError
    {
        get => _hasError;
        private set
        {
            if (SetProperty(ref _hasError, value))
                OnPropertyChanged(nameof(IsProgressVisible));
        }
    }

    /// <summary>Whether the progress panel should be visible (busy or showing an error).</summary>
    public bool IsProgressVisible => _isBusy || _hasError;

    /// <summary>Fun random text displayed above the progress bar during generation.</summary>
    public string? ProgressDisplayText
    {
        get => _progressDisplayText;
        private set => SetProperty(ref _progressDisplayText, value);
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
            HasError = true;
            ProgressDisplayText = "Generation failed – is ComfyUI running?";
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
        ResetErrorState();
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
        ResetErrorState();
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
        IsProgressIndeterminate = false;

        if (_hasError)
        {
            // Keep error state visible: full red bar + error message stay on screen
            InpaintProgress = 100;
        }
        else
        {
            Status = null;
            InpaintProgress = 0;
            ProgressDisplayText = null;
            _lastDisplayTextIndex = -1;
        }

        NotifyGenerateCommandsCanExecuteChanged();
    }

    /// <summary>Clears any previous error state so the progress bar starts fresh.</summary>
    private void ResetErrorState()
    {
        HasError = false;
        ProgressDisplayText = null;
        _lastDisplayTextIndex = -1;
    }

    /// <summary>Maps ComfyUI status strings to progress bar values (0–100).</summary>
    private void ParseProgress(string? status)
    {
        if (string.IsNullOrEmpty(status))
        {
            InpaintProgress = 0;
            IsProgressIndeterminate = false;
            ProgressDisplayText = null;
            return;
        }

        // "Progress: value/max" from the KSampler — scale into 30-90% range
        if (status.StartsWith("Progress:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = status["Progress:".Length..].Trim().Split('/');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var value)
                && int.TryParse(parts[1], out var max)
                && max > 0)
            {
                InpaintProgress = (int)(30 + (double)value / max * 60);
                IsProgressIndeterminate = false;
                PickRandomDisplayText();
                return;
            }
        }

        // Known phases mapped to fixed progress values
        InpaintProgress = status switch
        {
            _ when status.StartsWith("Preparing", StringComparison.OrdinalIgnoreCase) => 5,
            _ when status.StartsWith("Uploading", StringComparison.OrdinalIgnoreCase) => 10,
            _ when status.StartsWith("Queuing", StringComparison.OrdinalIgnoreCase) => 15,
            _ when status.StartsWith("Generating", StringComparison.OrdinalIgnoreCase) => 20,
            _ when status.StartsWith("Executing", StringComparison.OrdinalIgnoreCase) => 25,
            _ when status.StartsWith("Running", StringComparison.OrdinalIgnoreCase) => 25,
            _ when status.StartsWith("Loading", StringComparison.OrdinalIgnoreCase) => 25,
            _ when status.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase) => 95,
            _ => _inpaintProgress // keep current value for unknown messages
        };
        IsProgressIndeterminate = false;
        PickRandomDisplayText();
    }

    /// <summary>Picks a random fun message, avoiding repeating the last one.</summary>
    private void PickRandomDisplayText()
    {
        int index;
        do
        {
            index = _random.Next(FunProgressMessages.Length);
        } while (index == _lastDisplayTextIndex && FunProgressMessages.Length > 1);

        _lastDisplayTextIndex = index;
        ProgressDisplayText = FunProgressMessages[index];
    }

    private void NotifyGenerateCommandsCanExecuteChanged()
    {
        GenerateCommand.NotifyCanExecuteChanged();
        GenerateAndCompareCommand.NotifyCanExecuteChanged();
    }

    #endregion
}
