using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.Inference.Models;
using DiffusionNexus.Inference.StableDiffusionCpp;
using DiffusionNexus.UI.Services.Diffusion;
using Serilog;

namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>
/// ViewModel for the Diffusion Canvas module. Owns the collection of generation frames
/// the user has placed on the infinite canvas, the global prompt textbox, and the
/// Generate command that drives the local diffusion backend.
/// </summary>
public partial class DiffusionCanvasViewModel : ObservableObject
{
    private static readonly ILogger Logger = Log.ForContext<DiffusionCanvasViewModel>();
    private readonly LocalDiffusionBackendProvider? _backendProvider;
    private int _nextFrameOffset;

    /// <summary>All frames currently on the canvas, in z-order (last = top).</summary>
    public ObservableCollection<GenerationFrameViewModel> Frames { get; } = [];

    /// <summary>The canvas-level prompt, used as the default for new frames.</summary>
    [ObservableProperty]
    private string _promptText = string.Empty;

    /// <summary>True while a generation is running (used to disable the Generate button).</summary>
    [ObservableProperty]
    private bool _isGenerating;

    /// <summary>Toolbar status text ("Idle", "Loading Z-Image-Turbo…", "Sampling 5/9", "Done", "Error: …").</summary>
    [ObservableProperty]
    private string _statusText = "Idle";

    /// <summary>Backend availability message; non-null when the backend cannot be initialized.</summary>
    [ObservableProperty]
    private string? _backendUnavailableMessage;

    #region v2 Placeholder properties (bound to disabled UI controls)

    // TODO(v2-negative-prompt): bind the negative prompt UI control to this once enabled.
    [ObservableProperty]
    private string _negativePromptText = string.Empty;

    // TODO(v2-seed): wire to the seed UI when enabled. Random when null.
    [ObservableProperty]
    private long? _seed;

    // TODO(v2-seed): toggle for the random/fixed seed UI.
    [ObservableProperty]
    private bool _useRandomSeed = true;

    // TODO(v2-advanced): bind to the advanced sampling expander (Steps slider).
    [ObservableProperty]
    private int _steps = 9;

    // TODO(v2-advanced): bind to the CFG slider.
    [ObservableProperty]
    private float _cfg = 1.0f;

    // TODO(v2-advanced): bind to the sampler combo. Values: euler, euler_a, dpmpp2m, …
    [ObservableProperty]
    private string _selectedSampler = "euler";

    // TODO(v2-loras): observable list bound to the LoRA picker (each item carries path + strength).
    public ObservableCollection<object> Loras { get; } = [];

    // TODO(v2-backend-dropdown): values "Local (Z-Image-Turbo)" / "ComfyUI (coming soon)".
    [ObservableProperty]
    private string _selectedBackend = "Local (Z-Image-Turbo)";

    public IReadOnlyList<string> AvailableBackends { get; } =
        ["Local (Z-Image-Turbo)", "ComfyUI (coming soon)"];

    #endregion

    #region v2 Placeholder commands (wired to disabled UI controls)

    // TODO(v2-cancel): plumb a cancellation token through the backend once stable-diffusion.cpp exposes a cancel hook.
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => Logger.Information("Cancel requested — TODO(v2-cancel): not implemented in v1.");
    private bool CanCancel() => false;

    // TODO(v2-loras): show the LoRA picker dialog and add to Loras.
    [RelayCommand(CanExecute = nameof(AlwaysFalse))]
    private void AddLora() { /* placeholder */ }

    // TODO(v2-controlnet): open the ControlNet add dialog (image picker + preprocessor + strength).
    [RelayCommand(CanExecute = nameof(AlwaysFalse))]
    private void AddControlNet() { /* placeholder */ }

    // TODO(v2-mask-tools): activate brush mode in the canvas overlay.
    [RelayCommand(CanExecute = nameof(AlwaysFalse))]
    private void ActivateBrushTool() { /* placeholder */ }

    // TODO(v2-mask-tools): activate eraser mode in the canvas overlay.
    [RelayCommand(CanExecute = nameof(AlwaysFalse))]
    private void ActivateEraserTool() { /* placeholder */ }

    // TODO(v2-mask-tools): activate inpaint mask painting overlay.
    [RelayCommand(CanExecute = nameof(AlwaysFalse))]
    private void ActivateMaskTool() { /* placeholder */ }

    // TODO(v2-layers): toggle the layer panel side-flyout.
    [RelayCommand(CanExecute = nameof(AlwaysFalse))]
    private void ToggleLayerPanel() { /* placeholder */ }

    // TODO(v2-undo): real undo via a Command stack.
    [RelayCommand(CanExecute = nameof(AlwaysFalse))]
    private void Undo() { /* placeholder */ }

    // TODO(v2-undo): real redo via a Command stack.
    [RelayCommand(CanExecute = nameof(AlwaysFalse))]
    private void Redo() { /* placeholder */ }

    private static bool AlwaysFalse() => false;

    #endregion

    public DiffusionCanvasViewModel()
    {
        // Design-time ctor: no backend, leave a friendly placeholder frame so the designer renders.
        _backendProvider = null;
        Frames.Add(new GenerationFrameViewModel
        {
            CanvasX = 200,
            CanvasY = 200,
            Prompt = "(design-time preview)",
            StatusText = "Press Generate to start",
        });
    }

    public DiffusionCanvasViewModel(LocalDiffusionBackendProvider backendProvider)
    {
        _backendProvider = backendProvider ?? throw new ArgumentNullException(nameof(backendProvider));
        DeleteFrameCommand = new RelayCommand<GenerationFrameViewModel?>(DeleteFrame);
    }

    /// <summary>Right-click → Delete frame. Wired in v1 (the only enabled context-menu entry).</summary>
    public IRelayCommand<GenerationFrameViewModel?>? DeleteFrameCommand { get; }

    private void DeleteFrame(GenerationFrameViewModel? frame)
    {
        if (frame is null) return;
        Frames.Remove(frame);
    }

    /// <summary>
    /// Generate command — creates a new frame at an offset from the last one, kicks off
    /// the backend stream, and updates the frame as progress events arrive.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (_backendProvider is null)
        {
            BackendUnavailableMessage = "Diffusion backend is not available in design mode.";
            return;
        }

        if (string.IsNullOrWhiteSpace(PromptText))
        {
            StatusText = "Please enter a prompt before generating.";
            return;
        }

        IsGenerating = true;
        StatusText = "Resolving backend…";
        BackendUnavailableMessage = null;

        try
        {
            var backend = await _backendProvider.TryGetAsync().ConfigureAwait(true);
            if (backend is null)
            {
                BackendUnavailableMessage =
                    "No ComfyUI installation found. Add a ComfyUI installation in the Installer Manager so the canvas can locate the models folder.";
                StatusText = "Backend unavailable";
                return;
            }

            var descriptor = backend.Catalog.TryGet(ModelKeys.ZImageTurbo);
            if (descriptor is null)
            {
                BackendUnavailableMessage =
                    "Z-Image-Turbo files were not found in the ComfyUI models folder. Required: " +
                    "DiffusionModels/z_image_turbo_bf16.safetensors, TextEncoders/qwen_3_4b.safetensors, VAE/ae.safetensors.";
                StatusText = "Model unavailable";
                return;
            }

            // Create the new frame at an offset from the previous so they don't overlap.
            var offset = _nextFrameOffset++ * 40;
            var frame = new GenerationFrameViewModel
            {
                CanvasX = 100 + offset,
                CanvasY = 100 + offset,
                Width = descriptor.DefaultWidth,
                Height = descriptor.DefaultHeight,
                Prompt = PromptText,
                State = GenerationFrameState.Loading,
                StatusText = "Preparing…",
                DeleteCommand = DeleteFrameCommand,
            };
            Frames.Add(frame);

            await RunGenerationStreamAsync(backend, descriptor, frame).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Diffusion generation failed");
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
            GenerateCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanGenerate() => !IsGenerating && _backendProvider is not null;

    /// <summary>
    /// Drives the backend stream → frame UI updates. Marshals back to the UI thread because
    /// progress events fire on the channel reader's thread, not the dispatcher.
    /// </summary>
    private async Task RunGenerationStreamAsync(
        IDiffusionBackend backend,
        ModelDescriptor descriptor,
        GenerationFrameViewModel frame)
    {
        var request = new DiffusionRequest
        {
            ModelKey = descriptor.Key,
            Prompt = frame.Prompt,
            Width = frame.Width,
            Height = frame.Height,
            // v1 leaves Steps/Cfg/Sampler/Scheduler null so backend uses model defaults (Z-Image-Turbo: 9 / 1.0 / euler / simple).
            // TODO(v2-advanced): pass through Steps / Cfg / SelectedSampler when advanced UI is enabled.
            // TODO(v2-seed):     pass UseRandomSeed ? null : Seed.
            // TODO(v2-negative-prompt): pass NegativePromptText.
            Seed = UseRandomSeed ? null : Seed,
        };

        await foreach (var item in backend.GenerateAsync(request).ConfigureAwait(true))
        {
            ApplyProgress(frame, item);
        }
    }

    private void ApplyProgress(GenerationFrameViewModel frame, DiffusionStreamItem item)
    {
        // Always marshal to UI thread — backend producer runs on a Task.Run thread.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyProgress(frame, item));
            return;
        }

        switch (item.Progress.Phase)
        {
            case DiffusionPhase.Loading:
                frame.State = GenerationFrameState.Loading;
                frame.StatusText = item.Progress.Message ?? "Loading…";
                StatusText = frame.StatusText;
                break;

            case DiffusionPhase.Sampling:
                frame.State = GenerationFrameState.Sampling;
                frame.StepCurrent = item.Progress.Step;
                frame.StepTotal = item.Progress.TotalSteps;
                frame.StatusText = $"Sampling {item.Progress.Step}/{item.Progress.TotalSteps}";
                StatusText = frame.StatusText;
                break;

            case DiffusionPhase.Completed:
                if (item.Result is { } result)
                {
                    frame.Seed = result.Seed;
                    var path = SaveResultToOutputs(result);
                    frame.ImagePath = path;
                    frame.Image = LoadBitmap(result.PngBytes);
                    frame.State = GenerationFrameState.Completed;
                    frame.StatusText = $"Done in {result.Duration.TotalSeconds:N1}s";
                    StatusText = frame.StatusText;
                }
                else if (!string.IsNullOrEmpty(item.Progress.Message))
                {
                    // Error path — backend reports failure as a Completed message without a result.
                    frame.State = GenerationFrameState.Failed;
                    frame.StatusText = item.Progress.Message!;
                    StatusText = frame.StatusText;
                }
                break;
        }
    }

    private static string SaveResultToOutputs(DiffusionResult result)
    {
        Directory.CreateDirectory(OutputsFolderRegistrar.OutputsDirectory);
        var fileName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{result.Seed}.png";
        var path = Path.Combine(OutputsFolderRegistrar.OutputsDirectory, fileName);
        File.WriteAllBytes(path, result.PngBytes);
        return path;
    }

    private static Bitmap LoadBitmap(byte[] pngBytes)
    {
        using var ms = new MemoryStream(pngBytes);
        return new Bitmap(ms);
    }
}
