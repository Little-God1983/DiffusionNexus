using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.ImageEditor.Services;
using Serilog;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Sub-ViewModel managing the outpainting tool state, aspect ratio presets, and
/// ComfyUI workflow execution. Mirrors the <see cref="InpaintingViewModel"/> pattern
/// but supports two modes:
/// <list type="bullet">
///   <item><description>nonVision – uses the user-supplied positive prompt.</description></item>
///   <item><description>Vision – uses a Qwen3-VL VQA node to auto-describe the surroundings/background.</description></item>
/// </list>
/// </summary>
public partial class OutpaintingViewModel : ObservableObject
{
    private static readonly ILogger Logger = Log.ForContext<OutpaintingViewModel>();

    private readonly Func<bool> _hasImage;
    private readonly Func<int> _getImageWidth;
    private readonly Func<int> _getImageHeight;
    private readonly Action<string> _deactivateOtherTools;
    private readonly IComfyUIWrapperService? _comfyUiService;

    private const string OutpaintNonVisionWorkflowPath = "Assets/Workflows/Qwen-Image-2512-outpaint-nonVision.json";
    private const string OutpaintVisionWorkflowPath = "Assets/Workflows/Qwen-Image-2512-outpaint-Vision.json";
    private const string LoadImageNodeId = "16";
    private const string PositivePromptNodeId = "5";
    private const string NegativePromptNodeId = "8";
    private const string KSamplerNodeId = "11";
    private const string UnetLoaderNodeId = "15";
    private const string ImagePadNodeId = "26";
    private const string UnetLoaderGGUFNodeType = "UnetLoaderGGUF";
    private const string QwenImageGGUFPrefix = "qwen-image-2512-";
    private const string DefaultQwenImageGGUF = "qwen-image-2512-Q8_0.gguf";
    private const string DefaultNegativePrompt = "blurry, low quality, artifacts, distorted, deformed, ugly, bad anatomy, watermark, text";

    private static readonly string[] FunProgressMessages =
    [
        "Painting outside the lines…",
        "Imagining what's beyond the frame…",
        "Asking the canvas to grow…",
        "Consulting the pixel oracle…",
        "Summoning latent space spirits…",
        "Stretching the visible universe…",
        "Inventing scenery on the fly…",
        "Hallucinating extra background…",
        "Filling in the void…",
        "Negotiating with the noise schedule…",
        "Calibrating the creativity dial…",
        "Warming up the denoiser…",
        "Sprinkling magic attention dust…",
        "Asking the VAE nicely…",
        "Painting with invisible brushes…"
    ];
    private static readonly string[] PreferredQuantOrder =
    [
        "Q8_0", "Q6_K", "Q5_K_M", "Q5_K_S", "Q5_1", "Q5_0",
        "Q4_K_M", "Q4_K_S", "Q4_1", "Q4_0",
        "Q3_K_M", "Q3_K_S", "Q2_K", "F16", "BF16"
    ];
    private readonly Random _random = new();

    private bool _isPanelOpen;
    private string _outpaintResolutionText = string.Empty;
    private string _positivePrompt = string.Empty;
    private string _negativePrompt = DefaultNegativePrompt;
    private bool _isBusy;
    private string? _status;
    private int _progress;
    private bool _hasError;
    private string? _progressDisplayText;
    private int _lastDisplayTextIndex = -1;

    public OutpaintingViewModel(
        Func<bool> hasImage,
        Func<int> getImageWidth,
        Func<int> getImageHeight,
        Action<string> deactivateOtherTools,
        IComfyUIWrapperService? comfyUiService = null,
        IComfyUIReadinessService? readinessService = null)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(getImageWidth);
        ArgumentNullException.ThrowIfNull(getImageHeight);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);

        _hasImage = hasImage;
        _getImageWidth = getImageWidth;
        _getImageHeight = getImageHeight;
        _deactivateOtherTools = deactivateOtherTools;
        _comfyUiService = comfyUiService;

        Readiness = new ComfyUIReadinessViewModel(readinessService, ComfyUIFeature.Outpaint);
        VisionReadiness = new ComfyUIReadinessViewModel(readinessService, ComfyUIFeature.OutpaintVision);

        ToggleCommand = new RelayCommand(ExecuteToggle, () => _hasImage());
        ResetCommand = new RelayCommand(ExecuteReset, () => _hasImage() && IsPanelOpen);
        CancelCommand = new RelayCommand(ExecuteCancel, () => IsPanelOpen);
        SetAspectRatioCommand = new RelayCommand<string>(ExecuteSetAspectRatio, _ => _hasImage() && IsPanelOpen);
        GenerateCommand = new AsyncRelayCommand(
            () => ExecuteGenerateAsync(useVision: false),
            () => _hasImage() && IsPanelOpen && !IsBusy);
        GenerateVisionCommand = new AsyncRelayCommand(
            () => ExecuteGenerateAsync(useVision: true),
            () => _hasImage() && IsPanelOpen && !IsBusy);
    }

    /// <summary>Readiness check for the prompt-driven Outpaint workflow.</summary>
    public ComfyUIReadinessViewModel Readiness { get; }

    /// <summary>Readiness check for the Vision (Qwen3-VL auto-prompt) Outpaint workflow.</summary>
    public ComfyUIReadinessViewModel VisionReadiness { get; }

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

    /// <summary>User-supplied positive prompt (used by the nonVision workflow).</summary>
    public string PositivePrompt
    {
        get => _positivePrompt;
        set => SetProperty(ref _positivePrompt, value ?? string.Empty);
    }

    /// <summary>Negative prompt applied to both Vision and nonVision workflows.</summary>
    public string NegativePrompt
    {
        get => _negativePrompt;
        set => SetProperty(ref _negativePrompt, value ?? string.Empty);
    }

    /// <summary>Whether an outpainting operation is currently in progress.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                OnPropertyChanged(nameof(IsProgressVisible));
        }
    }

    /// <summary>Status message for the current outpainting operation.</summary>
    public string? Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
                ParseProgress(value);
        }
    }

    /// <summary>Progress percentage (0-100) for the current outpainting operation.</summary>
    public int OutpaintProgress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    /// <summary>Whether the current outpainting operation encountered an error.</summary>
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

    /// <summary>Generates the outpainted image using the user-supplied prompt (nonVision workflow).</summary>
    public IAsyncRelayCommand GenerateCommand { get; }

    /// <summary>Generates the outpainted image using the Qwen3-VL Vision auto-prompt workflow.</summary>
    public IAsyncRelayCommand GenerateVisionCommand { get; }

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

    /// <summary>
    /// Raised when the user requests outpaint generation. The View must respond by
    /// preparing a temp PNG of the current canvas and invoking <see cref="ProcessOutpaintAsync"/>.
    /// </summary>
    public event EventHandler<OutpaintGenerateEventArgs>? GenerateRequested;

    /// <summary>Raised when the outpaint result image bytes are ready.</summary>
    public event EventHandler<byte[]>? ResultReady;

    // TODO: Linux Implementation for Outpainting

    #endregion

    #region Public Methods

    /// <summary>Notifies all commands that their CanExecute state may have changed.</summary>
    public void RefreshCommandStates()
    {
        ToggleCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        SetAspectRatioCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
        GenerateVisionCommand.NotifyCanExecuteChanged();
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

    private async Task ExecuteGenerateAsync(bool useVision)
    {
        if (!useVision && string.IsNullOrWhiteSpace(_positivePrompt))
        {
            StatusMessageChanged?.Invoke(this, "Please enter a prompt describing the surroundings, or use 'Generate (Vision)'.");
            return;
        }

        if (_comfyUiService is null)
        {
            StatusMessageChanged?.Invoke(this, "ComfyUI service not available. Check ComfyUI server settings.");
            return;
        }

        ResetErrorState();
        IsBusy = true;
        Status = "Preparing image...";
        NotifyGenerateCommandsCanExecuteChanged();

        GenerateRequested?.Invoke(this, new OutpaintGenerateEventArgs(useVision));

        await Task.CompletedTask;
    }

    /// <summary>
    /// Processes the outpainting workflow via ComfyUI.
    /// Called by the View after it writes the prepared canvas image to a temp PNG file.
    /// </summary>
    public async Task ProcessOutpaintAsync(string imagePath, bool useVision,
        int extendLeft, int extendTop, int extendRight, int extendBottom)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        if (_comfyUiService is null)
        {
            StatusMessageChanged?.Invoke(this, "ComfyUI service not available.");
            OnFinished();
            return;
        }

        try
        {
            Status = "Uploading image to ComfyUI...";
            var uploadedFilename = await _comfyUiService.UploadImageAsync(imagePath);

            Status = "Checking available models...";
            var resolvedUnetName = await ResolveQwenImageGGUFModelAsync();
            if (resolvedUnetName is null)
            {
                HasError = true;
                ProgressDisplayText = "No Qwen Image GGUF model found";
                StatusMessageChanged?.Invoke(this,
                    "No Qwen Image 2512 GGUF model found in ComfyUI. " +
                    "Please download a qwen-image-2512 GGUF variant (e.g. Q8_0, Q4_K_M) " +
                    "and place it in your ComfyUI diffusion_models folder.");
                OnFinished();
                return;
            }

            Status = "Queuing outpainting workflow...";

            var workflowRelativePath = useVision
                ? OutpaintVisionWorkflowPath
                : OutpaintNonVisionWorkflowPath;

            var workflowPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                workflowRelativePath);

            if (!File.Exists(workflowPath))
            {
                HasError = true;
                ProgressDisplayText = "Outpainting workflow file missing";
                StatusMessageChanged?.Invoke(this, $"Outpainting workflow not found: {workflowPath}");
                OnFinished();
                return;
            }

            var seed = (long)(_random.NextDouble() * long.MaxValue);

            var overrides = new Dictionary<string, Action<System.Text.Json.Nodes.JsonNode>>
            {
                [LoadImageNodeId] = node =>
                {
                    node["inputs"]!["image"] = uploadedFilename;
                },
                [NegativePromptNodeId] = node =>
                {
                    node["inputs"]!["text"] = _negativePrompt;
                },
                [KSamplerNodeId] = node =>
                {
                    node["inputs"]!["seed"] = seed;
                },
                [UnetLoaderNodeId] = node =>
                {
                    node["inputs"]!["unet_name"] = resolvedUnetName;
                },
                [ImagePadNodeId] = node =>
                {
                    node["inputs"]!["left"] = extendLeft;
                    node["inputs"]!["top"] = extendTop;
                    node["inputs"]!["right"] = extendRight;
                    node["inputs"]!["bottom"] = extendBottom;
                }
            };

            // Only override the positive prompt for nonVision; the Vision workflow
            // wires positive prompt to the Qwen3_VQA node output.
            if (!useVision)
            {
                overrides[PositivePromptNodeId] = node =>
                {
                    node["inputs"]!["text"] = _positivePrompt;
                };
            }

            var promptId = await _comfyUiService.QueueWorkflowAsync(workflowPath, overrides);

            Status = "Generating (this may take a while)...";
            var progress = new Progress<string>(msg => Status = msg);
            await _comfyUiService.WaitForCompletionAsync(promptId, progress);

            Status = "Downloading result...";
            var result = await _comfyUiService.GetResultAsync(promptId);

            if (result.Images.Count > 0)
            {
                var imageBytes = await _comfyUiService.DownloadImageAsync(result.Images[0]);
                ResultReady?.Invoke(this, imageBytes);
                StatusMessageChanged?.Invoke(this, "Outpainting completed successfully.");
            }
            else
            {
                StatusMessageChanged?.Invoke(this, "Outpainting completed but no output image was returned.");
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessageChanged?.Invoke(this, "Outpainting was cancelled.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Outpainting failed");
            HasError = true;
            ProgressDisplayText = "Generation failed – is ComfyUI running?";
            StatusMessageChanged?.Invoke(this, $"Outpainting failed: {ex.Message}");
        }
        finally
        {
            OnFinished();
        }
    }

    private void OnFinished()
    {
        IsBusy = false;

        if (_hasError)
        {
            OutpaintProgress = 100;
        }
        else
        {
            Status = null;
            OutpaintProgress = 0;
            ProgressDisplayText = null;
            _lastDisplayTextIndex = -1;
        }

        NotifyGenerateCommandsCanExecuteChanged();
    }

    private void ResetErrorState()
    {
        HasError = false;
        ProgressDisplayText = null;
        _lastDisplayTextIndex = -1;
    }

    private void NotifyGenerateCommandsCanExecuteChanged()
    {
        GenerateCommand.NotifyCanExecuteChanged();
        GenerateVisionCommand.NotifyCanExecuteChanged();
    }

    private void ParseProgress(string? status)
    {
        if (string.IsNullOrEmpty(status))
        {
            OutpaintProgress = 0;
            ProgressDisplayText = null;
            return;
        }

        if (status.StartsWith("Progress:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = status["Progress:".Length..].Trim().Split('/');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var value)
                && int.TryParse(parts[1], out var max)
                && max > 0)
            {
                OutpaintProgress = (int)(30 + (double)value / max * 60);
                PickRandomDisplayText();
                return;
            }
        }

        OutpaintProgress = status switch
        {
            _ when status.StartsWith("Preparing", StringComparison.OrdinalIgnoreCase) => 5,
            _ when status.StartsWith("Uploading", StringComparison.OrdinalIgnoreCase) => 10,
            _ when status.StartsWith("Queuing", StringComparison.OrdinalIgnoreCase) => 15,
            _ when status.StartsWith("Generating", StringComparison.OrdinalIgnoreCase) => 20,
            _ when status.StartsWith("Executing", StringComparison.OrdinalIgnoreCase) => 25,
            _ when status.StartsWith("Running", StringComparison.OrdinalIgnoreCase) => 25,
            _ when status.StartsWith("Loading", StringComparison.OrdinalIgnoreCase) => 25,
            _ when status.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase) => 95,
            _ => _progress
        };
        PickRandomDisplayText();
    }

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

    private async Task<string?> ResolveQwenImageGGUFModelAsync()
    {
        if (_comfyUiService is null)
            return null;

        try
        {
            var availableModels = await _comfyUiService.GetNodeInputOptionsAsync(
                UnetLoaderGGUFNodeType, "unet_name");

            var qwenModels = availableModels
                .Where(m => m.StartsWith(QwenImageGGUFPrefix, StringComparison.OrdinalIgnoreCase)
                         && m.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (qwenModels.Count == 0)
            {
                Logger.Warning("No {Prefix}*.gguf models found on ComfyUI server", QwenImageGGUFPrefix);
                return null;
            }

            foreach (var quant in PreferredQuantOrder)
            {
                var expected = $"{QwenImageGGUFPrefix}{quant}.gguf";
                var match = qwenModels.FirstOrDefault(
                    m => m.Equals(expected, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    Logger.Information("Resolved Qwen Image GGUF model: {Model}", match);
                    return match;
                }
            }

            var fallback = qwenModels[0];
            Logger.Information("Using fallback Qwen Image GGUF model: {Model}", fallback);
            return fallback;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to query ComfyUI for available GGUF models, falling back to {Default}", DefaultQwenImageGGUF);
            return DefaultQwenImageGGUF;
        }
    }

    #endregion
}

/// <summary>Event args for an outpaint generate request.</summary>
public class OutpaintGenerateEventArgs : EventArgs
{
    /// <summary>Whether to use the Vision (auto-prompt) workflow.</summary>
    public bool UseVision { get; }

    public OutpaintGenerateEventArgs(bool useVision)
    {
        UseVision = useVision;
    }
}
