using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.Inference.StableDiffusionCpp;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Diffusion;
using Serilog;
using SkiaSharp;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Sub-ViewModel managing inpainting tool state, ComfyUI workflow execution, and mask settings.
/// Extracted from <see cref="ImageEditorViewModel"/> to reduce its size.
/// </summary>
public partial class InpaintingViewModel : ObservableObject
{
    private static readonly ILogger Logger = Log.ForContext<InpaintingViewModel>();

    private readonly Func<bool> _hasImage;
    private readonly Action<string> _deactivateOtherTools;
    private readonly IComfyUIWrapperService? _comfyUiService;
    private readonly IDatasetEventAggregator? _eventAggregator;
    private readonly LocalDiffusionBackendProvider? _backendProvider;
    private readonly IUnifiedLogger? _unifiedLogger;

    /// <summary>On-disk name of the 4-step Qwen-Image Lightning LoRA (installed by the inpaint workload).</summary>
    private const string LightningLoraFileName = "Qwen-Image-Lightning-4steps-V1.0.safetensors";

    private const string InpaintWorkflowPath = "Assets/Workflows/Inpaint-Qwen-2512.json";
    private const string InpaintLoadImageNodeId = "16";
    private const string InpaintPositivePromptNodeId = "5";
    private const string InpaintNegativePromptNodeId = "8";
    private const string InpaintKSamplerNodeId = "11";
    private const string InpaintUnetLoaderNodeId = "15";
    private const string UnetLoaderGGUFNodeType = "UnetLoaderGGUF";
    private const string QwenImageGGUFPrefix = "qwen-image-2512-";
    private const string DefaultQwenImageGGUF = "qwen-image-2512-Q8_0.gguf";
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
        IDatasetEventAggregator? eventAggregator,
        IFeatureReadinessService? readinessService = null,
        LocalDiffusionBackendProvider? backendProvider = null,
        IUnifiedLogger? unifiedLogger = null)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);
        _hasImage = hasImage;
        _deactivateOtherTools = deactivateOtherTools;
        _comfyUiService = comfyUiService;
        _eventAggregator = eventAggregator;
        _backendProvider = backendProvider;
        _unifiedLogger = unifiedLogger;

        Readiness = new FeatureReadinessViewModel(readinessService, Feature.Inpainting, allowBackendSelection: true);

        ClearMaskCommand = new RelayCommand(
            () => ClearMaskRequested?.Invoke(this, EventArgs.Empty),
            () => _hasImage() && IsPanelOpen);
        GenerateCommand = new AsyncRelayCommand(
            ExecuteGenerateAsync,
            () => _hasImage() && IsPanelOpen && !IsBusy && IsReadinessClickable(Readiness));
        GenerateAndCompareCommand = new AsyncRelayCommand(
            ExecuteGenerateAndCompareAsync,
            () => _hasImage() && IsPanelOpen && !IsBusy && IsReadinessClickable(Readiness));
        UseCurrentAsBaseCommand = new RelayCommand(
            () => SetBaseRequested?.Invoke(this, EventArgs.Empty),
            () => _hasImage() && IsPanelOpen);

        // Re-evaluate Generate CanExecute when readiness flips, so the buttons grey out
        // automatically once the workload reports as not fully installed (matching what
        // the Installer Manager workload dialog would show).
        Readiness.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(FeatureReadinessViewModel.IsReady)
                                  or nameof(FeatureReadinessViewModel.HasChecked))
            {
                GenerateCommand.NotifyCanExecuteChanged();
                GenerateAndCompareCommand.NotifyCanExecuteChanged();
            }
        };
    }

    /// <summary>
    /// A feature is clickable once readiness either reports ready, or readiness has never
    /// been checked yet (initial state — the button shouldn't be disabled before we know).
    /// As soon as the first check completes with <c>IsReady=false</c>, the button greys out.
    /// </summary>
    private static bool IsReadinessClickable(FeatureReadinessViewModel readiness) =>
        !readiness.HasChecked || readiness.IsReady;

    /// <summary>
    /// Runs the inpainting readiness check. Fired automatically when the panel opens so the
    /// Generate buttons reflect installation state without the user having to press "Check".
    /// </summary>
    private async Task RunReadinessCheckAsync()
    {
        try
        {
            await Readiness.CheckReadinessAsync();
        }
        catch (OperationCanceledException)
        {
            // Cancellation during panel close — nothing to do.
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Inpaint readiness check failed");
        }
    }

    /// <summary>Unified readiness check for the Inpainting feature (server, nodes, models).</summary>
    public FeatureReadinessViewModel Readiness { get; }

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
                    _ = RunReadinessCheckAsync();

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

    /// <summary>Event raised when the inpaint mask should be hidden (after successful send to ComfyUI).</summary>
    public event EventHandler? HideMaskRequested;

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

    /// <summary>
    /// Aborts a generation that set the busy state (via Generate) but never reached a Process*
    /// method — e.g. the View couldn't prepare the image/mask. Resets <see cref="IsBusy"/> so the
    /// Generate buttons re-enable. Without this, an early return in the View's generate handler
    /// would leave the buttons permanently greyed out.
    /// </summary>
    public void AbortGeneration(string? statusMessage = null)
    {
        if (!string.IsNullOrWhiteSpace(statusMessage))
            StatusMessageChanged?.Invoke(this, statusMessage);
        OnFinished();
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

            Status = "Queuing inpainting workflow...";

            var workflowPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                InpaintWorkflowPath);

            if (!File.Exists(workflowPath))
            {
                HasError = true;
                ProgressDisplayText = "Inpainting workflow file missing";
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
                    },
                    [InpaintUnetLoaderNodeId] = node =>
                    {
                        node["inputs"]!["unet_name"] = resolvedUnetName;
                    }
                });

            Status = "Generating (this may take a while)...";
            var maskHidden = false;
            var progress = new Progress<string>(msg =>
            {
                Status = msg;

                // Hide the mask on the first real progress update from ComfyUI,
                // meaning the server has picked up the job and is actively working.
                if (!maskHidden)
                {
                    maskHidden = true;
                    HideMaskRequested?.Invoke(this, EventArgs.Empty);
                }
            });
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

    /// <summary>
    /// Processes inpainting <b>locally</b> via the DiffusionNexus core (stable-diffusion.cpp),
    /// mirroring the ComfyUI Qwen-Image inpaint workflow: Qwen-Image-2512 + the InstantX inpainting
    /// ControlNet + the 4-step Lightning LoRA, with the painted mask confining regeneration. The View
    /// supplies the base image and the white/black mask as separate PNGs. Called when the user has
    /// picked "Diffusion Nexus Core" in the readiness panel's backend dropdown.
    /// </summary>
    public async Task ProcessInpaintLocalAsync(string baseImagePath, string maskImagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(maskImagePath);

        if (_backendProvider is null)
        {
            HasError = true;
            ProgressDisplayText = "Local renderer unavailable";
            StatusMessageChanged?.Invoke(this, "The local DiffusionNexus core renderer is not available.");
            OnFinished();
            return;
        }

        string? alignedBase = null;
        string? alignedMask = null;
        try
        {
            Status = "Loading local renderer...";
            _unifiedLogger?.Info(LogCategory.General, "Inpaint (Local)", "Resolving local renderer…");
            var backend = await _backendProvider.TryGetAsync();
            if (backend is null)
            {
                HasError = true;
                ProgressDisplayText = "No ComfyUI models folder found";
                StatusMessageChanged?.Invoke(this,
                    "The local renderer needs a ComfyUI installation's models folder (where the inpaint " +
                    "models are downloaded). Add one in the Installer Manager, then install the Inpaint workload.");
                OnFinished();
                return;
            }

            _unifiedLogger?.Info(LogCategory.General, "Inpaint (Local)",
                $"Renderer ready (model roots: {string.Join(" | ", _backendProvider.ResolvedModelsRoots)}). Preparing image + mask…");

            // Qwen-Image needs /16-aligned dimensions and inpaint requires the init image, mask and
            // output to share one size — so resize the base + mask to the aligned size. The View
            // resizes the result back to the canvas size on ResultReady.
            var (width, height) = ComputeAlignedDimensions(baseImagePath);
            alignedBase = ResizeToTempPng(baseImagePath, width, height);
            alignedMask = ResizeToTempPng(maskImagePath, width, height);

            var loras = new List<LoraReference>();
            var lightning = await Task.Run(FindLightningLora);
            if (lightning is not null)
                loras.Add(new LoraReference(lightning, 1.0f));
            else
                Logger.Warning(
                    "Qwen-Image Lightning 4-step LoRA ({File}) not found under the models roots; " +
                    "local inpaint will run without it (results may need more steps).", LightningLoraFileName);

            // sd.cpp pins unmasked pixels to the VAE-encoded init latent each step — but only when the
            // init image is actually encoded. At strength 1.0 it starts from pure noise and the
            // unmasked region isn't preserved, so cap the inpaint denoise below 1.0.
            var inpaintStrength = Math.Min(_denoise, 0.85f);
            if (inpaintStrength < _denoise)
                _unifiedLogger?.Info(LogCategory.General, "Inpaint (Local)",
                    $"Denoise capped {_denoise:F2}→{inpaintStrength:F2} so the mask preserves the unmasked area (use the Denoise slider to fine-tune).");

            _unifiedLogger?.Info(LogCategory.General, "Inpaint (Local)",
                $"Aligned base+mask to {width}x{height}; Lightning LoRA: {(lightning ?? "(not found — running without it)")}. Submitting to Qwen-Image…");

            // Native Qwen-Image-2512 masked inpaint + the 4-step Lightning LoRA (4 steps, cfg 1,
            // euler/simple). The base image is the img2img init; the mask confines regeneration to the
            // painted region while unmasked pixels are preserved. NOTE: the ComfyUI workflow's InstantX
            // inpainting ControlNet is NOT used here — this stable-diffusion.cpp build can't load a
            // Qwen-Image DiT ControlNet, so we fall back to native mask inpaint (no ControlNets set).
            var request = new DiffusionRequest
            {
                ModelKey = ModelKeys.QwenImageInpaint,
                Prompt = _positivePrompt,
                NegativePrompt = _negativePrompt,
                Width = width,
                Height = height,
                Steps = 4,
                Cfg = 1.0f,
                Sampler = "euler",
                Scheduler = "simple",
                InitImage = new DiffusionReferenceImage(alignedBase, inpaintStrength),
                MaskImage = new DiffusionReferenceImage(alignedMask),
                Loras = loras,
                Seed = (long)(_random.NextDouble() * long.MaxValue),
            };

            Status = "Generating (local)...";
            var maskHidden = false;
            byte[]? png = null;

            await foreach (var item in backend.GenerateAsync(request))
            {
                Status = MapLocalProgress(item.Progress);

                // Hide the mask once the sampler is actively running (the result will replace it).
                if (!maskHidden && item.Progress.Phase == DiffusionPhase.Sampling)
                {
                    maskHidden = true;
                    HideMaskRequested?.Invoke(this, EventArgs.Empty);
                }

                if (item.Result is { } result)
                    png = result.PngBytes;
            }

            if (png is not null)
            {
                ResultReady?.Invoke(this, png);
                StatusMessageChanged?.Invoke(this, "Inpainting completed successfully.");
                await HandleLocalCompareAsync(png);
            }
            else
            {
                HasError = true;
                ProgressDisplayText = "No image produced";
                StatusMessageChanged?.Invoke(this,
                    "Local inpainting finished without an image. Check the Unified Console for native engine errors.");
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessageChanged?.Invoke(this, "Inpainting was cancelled.");
        }
        catch (Exception ex)
        {
            HasError = true;
            ProgressDisplayText = "Local generation failed";
            StatusMessageChanged?.Invoke(this, $"Local inpainting failed: {ex.Message}");
            Logger.Error(ex, "Local inpaint generation failed");
        }
        finally
        {
            TryDeleteTemp(alignedBase);
            TryDeleteTemp(alignedMask);
            OnFinished();
        }
    }

    #endregion

    #region Private Methods

    /// <summary>Publishes the before/after pair to the Image Comparer when a compare run is pending.</summary>
    private async Task HandleLocalCompareAsync(byte[] resultPng)
    {
        if (string.IsNullOrEmpty(_pendingCompareBeforeImagePath))
            return;

        var afterPath = Path.Combine(Path.GetTempPath(), $"diffnexus_inpaint_after_{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(afterPath, resultPng);

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

    /// <summary>Maps a local backend progress item onto the existing ComfyUI-style status strings.</summary>
    private static string MapLocalProgress(DiffusionProgress p) => p.Phase switch
    {
        DiffusionPhase.Loading => string.IsNullOrWhiteSpace(p.Message) ? "Loading model..." : p.Message,
        DiffusionPhase.Encoding => "Running...",
        DiffusionPhase.Sampling => p.TotalSteps > 0 ? $"Progress: {p.Step}/{p.TotalSteps}" : "Generating...",
        DiffusionPhase.Decoding => "Downloading result...",
        DiffusionPhase.Completed => "Downloading result...",
        _ => "Generating...",
    };

    /// <summary>Computes the nearest /16-aligned output dimensions for the supplied image.</summary>
    private static (int Width, int Height) ComputeAlignedDimensions(string imagePath)
    {
        int width = 1024, height = 1024;
        try
        {
            using var codec = SKCodec.Create(imagePath);
            if (codec?.Info is { Width: > 0, Height: > 0 } info)
            {
                width = info.Width;
                height = info.Height;
            }
        }
        catch
        {
            // keep 1024² fallback
        }

        return (Align(width), Align(height));

        static int Align(int value)
        {
            var clamped = Math.Clamp(value, 256, 2048);
            var rounded = (int)(Math.Round(clamped / 16.0) * 16);
            return Math.Max(16, rounded);
        }
    }

    /// <summary>Resizes a PNG to the given dimensions and writes it to a fresh temp file; returns the path.</summary>
    private static string ResizeToTempPng(string sourcePath, int width, int height)
    {
        using var src = SKBitmap.Decode(sourcePath)
            ?? throw new InvalidOperationException($"Could not decode image: {sourcePath}");

        var outPath = Path.Combine(Path.GetTempPath(), $"diffnexus_inpaint_aligned_{Guid.NewGuid():N}.png");

        if (src.Width == width && src.Height == height)
        {
            File.Copy(sourcePath, outPath, overwrite: true);
            return outPath;
        }

        using var resized = src.Resize(new SKImageInfo(width, height), SKFilterQuality.High)
            ?? throw new InvalidOperationException("Image resize failed.");
        using var img = SKImage.FromBitmap(resized);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(outPath, data.ToArray());
        return outPath;
    }

    /// <summary>Finds the Lightning 4-step LoRA across the local renderer's models roots, or null.</summary>
    private string? FindLightningLora()
    {
        var roots = _backendProvider?.ResolvedModelsRoots ?? [];
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;
            try
            {
                var hit = Directory.EnumerateFiles(root, LightningLoraFileName, options).FirstOrDefault();
                if (hit is not null)
                    return hit;
            }
            catch
            {
                // skip inaccessible roots
            }
        }

        return null;
    }

    private static void TryDeleteTemp(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    /// <summary>Whether the user has selected the local DiffusionNexus core backend in the readiness panel.</summary>
    private bool IsLocalBackendSelected =>
        Readiness.SelectedBackend?.Kind == BackendKind.LocalInference;

    private async Task ExecuteGenerateAsync()
    {
        if (!CheckReadinessOrReport()) return;

        if (string.IsNullOrWhiteSpace(_positivePrompt))
        {
            StatusMessageChanged?.Invoke(this, "Please enter a prompt describing what to generate.");
            return;
        }

        if (!IsLocalBackendSelected && _comfyUiService is null)
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
        if (!CheckReadinessOrReport()) return;

        if (string.IsNullOrWhiteSpace(_positivePrompt))
        {
            StatusMessageChanged?.Invoke(this, "Please enter a prompt describing what to generate.");
            return;
        }

        if (!IsLocalBackendSelected && _comfyUiService is null)
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

    /// <summary>
    /// Surfaces a clear status message and returns <c>false</c> when readiness has already
    /// reported the workload as not fully installed, so the click is short-circuited before
    /// any image-export or upload work happens.
    /// </summary>
    private bool CheckReadinessOrReport()
    {
        if (Readiness.HasChecked && !Readiness.IsReady)
        {
            var detail = Readiness.MissingRequirements.Count > 0
                ? Readiness.MissingRequirements[0]
                : "Required nodes or models are missing.";
            StatusMessageChanged?.Invoke(this, detail);
            return false;
        }

        return true;
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

    /// <summary>
    /// Preferred GGUF quantization variants in descending quality order.
    /// Higher-quality variants are tried first; the first match found on the server wins.
    /// </summary>
    private static readonly string[] PreferredQuantOrder =
    [
        "Q8_0",
        "Q6_K",
        "Q5_K_M",
        "Q5_K_S",
        "Q5_1",
        "Q5_0",
        "Q4_K_M",
        "Q4_K_S",
        "Q4_1",
        "Q4_0",
        "Q3_K_M",
        "Q3_K_S",
        "Q2_K",
        "F16",
        "BF16"
    ];

    /// <summary>
    /// Queries ComfyUI for available UnetLoaderGGUF models and returns the best
    /// <c>qwen-image-2512-*.gguf</c> variant, or <c>null</c> if none is installed.
    /// </summary>
    private async Task<string?> ResolveQwenImageGGUFModelAsync()
    {
        if (_comfyUiService is null)
            return null;

        try
        {
            var availableModels = await _comfyUiService.GetNodeInputOptionsAsync(
                UnetLoaderGGUFNodeType, "unet_name");

            // Filter to only qwen-image-2512 GGUF variants (case-insensitive)
            var qwenModels = availableModels
                .Where(m => m.StartsWith(QwenImageGGUFPrefix, StringComparison.OrdinalIgnoreCase)
                         && m.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (qwenModels.Count == 0)
            {
                Logger.Warning("No {Prefix}*.gguf models found on ComfyUI server", QwenImageGGUFPrefix);
                return null;
            }

            // Pick the best variant by walking the preference list
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

            // Fallback: use the first available variant even if not in our preference list
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
