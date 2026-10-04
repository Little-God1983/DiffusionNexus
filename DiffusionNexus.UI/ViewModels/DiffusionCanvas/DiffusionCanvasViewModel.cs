using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.Inference.Models;
using DiffusionNexus.Inference.StableDiffusionCpp;
using DiffusionNexus.UI.DiffusionCanvas;
using DiffusionNexus.UI.Services.Diffusion;
using DiffusionNexus.UI.Services.Lora;
using DiffusionNexus.UI.ViewModels.Controls;
using Serilog;
using SkiaSharp;

namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>A selectable model in the generate panel (e.g. "Local (FLUX.2-klein)").</summary>
/// <param name="Descriptor">
/// The descriptor this option came from, carried so the panel can seed steps, guidance, sampler and
/// scheduler from the model's own defaults without re-querying a catalog — for the local backend that
/// query is a recursive multi-root disk walk. Null only at design time.
/// </param>
public sealed record CanvasModelOption(string Key, string DisplayName, ModelDescriptor? Descriptor = null);

/// <summary>Stable keys for the canvas backend dropdown.</summary>
public static class CanvasBackendKeys
{
    /// <summary>In-process stable-diffusion.cpp backend (the original canvas engine).</summary>
    public const string Local = "local";

    /// <summary>The app-owned ComfyUI engine.</summary>
    public const string Engine = "engine";
}

/// <summary>A selectable generation backend in the canvas title bar.</summary>
/// <param name="Capabilities">
/// What this backend honours. Read from a static so selecting a backend can gate the panel immediately,
/// without constructing the backend or running its readiness probe.
/// </param>
public sealed record CanvasBackendOption(string Key, string DisplayName, BackendCapabilities Capabilities);

/// <summary>
/// ViewModel for the Diffusion Canvas module.
///
/// The spatial model is one movable, resizable <see cref="GenerationBoundingBox"/> over an unbounded
/// world of accepted results: the box's size is the latent size, its position is where the pixels land,
/// and whatever is underneath it is what the model sees. Dragging it onto an existing result makes the
/// generation img2img without any extra mode; dragging it onto empty canvas makes it text2img.
///
/// Results arrive in <see cref="Staging"/> as candidates and only become <see cref="Frames"/> entries
/// when the user accepts them, so nothing touches the canvas unasked.
/// </summary>
public partial class DiffusionCanvasViewModel : ObservableObject, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<DiffusionCanvasViewModel>();

    /// <summary>Unified Console source string for every trace this module emits.</summary>
    private const string LogSource = "DiffusionCanvas";

    private readonly LocalDiffusionBackendProvider? _backendProvider;
    private readonly IDiffusionBackend? _engineBackend;
    private readonly IUnifiedLogger? _unifiedLogger;
    private readonly ILoraCatalog? _loraCatalog;

    /// <summary>
    /// Generation counter for the LoRA load. The load is fire-and-forget and the filter depends on the
    /// selected model, so a fast second selection can otherwise land its results after a slower first one
    /// and leave the picker showing the wrong model's LoRAs.
    /// </summary>
    private int _loraLoadGeneration;

    private bool _disposed;

    /// <summary>
    /// File name of the composited region handed to a backend as the init image. Fixed, and deliberately
    /// distinctive — see the remarks on <see cref="RegionScratchPath"/>.
    /// </summary>
    internal const string RegionScratchFileName = "diffusionnexus_canvas_region.png";

    /// <summary>
    /// Scratch file the composited region is written to before a backend reads it.
    /// </summary>
    /// <remarks>
    /// The <b>file name</b> is fixed for every run and every launch. The engine backend uploads this file
    /// into ComfyUI's own <c>input/</c> folder under its own name (<c>UploadImageAsync</c> posts
    /// <c>overwrite=true</c>), and nothing ever deletes that copy: a per-run name left hundreds of
    /// multi-megabyte PNGs behind over a session, and a per-launch GUID merely slowed the leak to one file
    /// per launch, forever. One fixed name means the engine holds exactly one such file, overwritten in
    /// place. It carries the app name because <c>overwrite=true</c> would clobber any user file of the
    /// same name in that folder. The per-process <b>directory</b> is what keeps concurrent app instances
    /// from overwriting each other's scratch.
    /// </remarks>
    private string RegionScratchPath => Path.Combine(ScratchDirectory, RegionScratchFileName);

    /// <summary>File name of the rasterised inpaint mask. Fixed for the same reason as <see cref="RegionScratchFileName"/>.</summary>
    internal const string MaskScratchFileName = "diffusionnexus_canvas_mask.png";

    /// <summary>Scratch file the rasterised inpaint mask is written to, beside the region's.</summary>
    private string MaskScratchPath => Path.Combine(ScratchDirectory, MaskScratchFileName);

    /// <summary>
    /// The per-process folder both scratch files live in; <see cref="Dispose"/> deletes it. A test seam:
    /// one process hosts many view models in tests, and one disposing must not delete another's files
    /// mid-run.
    /// </summary>
    internal string ScratchDirectory { get; set; } =
        Path.Combine(Path.GetTempPath(), "DiffusionNexus", $"canvas-{Environment.ProcessId}");

    /// <summary>All accepted results on the canvas, in z-order (last = top).</summary>
    public ObservableCollection<GenerationFrameViewModel> Frames { get; } = [];

    /// <summary>The generation region — the canvas's entire spatial model.</summary>
    public GenerationBoundingBox Box { get; } = new();

    /// <summary>Candidates awaiting a verdict.</summary>
    public CanvasStagingViewModel Staging { get; } = new();

    /// <summary>The layer stack over <see cref="Frames"/>: order, selection, lock (#594).</summary>
    public CanvasLayerStackViewModel Layers { get; }

    /// <summary>Whether the right-hand layer column is shown. Toggled by the tool strip's Layers button.</summary>
    [ObservableProperty]
    private bool _isLayerPanelVisible = true;

    /// <summary>The canvas-level prompt.</summary>
    [ObservableProperty]
    private string _promptText = string.Empty;

    /// <summary>
    /// True while the queue has a batch running or waiting; mirrors <see cref="CanvasGenerationQueue.IsBusy"/>.
    /// Cancel is live for exactly that long. Generate does not depend on it: a press while a batch runs
    /// queues another (#598).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isGenerating;

    /// <summary>Status bar text ("Idle", "Loading Z-Image-Turbo…", "Sampling 5/9", "Done", "Error: …").</summary>
    [ObservableProperty]
    private string _statusText = "Idle";

    /// <summary>Backend availability message; non-null when the backend cannot be initialized.</summary>
    [ObservableProperty]
    private string? _backendUnavailableMessage;

    /// <summary>Whether the dot grid is drawn on the surface.</summary>
    [ObservableProperty]
    private bool _showGrid = true;

    /// <summary>How many images one Generate enqueues. Runs sequentially — see the remarks on the runner.</summary>
    [ObservableProperty]
    private int _batchCount = 1;

    /// <summary>
    /// Denoise strength used when the box sits over existing pixels. 0 keeps the input, 1 ignores it.
    /// Only meaningful for an img2img run; the readout hides it when the box is over empty canvas.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DenoiseText))]
    private double _denoiseStrength = 0.65;

    /// <summary>The denoise slider's label, invariant for the same reason as <see cref="GuidanceText"/>.</summary>
    public string DenoiseText => string.Format(CultureInfo.InvariantCulture, "Denoise: {0:0.00}", DenoiseStrength);

    /// <summary>
    /// Describes what pressing Generate will actually do, given where the box currently sits — the
    /// difference between text2img and img2img is a drag, so the UI has to say which one it is.
    /// </summary>
    [ObservableProperty]
    private string _regionModeText = "Text to image — the box is over empty canvas";

    /// <summary>True when the box overlaps at least one accepted result, so the denoise control matters.</summary>
    [ObservableProperty]
    private bool _isRegionOccupied;

    /// <summary>
    /// True when Generate will run as an inpaint: the mask is visible, painted, meets the box, and the box
    /// is over pixels it can keep. The panel's Denoise then gives way to the mask's (#595).
    /// </summary>
    [ObservableProperty]
    private bool _isInpaintRun;

    /// <summary>
    /// The two-word form of <see cref="RegionModeText"/> for the panel's header chip, which has about
    /// 150px. The long form is the chip's tooltip, so nothing is lost.
    /// </summary>
    [ObservableProperty]
    private string _regionModeBadge = "Text to image";

    // ────────────────────────────── Generate panel (issue #518 region B) ──────────────────────────────

    /// <summary>The negative prompt. Honoured by both backends; see <see cref="IsNegativePromptSupported"/>.</summary>
    [ObservableProperty]
    private string _negativePromptText = string.Empty;

    /// <summary>
    /// The seed to generate from when <see cref="UseRandomSeed"/> is off. Null means "none chosen yet".
    /// </summary>
    /// <remarks>
    /// A batch adds the candidate index to this, so a locked seed of 1000 across three images produces
    /// 1000, 1001 and 1002 — reusing one seed for a whole batch would return the same image three times.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeedText))]
    private long? _seed;

    /// <summary>When set, each image gets a fresh random seed and <see cref="Seed"/> is ignored.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeedText))]
    [NotifyCanExecuteChangedFor(nameof(ReuseLastSeedCommand))]
    private bool _useRandomSeed = true;

    /// <summary>Sampling steps. Seeded from the selected model's default whenever the model changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SamplingSummary))]
    private int _steps = 9;

    /// <summary>Classifier-free guidance. Seeded from the selected model's default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SamplingSummary))]
    [NotifyPropertyChangedFor(nameof(GuidanceText))]
    private float _cfg = 1.0f;

    /// <summary>
    /// The guidance slider's label.
    /// </summary>
    /// <remarks>
    /// Formatted here rather than through a XAML <c>StringFormat</c>, which uses the current culture: this
    /// machine is German-locale, so the binding rendered "Guidance: 1,0" beside a header reading "cfg 1.0".
    /// A decimal comma in a numeric readout also reads as a thousands separator. Same rule the canvas
    /// readout and the Image Editor's own value labels already follow.
    /// </remarks>
    public string GuidanceText => string.Format(CultureInfo.InvariantCulture, "Guidance: {0:0.0}", Cfg);

    /// <summary>The sampling algorithm. Only meaningful on a backend that honours it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SamplingSummary))]
    private string _selectedSampler = "euler";

    /// <summary>The noise schedule. Same backend caveat as <see cref="SelectedSampler"/>.</summary>
    [ObservableProperty]
    private string _selectedScheduler = "simple";

    /// <summary>
    /// LoRA rows, each carrying a file and a strength. Bound to the shared <c>MultiLoraPickerControl</c>,
    /// which mutates this collection in place.
    /// </summary>
    public ObservableCollection<LoraPickerItemViewModel> Loras { get; } = [];

    /// <summary>The LoRAs offered to the picker: installed, and filtered to the selected model's base model.</summary>
    public ObservableCollection<AvailableLora> AvailableLoras { get; } = [];

    /// <summary>
    /// Why the LoRA picker has nothing to offer, or null when it does. An empty dropdown is otherwise
    /// indistinguishable from a broken query, an unconfigured library and an incompatible model.
    /// </summary>
    [ObservableProperty]
    private string? _loraUnavailableMessage;

    /// <summary>Sampler names the local backend actually maps; anything else would silently run euler.</summary>
    public IReadOnlyList<string> AvailableSamplers { get; } = StableDiffusionCppBackend.SupportedSamplers;

    /// <summary>Scheduler names the local backend actually maps.</summary>
    public IReadOnlyList<string> AvailableSchedulers { get; } = StableDiffusionCppBackend.SupportedSchedulers;

    /// <summary>
    /// The collapsed sampling section's header summary, e.g. <c>euler · 9 · cfg 1.0</c>, so the values
    /// stay readable without expanding it.
    /// </summary>
    public string SamplingSummary => string.Format(
        CultureInfo.InvariantCulture, "{0} · {1} · cfg {2:0.0}", SelectedSampler, Steps, Cfg);

    /// <summary>
    /// The seed the panel shows. Deliberately a string rather than the raw number: "Random" is a state,
    /// not a value, and showing a stale number beside a random toggle invites the reader to believe it.
    /// </summary>
    public string SeedText => UseRandomSeed
        ? "Random"
        : Seed?.ToString(CultureInfo.InvariantCulture) ?? "Not set";

    /// <summary>
    /// Length of the prompt, in characters. Deliberately not a token count and deliberately uncapped:
    /// a real token count needs the model's own tokenizer, and the familiar 77 limit is CLIP's, which does
    /// not apply to the T5-based models this canvas runs. A made-up number is worse than none.
    /// </summary>
    public string PromptLengthText => $"{PromptText.Length} characters";

    partial void OnPromptTextChanged(string value) => OnPropertyChanged(nameof(PromptLengthText));

    /// <summary>The seed the last finished image actually used, so it can be locked and reused.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReuseLastSeedCommand))]
    private long? _lastUsedSeed;

    // ────────────────────────────── Backend capability gating ──────────────────────────────

    /// <summary>
    /// What the selected backend honours. Before a backend is chosen this answers for the local one,
    /// which is what <c>AvailableBackends[0]</c> selects in every constructor.
    /// </summary>
    private BackendCapabilities SelectedCapabilities =>
        SelectedBackend?.Capabilities ?? StableDiffusionCppBackend.LocalCapabilities;

    /// <summary>True when the selected backend sends the negative prompt to the model.</summary>
    public bool IsNegativePromptSupported => SelectedCapabilities.Supports(BackendFeature.NegativePrompt);

    /// <summary>Why the negative prompt is unavailable on this backend, or null when it works.</summary>
    public string? NegativePromptLimitation => SelectedCapabilities.LimitationFor(BackendFeature.NegativePrompt);

    /// <summary>True when the selected backend honours the sampler and scheduler.</summary>
    public bool IsSamplerSelectionSupported => SelectedCapabilities.Supports(BackendFeature.SamplerSelection);

    /// <summary>Why sampler choice is unavailable on this backend, or null when it works.</summary>
    public string? SamplerSelectionLimitation => SelectedCapabilities.LimitationFor(BackendFeature.SamplerSelection);

    /// <summary>True when the selected backend honours steps and guidance.</summary>
    public bool IsStepsAndGuidanceSupported => SelectedCapabilities.Supports(BackendFeature.StepsAndGuidance);

    /// <summary>Why steps and guidance are unavailable on this backend, or null when they work.</summary>
    public string? StepsAndGuidanceLimitation => SelectedCapabilities.LimitationFor(BackendFeature.StepsAndGuidance);

    /// <summary>True when the selected backend loads LoRAs.</summary>
    public bool IsLoraSupported => SelectedCapabilities.Supports(BackendFeature.Loras);

    /// <summary>
    /// Why the disabled control-layer button is disabled. Region D has not built the feature at all, and
    /// the selected backend may separately be unable to run it; both are true, so both are said.
    /// </summary>
    /// <remarks>
    /// This is a composed sentence rather than a raw <see cref="BackendFeature.ControlNet"/> limitation
    /// because the limitation is null on a backend that <i>could</i> do it, and a disabled control with an
    /// empty tooltip is the exact failure the capability surface exists to prevent.
    /// </remarks>
    public string ControlNetTooltip => Compose(
        "Control layers arrive with the layer stack (issue #518 region D).",
        SelectedCapabilities.LimitationFor(BackendFeature.ControlNet));

    /// <summary>
    /// What the mask button does, plus the selected backend's inpainting limitation if it has one. Both
    /// shipped backends honour a mask (#595), so the limitation is there for a future backend that does not.
    /// </summary>
    public string MaskTooltip => Compose(
        "Add the inpaint mask layer, or select it. Paint where the next image to image run may repaint; the rest of the box is kept.",
        SelectedCapabilities.LimitationFor(BackendFeature.Inpainting));

    private static string Compose(string primary, string? backendLimit) =>
        string.IsNullOrWhiteSpace(backendLimit) ? primary : $"{primary} {backendLimit}";

    /// <summary>Why LoRAs are unavailable on this backend, or null when they work.</summary>
    public string? LoraLimitation => SelectedCapabilities.LimitationFor(BackendFeature.Loras);

    /// <summary>
    /// What Cancel can and cannot stop on this backend, shown on the Cancel button. The engine interrupts
    /// mid-sample; the local backend finishes the image it is on.
    /// </summary>
    public string CancelTooltip => SelectedCapabilities.LimitationFor(BackendFeature.MidSampleInterrupt)
        ?? "Stops the batch and interrupts the image being sampled.";

    private void RaiseCapabilityProjections()
    {
        OnPropertyChanged(nameof(IsNegativePromptSupported));
        OnPropertyChanged(nameof(NegativePromptLimitation));
        OnPropertyChanged(nameof(IsSamplerSelectionSupported));
        OnPropertyChanged(nameof(SamplerSelectionLimitation));
        OnPropertyChanged(nameof(IsStepsAndGuidanceSupported));
        OnPropertyChanged(nameof(StepsAndGuidanceLimitation));
        OnPropertyChanged(nameof(IsLoraSupported));
        OnPropertyChanged(nameof(LoraLimitation));
        OnPropertyChanged(nameof(CancelTooltip));
        OnPropertyChanged(nameof(ControlNetTooltip));
        OnPropertyChanged(nameof(MaskTooltip));
    }

    // ────────────────────────────── Seed commands ──────────────────────────────

    /// <summary>Rolls a new fixed seed and locks it, so the next run is reproducible.</summary>
    [RelayCommand]
    private void RandomizeSeed()
    {
        Seed = Random.Shared.NextInt64(0, int.MaxValue);
        UseRandomSeed = false;
        EmitInfo($"Seed locked to {Seed}.");
    }

    /// <summary>Locks the seed that produced the last finished image, to vary a prompt against it.</summary>
    [RelayCommand(CanExecute = nameof(CanReuseLastSeed))]
    private void ReuseLastSeed()
    {
        if (LastUsedSeed is not { } seed)
            return;

        Seed = seed;
        UseRandomSeed = false;
        EmitInfo($"Reusing seed {seed} from the last result.");
    }

    private bool CanReuseLastSeed() => LastUsedSeed is not null;

    /// <summary>Local models discovered under the configured model roots (Diffusion Nexus core).</summary>
    public ObservableCollection<CanvasModelOption> AvailableModels { get; } = [];

    /// <summary>The model the Generate command will load and run.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private CanvasModelOption? _selectedModel;

    /// <summary>Backends the canvas can generate with, each carrying what it honours.</summary>
    public ObservableCollection<CanvasBackendOption> AvailableBackends { get; } =
    [
        new(CanvasBackendKeys.Local, "Diffusion Nexus Core (local)", StableDiffusionCppBackend.LocalCapabilities),
        new(CanvasBackendKeys.Engine, "Diffusion Nexus Engine (ComfyUI)", ManagedComfyUiBackend.EngineCapabilities)
    ];

    /// <summary>
    /// The selected backend. In-memory only for now — the canvas is still behind its switch, so
    /// there is nothing worth persisting yet.
    /// </summary>
    [ObservableProperty]
    private CanvasBackendOption? _selectedBackend;

    /// <summary>
    /// Switches <see cref="AvailableModels"/> to match the newly selected backend.
    ///
    /// <see cref="AvailableModels"/> is otherwise populated only by <see cref="LoadModelsAsync"/>,
    /// which reads the LOCAL backend's disk-scanned catalog. Without this hook, picking the engine
    /// backend would leave <see cref="SelectedModel"/> pointing at a local-only key (e.g.
    /// "flux2-klein") that the engine's <c>Catalog.TryGet</c> can never resolve — Generate would
    /// fail with "files were not found" despite the engine being fully installed and ready.
    /// </summary>
    partial void OnSelectedBackendChanged(CanvasBackendOption? value)
    {
        // Every panel control gates on the new backend, and the LoRA list may become unavailable.
        RaiseCapabilityProjections();

        if (value?.Key == CanvasBackendKeys.Engine && _engineBackend is not null)
        {
            AvailableModels.Clear();
            foreach (var descriptor in _engineBackend.Catalog.ListAvailable())
                AvailableModels.Add(new CanvasModelOption(descriptor.Key, descriptor.DisplayName, descriptor));
            SelectedModel = AvailableModels.FirstOrDefault();
        }
        else if (value?.Key == CanvasBackendKeys.Local)
        {
            _ = LoadModelsAsync();
        }

        _ = LoadLorasForSelectedModelAsync();
    }

    /// <summary>
    /// Adopts the newly selected model's own sampling defaults and reloads the LoRA list for its base model.
    /// </summary>
    /// <remarks>
    /// Seeding the values matters for correctness, not just convenience. The panel sends whatever it shows,
    /// so leaving the previous model's numbers in place would silently override the new model's defaults —
    /// FLUX.2-klein wants 20 steps and Qwen wants 4, and running one on the other's count produces a bad
    /// image with no indication why. Before region B the request left these null and the backend applied
    /// its own defaults; the panel now has to reproduce that faithfully.
    /// </remarks>
    partial void OnSelectedModelChanged(CanvasModelOption? value)
    {
        if (value?.Descriptor is { } descriptor)
        {
            Steps = descriptor.DefaultSteps;
            Cfg = descriptor.DefaultCfg;
            SelectedSampler = descriptor.DefaultSampler;
            SelectedScheduler = descriptor.DefaultScheduler;
        }

        _ = LoadLorasForSelectedModelAsync();
    }

    #region v2 Placeholder commands (wired to disabled UI controls)

    // TODO(v2-controlnet): open the ControlNet add dialog (image picker + preprocessor + strength).
    [RelayCommand(CanExecute = nameof(AlwaysFalse))]
    private void AddControlNet() { /* placeholder */ }

    // TODO(v2-undo): a shallow undo stack over layer operations (issue #518, deliberately not unbounded).
    [RelayCommand(CanExecute = nameof(AlwaysFalse))]
    private void Undo() { /* placeholder */ }

    // TODO(v2-undo): real redo via a Command stack.
    [RelayCommand(CanExecute = nameof(AlwaysFalse))]
    private void Redo() { /* placeholder */ }

    private static bool AlwaysFalse() => false;

    #endregion

    // ────────────────────────────── Inpaint mask (#595) ──────────────────────────────

    /// <summary>Smallest brush, in world pixels.</summary>
    public const double MinBrushSize = CanvasBrush.MinSize;

    /// <summary>Largest brush, in world pixels.</summary>
    public const double MaxBrushSize = CanvasBrush.MaxSize;

    /// <summary>Creates the inpaint mask layer, or selects it, and shows the layer panel so its row is in view.</summary>
    [RelayCommand]
    private void AddMask()
    {
        Layers.AddMask();
        IsLayerPanelVisible = true;
    }

    /// <summary>What a left drag on the canvas does. Only a visible, selected mask can be painted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrushActive), nameof(IsEraserActive))]
    private CanvasPaintTool _paintTool;

    partial void OnPaintToolChanged(CanvasPaintTool value) => EmitInfo(value switch
    {
        CanvasPaintTool.Brush => string.Create(CultureInfo.InvariantCulture, $"Mask brush on ({BrushSize:0} px)."),
        CanvasPaintTool.Eraser => string.Create(CultureInfo.InvariantCulture, $"Mask eraser on ({BrushSize:0} px)."),
        _ => "Paint tool off: a left drag moves the box again.",
    });

    /// <summary>The Brush toggle. Turning it on while the mask cannot be painted does nothing.</summary>
    public bool IsBrushActive
    {
        get => PaintTool == CanvasPaintTool.Brush;
        set => SetPaintTool(CanvasPaintTool.Brush, value);
    }

    /// <summary>The Eraser toggle.</summary>
    public bool IsEraserActive
    {
        get => PaintTool == CanvasPaintTool.Eraser;
        set => SetPaintTool(CanvasPaintTool.Eraser, value);
    }

    private void SetPaintTool(CanvasPaintTool tool, bool on)
    {
        if (on && CanPaint)
            PaintTool = tool;
        else if (!on && PaintTool == tool)
            PaintTool = CanvasPaintTool.None;

        // A toggle the user clicked while painting is impossible has already flipped itself on screen.
        OnPropertyChanged(nameof(IsBrushActive));
        OnPropertyChanged(nameof(IsEraserActive));
    }

    /// <summary>True while the mask is selected and visible: painting on a hidden mask would be painting blind.</summary>
    public bool CanPaint => Layers.SelectedMask is { IsVisible: true };

    /// <summary>The Brush toggle's tooltip: what it does, or why it cannot.</summary>
    public string BrushTooltip => CanPaint
        ? "Paint where to repaint. [ and ] change the size; Escape puts the brush down."
        : PaintBlockedReason;

    /// <summary>The Eraser toggle's tooltip.</summary>
    public string EraserTooltip => CanPaint
        ? "Erase painted mask. [ and ] change the size; Escape puts the eraser down."
        : PaintBlockedReason;

    private string PaintBlockedReason => Layers.SelectedMask is { IsVisible: false }
        ? "The mask is hidden. Show it in the layer panel to paint on it."
        : "Select the inpaint mask in the layer panel to paint on it. + Mask creates it.";

    private double _brushSize = 64;

    /// <summary>Brush diameter in world pixels (the generated image's pixels), <see cref="MinBrushSize"/> to <see cref="MaxBrushSize"/>.</summary>
    public double BrushSize
    {
        get => _brushSize;
        set
        {
            var clamped = double.IsNaN(value) ? _brushSize : CanvasBrush.Clamp(value);
            if (SetProperty(ref _brushSize, clamped))
                OnPropertyChanged(nameof(BrushSizeText));
        }
    }

    /// <summary>Brush size as display text, formatted invariantly.</summary>
    public string BrushSizeText => string.Create(CultureInfo.InvariantCulture, $"{BrushSize:0} px");

    /// <summary>Steps the brush size, the <c>[</c> / <c>]</c> keys.</summary>
    public void StepBrushSize(bool grow) => BrushSize = CanvasBrush.Step(BrushSize, grow);

    /// <summary>
    /// Re-reads whether the mask can be painted, and puts the tool down when it no longer can: another
    /// layer was selected, or the mask was hidden or deleted.
    /// </summary>
    private void RefreshPaintAvailability()
    {
        OnPropertyChanged(nameof(CanPaint));
        OnPropertyChanged(nameof(BrushTooltip));
        OnPropertyChanged(nameof(EraserTooltip));

        if (!CanPaint && PaintTool != CanvasPaintTool.None)
            PaintTool = CanvasPaintTool.None;
    }

    // Design-time ctor: no backend. MUST stay parameterless — CanvasBackendSelectionTests
    // constructs the view model this way, so a required parameter here breaks the test project's build.
    public DiffusionCanvasViewModel()
        : this(provider: null, engine: null, logger: null, catalog: null, monitor: null)
    {
    }

    /// <summary>
    /// The one place every constructor goes through, so the layer stack and the delete command are built
    /// once. Named parameters differ from the public constructor's, which keeps the chained calls
    /// unambiguous.
    /// </summary>
    private DiffusionCanvasViewModel(
        LocalDiffusionBackendProvider? provider,
        IDiffusionBackend? engine,
        IUnifiedLogger? logger,
        ILoraCatalog? catalog,
        ResourceMonitorViewModel? monitor)
    {
        _backendProvider = provider;
        ResourceMonitor = monitor;
        _engineBackend = engine;
        _unifiedLogger = logger;
        _loraCatalog = catalog;
        AdoptEngineCapabilities();
        _selectedBackend = AvailableBackends[0];
        Layers = new CanvasLayerStackViewModel(Frames, EmitInfo);
        Queue = new CanvasGenerationQueue(EmitInfo);
        Queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CanvasGenerationQueue.IsBusy))
                IsGenerating = Queue.IsBusy;
        };
        // A discarded slot is an image the queue no longer has to make: its count and ETA follow, and a
        // waiting batch with no slot left leaves the queue.
        Staging.Candidates.CollectionChanged += (_, _) =>
        {
            Queue.SlotsChanged();
            StopAPreflightWithNothingLeftToMake();
        };
        DeleteFrameCommand = new RelayCommand<GenerationFrameViewModel?>(DeleteFrame, CanvasLayerStackViewModel.CanDelete);
        WireCanvasEvents();
    }

    /// <summary>GPU/RAM monitor widget shown in the canvas toolbar (null at design time).</summary>
    public ResourceMonitorViewModel? ResourceMonitor { get; }

    public DiffusionCanvasViewModel(
        LocalDiffusionBackendProvider backendProvider,
        ResourceMonitorViewModel? resourceMonitor = null,
        IDiffusionBackend? engineBackend = null,
        IUnifiedLogger? unifiedLogger = null,
        ILoraCatalog? loraCatalog = null)
        : this(
            provider: backendProvider ?? throw new ArgumentNullException(nameof(backendProvider)),
            engine: engineBackend,
            logger: unifiedLogger,
            catalog: loraCatalog,
            monitor: resourceMonitor)
    {
        // Populate the model dropdown in the background. Uses a lightweight catalog built directly
        // from the resolved model roots, so it does NOT load the native CUDA library at startup —
        // that happens only on the first Generate.
        _ = LoadModelsAsync();
    }

    /// <summary>
    /// Test seam: an engine-only view model with no local backend provider.
    /// </summary>
    /// <remarks>
    /// The production constructor takes <c>LocalDiffusionBackendProvider</c>, which is <c>public sealed</c>
    /// with non-virtual methods over an <c>IServiceProvider</c> — it cannot be mocked, and constructing a
    /// real one starts a background model scan that would race the test. Reached through
    /// <c>InternalsVisibleTo("DiffusionNexus.Tests")</c>.
    /// </remarks>
    internal DiffusionCanvasViewModel(IDiffusionBackend engineBackend, IUnifiedLogger? unifiedLogger = null)
        : this(
            provider: null,
            engine: engineBackend ?? throw new ArgumentNullException(nameof(engineBackend)),
            logger: unifiedLogger,
            catalog: null,
            monitor: null)
    {
        // Assigning through the property runs OnSelectedBackendChanged, which fills AvailableModels
        // from the engine's own catalog — the same path the toolbar takes.
        SelectedBackend = AvailableBackends.First(b => b.Key == CanvasBackendKeys.Engine);
    }

    /// <summary>
    /// Replaces the engine option's capability set with the injected backend's own.
    /// </summary>
    /// <remarks>
    /// The options are built from statics so the panel can gate the moment a backend is picked, without
    /// constructing anything. Where a real engine instance exists, its own answer is the authoritative one
    /// — and taking it from the instance is what lets a test drive the gating with a fake.
    /// </remarks>
    private void AdoptEngineCapabilities()
    {
        if (_engineBackend is null)
            return;

        for (var i = 0; i < AvailableBackends.Count; i++)
        {
            if (AvailableBackends[i].Key != CanvasBackendKeys.Engine)
                continue;

            AvailableBackends[i] = AvailableBackends[i] with { Capabilities = _engineBackend.Capabilities };
            return;
        }
    }

    private void WireCanvasEvents()
    {
        Box.Changed += (_, _) => RefreshRegionMode();
        // The stack raises LayersChanged for collection changes as well as visibility, opacity and lock,
        // so the readout and the removal commands follow both.
        Layers.LayersChanged += (_, _) =>
        {
            RefreshRegionMode();
            RefreshPaintAvailability();
            ClearCanvasCommand.NotifyCanExecuteChanged();
            DeleteFrameCommand.NotifyCanExecuteChanged();
        };
        Layers.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CanvasLayerStackViewModel.SelectedLayer))
                RefreshPaintAvailability();
        };
        Staging.CandidateAccepted += OnCandidateAccepted;
        RefreshRegionMode();
    }

    /// <summary>
    /// Emits to both Serilog (file) and the in-app Unified Console. The standing repo rule is that every
    /// step of a feature's flow is traced, so a hang shows the last successful step.
    /// </summary>
    private void EmitInfo(string message)
    {
        Logger.Information("DiffusionCanvas: {Message}", message);
        _unifiedLogger?.Info(LogCategory.General, LogSource, message);
    }

    private void EmitWarning(string message, string? detail = null)
    {
        Logger.Warning("DiffusionCanvas: {Message} {Detail}", message, detail);
        _unifiedLogger?.Warn(LogCategory.General, LogSource, message, detail);
    }

    private void EmitError(string message, Exception? ex = null)
    {
        Logger.Error(ex, "DiffusionCanvas: {Message}", message);
        _unifiedLogger?.Error(LogCategory.General, LogSource, message, ex);
    }

    // ────────────────────────────── Region under the box ──────────────────────────────

    /// <summary>
    /// Recomputes whether the box currently overlaps any accepted result that can feed an image-to-image
    /// run. Cheap — rectangle intersection and a null check, no pixel work and no disk access — so it can
    /// run on every box move.
    /// </summary>
    /// <remarks>
    /// Only rasters that <see cref="CanContribute"/> count. A frame whose save failed is still drawn on
    /// the canvas but has nothing the compositor can read back, so counting it would promise image-to-image
    /// in the readout for a run that executes as text-to-image. Whether the file is still readable is not
    /// checked here (that is a disk hit per move); <see cref="GenerateAsync"/> refuses the run instead if
    /// the composite comes back degraded.
    /// </remarks>
    private void RefreshRegionMode()
    {
        var region = Box.WorldRect;
        var overlapping = Frames.Count(f => CanContribute(f) && f.WorldRect.Intersects(region));
        var mask = EvaluateMask(Layers.Mask, region, overlapping);

        IsRegionOccupied = overlapping > 0;
        IsInpaintRun = mask == MaskUse.Applies;

        var results = overlapping == 1 ? "1 result" : $"{overlapping} results";
        RegionModeBadge = IsInpaintRun ? "Inpaint" : overlapping > 0 ? "Image to image" : "Text to image";
        RegionModeText = (overlapping, mask) switch
        {
            (_, MaskUse.Applies) => Layers.Mask!.Invert
                ? $"Inpaint — the box is over {results}; everything but the painting is repainted"
                : $"Inpaint — the mask meets the box over {results}; only the painting is repainted",
            (0, MaskUse.NothingToKeep) =>
                "Text to image — the box is over empty canvas (the mask needs an image under the box to keep)",
            (0, _) => "Text to image — the box is over empty canvas",
            (_, MaskUse.Hidden) => $"Image to image — the box is over {results} (the mask is hidden)",
            (_, MaskUse.Empty) => $"Image to image — the box is over {results} (nothing is painted on the mask)",
            (_, MaskUse.OutsideTheBox) => $"Image to image — the box is over {results} (the mask is outside the box)",
            _ => $"Image to image — the box is over {results}",
        };
    }

    /// <summary>Whether, and why not, the mask takes part in the next run.</summary>
    private enum MaskUse
    {
        NoMask,
        Hidden,
        Empty,
        NothingToKeep,
        OutsideTheBox,
        Applies,
    }

    /// <summary>
    /// The one rule for whether the mask takes part, shared by the readout and Generate: visible,
    /// painted, over pixels to keep, and meeting the box. An inverted mask always meets it: everything
    /// in the box except the painting is repainted. Cheap: bounds only, no rasterising. The painted bounds
    /// are grown by the feather's reach, the same reach the rasteriser draws with, so a stroke just
    /// outside the box that feathers into it counts.
    /// </summary>
    private static MaskUse EvaluateMask(InpaintMaskLayerViewModel? mask, Rect region, int overlapping)
    {
        if (mask is null)
            return MaskUse.NoMask;
        if (!mask.IsVisible)
            return MaskUse.Hidden;
        if (!mask.HasStrokes || (!mask.Invert && mask.PaintedBounds is null))
            return MaskUse.Empty;
        if (overlapping == 0)
            return MaskUse.NothingToKeep;
        if (!mask.Invert)
        {
            var reach = CanvasMaskRasterizer.FeatherReachOf(mask.Feather);
            if (!mask.PaintedBounds!.Value.Inflate(reach).Intersects(region))
                return MaskUse.OutsideTheBox;
        }

        return MaskUse.Applies;
    }

    /// <summary>The mask as Generate takes it: frozen for the whole batch, like the panel.</summary>
    private sealed record MaskSnapshot(
        string Name, IReadOnlyList<CanvasMaskStroke> Strokes, double Feather, bool Invert, double Denoise);

    /// <summary>
    /// True when the raster is shown (visible, not fully transparent) and has a saved file the compositor
    /// could read the region back from. The same rule decides the readout and what Generate composites.
    /// </summary>
    private static bool CanContribute(ICanvasRaster raster) =>
        CanvasRegionCompositor.IsShown(raster) && !string.IsNullOrWhiteSpace(raster.ImagePath);

    /// <summary>
    /// Right-click on a result → Delete. The surface opens the flyout and passes the raster under the
    /// pointer as the parameter. A locked layer is refused; the flyout item is disabled for it.
    /// </summary>
    public IRelayCommand<GenerationFrameViewModel?> DeleteFrameCommand { get; }

    private void DeleteFrame(GenerationFrameViewModel? frame)
    {
        // The stack detaches before disposing and traces the outcome, including a refused locked layer.
        Layers.Delete(frame);
    }

    /// <summary>Removes every unlocked layer from the canvas, releasing their bitmaps. Locked layers stay.</summary>
    [RelayCommand(CanExecute = nameof(CanClearCanvas))]
    private void ClearCanvas()
    {
        var (removed, kept) = Layers.ClearUnlocked();
        EmitInfo(kept == 0
            ? $"Cleared the canvas ({removed} result(s) removed)."
            : $"Cleared the canvas ({removed} result(s) removed, {kept} locked layer(s) kept).");
    }

    private bool CanClearCanvas() => Layers.HasUnlockedLayers;

    /// <summary>
    /// Unloads the resident diffusion model, freeing its VRAM. The next Generate reloads on demand.
    /// (Switching models already auto-unloads the previous one; this is a manual "free VRAM" action.)
    /// </summary>
    [RelayCommand]
    private async Task UnloadModelAsync()
    {
        if (_backendProvider is null)
            return;

        try
        {
            StatusText = "Unloading model…";
            await _backendProvider.UnloadAllAsync().ConfigureAwait(true);
            StatusText = "Model unloaded — VRAM freed.";
            EmitInfo("Unloaded the resident model; VRAM freed.");
            ResourceMonitor?.RefreshCommand.Execute(null);
        }
        catch (Exception ex)
        {
            EmitError($"Failed to unload the diffusion model: {ex.Message}", ex);
            StatusText = $"Unload failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Discovers the local models available under the Diffusion Nexus core model roots and fills
    /// <see cref="AvailableModels"/>. Prefers FLUX.2-klein as the default selection when present.
    /// </summary>
    public async Task LoadModelsAsync()
    {
        if (_backendProvider is null)
            return;

        try
        {
            var roots = await _backendProvider.GetComfyUiModelsRootsAsync().ConfigureAwait(false);
            if (roots.Count == 0)
            {
                PostToUi(() =>
                {
                    if (SelectedBackend?.Key != CanvasBackendKeys.Local)
                        return;

                    AvailableModels.Clear();
                    SelectedModel = null;
                    BackendUnavailableMessage =
                        "No model roots found. The local backend reuses a ComfyUI-layout models folder " +
                        "(register a ComfyUI installation, or point its extra_model_paths.yaml at your models).";
                });
                return;
            }

            // ComfyUiModelCatalog only scans disk — no native init needed just to list models.
            var models = new ComfyUiModelCatalog(roots).ListAvailable();

            PostToUi(() =>
            {
                // This scan is fire-and-forget and takes seconds on a large model tree. By the time it
                // lands the user may have switched to the engine backend, whose catalog is already in
                // AvailableModels — overwriting it would leave SelectedModel pointing at a local-only key
                // the engine can never resolve, and Generate would then fail with "files were not found"
                // against a perfectly healthy engine.
                if (SelectedBackend?.Key != CanvasBackendKeys.Local)
                    return;

                AvailableModels.Clear();
                foreach (var descriptor in models)
                    AvailableModels.Add(new CanvasModelOption(descriptor.Key, $"Local ({descriptor.DisplayName})", descriptor));

                SelectedModel = AvailableModels.FirstOrDefault(m => m.Key == ModelKeys.Flux2Klein)
                    ?? AvailableModels.FirstOrDefault();

                if (AvailableModels.Count == 0)
                {
                    BackendUnavailableMessage =
                        "No runnable models were found under the model roots. Install a pipeline's models from " +
                        "Installer Manager → Diffusion Nexus Core → Workloads.";
                }
                else
                {
                    BackendUnavailableMessage = null;
                }
            });
        }
        catch (Exception ex)
        {
            EmitError($"Failed to load the canvas model list: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Refills <see cref="AvailableLoras"/> for the selected model, or explains why it cannot.
    /// </summary>
    /// <remarks>
    /// Never calls the catalog with a null or empty filter. <c>ILoraCatalog</c> reads null as "return
    /// everything", which on a real library means thousands of rows each decoding a thumbnail out of a
    /// database BLOB — the hazard the pipeline view models already document. When no compatible labels are
    /// known the honest answer is an empty list plus a sentence, not the whole library.
    /// </remarks>
    private async Task LoadLorasForSelectedModelAsync()
    {
        var generation = Interlocked.Increment(ref _loraLoadGeneration);

        void Publish(IReadOnlyList<AvailableLora> loras, string? unavailable)
        {
            PostToUi(() =>
            {
                // A newer selection already started loading; its answer wins.
                if (Volatile.Read(ref _loraLoadGeneration) != generation)
                    return;

                RepublishAvailableLoras(loras);
                LoraUnavailableMessage = unavailable;
            });
        }

        void Explain(string? unavailable)
        {
            // Says why without touching the item source, so the rows keep their picks.
            PostToUi(() =>
            {
                if (Volatile.Read(ref _loraLoadGeneration) != generation)
                    return;

                LoraUnavailableMessage = unavailable;
            });
        }

        if (!IsLoraSupported)
        {
            // Deliberately Explain, not Publish, and deliberately checked before the catalog: the picker is
            // merely disabled on this backend, and emptying its item source would drive every row's
            // ComboBox selection to null — silently throwing the user's chosen LoRAs away on a backend
            // switch, and taking with them the "these will not be applied" warning Generate should raise.
            // Whether a catalog happens to be available cannot change that answer.
            Explain(LoraLimitation);
            return;
        }

        if (_loraCatalog is null)
        {
            Publish([], null);
            return;
        }

        var modelKey = SelectedModel?.Key;
        if (string.IsNullOrWhiteSpace(modelKey))
        {
            Publish([], "Select a model to see its compatible LoRAs.");
            return;
        }

        var labels = ModelBaseModelLabels.ForModelKey(modelKey);
        if (labels is null)
        {
            Publish([], $"No LoRA compatibility is recorded for '{SelectedModel?.DisplayName ?? modelKey}' yet.");
            EmitWarning($"No base-model labels are mapped for model '{modelKey}', so its LoRA list stays empty.");
            return;
        }

        if (labels.Count == 0)
        {
            Publish([], $"{SelectedModel?.DisplayName ?? modelKey} has no published LoRA base model, so none can be matched.");
            return;
        }

        try
        {
            var loras = await _loraCatalog.GetInstalledLorasAsync(labels).ConfigureAwait(false);
            EmitInfo($"Found {loras.Count} LoRA(s) for {SelectedModel?.DisplayName ?? modelKey}.");
            Publish(
                loras,
                loras.Count == 0
                    ? $"No installed LoRA matches {string.Join(", ", labels)}."
                    : null);
        }
        catch (Exception ex)
        {
            // The catalog swallows its own failures and returns an empty list, so reaching here means
            // something further out broke. Either way the user must not read a failure as an empty library.
            EmitError($"Failed to load the LoRA list: {ex.Message}", ex);
            Publish([], "The LoRA list could not be loaded — see the Unified Console.");
        }
    }

    /// <summary>
    /// Swaps the picker's item source while preserving each row's chosen LoRA.
    /// </summary>
    /// <remarks>
    /// The rows' ComboBoxes are bound to this collection, and clearing an <c>ObservableCollection</c> that
    /// is a <c>ComboBox.ItemsSource</c> drives <c>SelectedItem</c> to null. Refilling it therefore wiped
    /// every pick with no message, and a pick lost this way took its file path with it. Rows whose LoRA
    /// survives the new filter are re-selected by path; rows whose LoRA does not are genuinely
    /// incompatible with the newly selected model, so they are reported rather than silently emptied.
    /// </remarks>
    private void RepublishAvailableLoras(IReadOnlyList<AvailableLora> loras)
    {
        var previouslyPicked = Loras.Select(row => row.FilePath).ToList();

        AvailableLoras.Clear();
        foreach (var lora in loras)
            AvailableLoras.Add(lora);

        var dropped = 0;
        for (var i = 0; i < Loras.Count && i < previouslyPicked.Count; i++)
        {
            var path = previouslyPicked[i];
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var match = AvailableLoras.FirstOrDefault(
                l => string.Equals(l.FilePath, path, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                Loras[i].SelectedLora = match;
            else
                dropped++;
        }

        if (dropped > 0)
        {
            EmitWarning(
                $"{dropped} selected LoRA(s) are not published for {SelectedModel?.DisplayName ?? "this model"} " +
                "and were cleared from the picker.");
        }
    }

    /// <summary>
    /// Turns the picker's rows into backend references: enabled rows with a resolved file, deduplicated by
    /// path, and never re-adding a LoRA the model already applies by default.
    /// </summary>
    /// <remarks>
    /// The descriptor's own <c>DefaultLoras</c> are stacked by the backend before per-request ones (Qwen's
    /// mandatory 4-step Lightning LoRA arrives that way), so a user who picks the same file by hand would
    /// otherwise apply it twice at double strength.
    /// </remarks>
    private IReadOnlyList<LoraReference> ResolveLoraReferences(ModelDescriptor? descriptor)
    {
        if (Loras.Count == 0)
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var already in descriptor?.DefaultLoras ?? [])
        {
            if (!string.IsNullOrWhiteSpace(already.FilePath))
                seen.Add(already.FilePath);
        }

        var resolved = new List<LoraReference>();
        foreach (var row in Loras)
        {
            if (!row.IsEnabled || string.IsNullOrWhiteSpace(row.FilePath))
                continue;
            if (!seen.Add(row.FilePath))
            {
                EmitInfo($"Skipping '{row.DisplayName}' — the model already applies it.");
                continue;
            }

            resolved.Add(new LoraReference(row.FilePath, (float)row.Strength));
        }

        return resolved;
    }

    private static void PostToUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    // ────────────────────────────── Generate ──────────────────────────────

    /// <summary>The generation queue: Generate adds a batch, the status bar reads its count, pace and ETA (#598).</summary>
    public CanvasGenerationQueue Queue { get; }

    /// <summary>Session-wide batch counter, for the queue list and the console.</summary>
    private int _batchNumber;

    /// <summary>
    /// Everything one Generate press needs to run later, frozen on the UI thread at the press. The user
    /// keeps working while batches wait, so a queued batch must not read a live control, the live box or
    /// the live layers when its turn comes: it would run something other than what was on screen when it
    /// was asked for.
    /// </summary>
    private sealed record PendingBatch(
        string? BackendKey,
        bool LorasSupported,
        string? LoraLimitation,
        string ModelKey,
        string ModelDisplayName,
        BatchSettings Settings,
        IReadOnlyList<ICanvasRaster> Rasters,
        MaskSnapshot? Mask,
        Rect Region,
        int Width,
        int Height,
        double Denoise,
        IReadOnlyList<StagedCandidateViewModel> Candidates);

    /// <summary>
    /// Enqueues a batch for the current bounding box. The returned task completes when that batch is
    /// over, so awaiting the command still means "this batch has run".
    /// </summary>
    /// <remarks>
    /// Concurrent executions are allowed on purpose: the command's task lives as long as its batch, and
    /// the toolkit would otherwise disable Generate for exactly that long, which is the blocking this
    /// queue removes. Batches still run <b>sequentially</b> (see <see cref="CanvasGenerationQueue"/>).
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanGenerate), AllowConcurrentExecutions = true)]
    private Task GenerateAsync()
    {
        if (string.IsNullOrWhiteSpace(PromptText))
        {
            StatusText = "Please enter a prompt before generating.";
            return Task.CompletedTask;
        }

        if (SelectedModel is not { } model || string.IsNullOrEmpty(model.Key))
        {
            StatusText = "Select a model before generating.";
            return Task.CompletedTask;
        }

        EmitInfo($"Generate requested: batch={BatchCount}, box={Describe(Box.WorldRect)}.");

        // Adopt the model's alignment before freezing the region: the backend's own ValidateRequest
        // throws lazily, on the first MoveNextAsync inside the caller's await foreach, which is long after
        // the candidate slots already exist. Read the value back from the box rather than reusing the
        // descriptor's raw field — the box sanitises it, and a catalog entry with alignment 0 would
        // otherwise divide by zero on the very next line. Done here, at the press, so a queued batch
        // never re-snaps the box under the user later.
        if (model.Descriptor is { } selected)
        {
            Box.Alignment = selected.DimensionAlignment;
            var alignment = Box.Alignment;
            if (Box.Width % alignment != 0 || Box.Height % alignment != 0)
            {
                StatusText = $"The box must be a multiple of {alignment} px for {selected.DisplayName}.";
                EmitWarning($"Refused to generate: box {Box.Width}x{Box.Height} is not aligned to {alignment}.");
                return Task.CompletedTask;
            }
        }

        var region = Box.WorldRect;

        // Snapshot the whole panel once for the whole batch. The user is free to keep editing while a
        // batch runs or waits, and reading the controls per candidate would let a half-typed change land
        // on image three of four — a batch that is not one batch.
        var settings = CaptureBatchSettings(model.Descriptor);

        // Snapshot the rasters on the UI thread; the batch composites them off it when its turn comes.
        // Frames is an ObservableCollection, so it must not be enumerated off the UI thread, and the
        // snapshot copies visibility and opacity, so hiding a layer or dragging its opacity afterwards
        // cannot change this batch's input.
        var rasters = Frames.Select(f => (ICanvasRaster)CanvasRasterSnapshot.Of(f)).ToArray();

        // The mask is frozen on the same terms. Its strokes list is replaced on every change, never
        // mutated, so holding the reference is a snapshot.
        var overlapping = rasters.Count(r => CanContribute(r) && r.WorldRect.Intersects(region));
        var maskSnapshot = Layers.Mask is { } liveMask && EvaluateMask(liveMask, region, overlapping) == MaskUse.Applies
            ? new MaskSnapshot(liveMask.Name, liveMask.Strokes, liveMask.Feather, liveMask.Invert, liveMask.Denoise)
            : null;

        // The slots appear at the press, so the strip shows the queue's shape. A batch that starts at
        // once takes the selection, as Generate always did; one that waits must not move it.
        var candidates = Staging.AddBatch(BatchCount, region, select: !Queue.IsBusy);
        foreach (var candidate in candidates)
            candidate.Prompt = settings.Prompt;

        var number = ++_batchNumber;
        EmitInfo($"Staged {candidates.Count} candidate slot(s) for batch #{number}.");

        var batch = new PendingBatch(
            SelectedBackend?.Key,
            IsLoraSupported,
            LoraLimitation,
            model.Key,
            model.DisplayName,
            settings,
            rasters,
            maskSnapshot,
            region,
            Box.Width,
            Box.Height,
            DenoiseStrength,
            candidates);

        // The pace depends on these three; a batch that differs in any of them starts with no known pace.
        var paceKey = $"{batch.BackendKey}|{batch.ModelKey}|{batch.Width}x{batch.Height}";
        var item = new CanvasQueuedBatchViewModel(number, candidates.Count, settings.Steps, settings.Prompt, paceKey);

        return Queue.Enqueue(item, new CanvasBatchWork(
            Run: token => RunQueuedBatchAsync(item, batch, token),
            OnDropped: () => OnQueuedBatchDropped(item, batch),
            OnCancelling: () => OnBatchCancelling(batch),
            // Counted from the slots, not from the batch's size: a slot the user discarded will not run.
            // The slot's own flag is cheaper to ask than searching the strip: this runs for every batch
            // on every sampling step. Not IsDisposed, which is still false while the strip announces
            // the removal.
            PendingImages: () => batch.Candidates.Count(
                c => c.State == StagedCandidateState.Pending && !c.IsDiscarded)));
    }

    /// <summary>
    /// Runs one queued batch from its frozen inputs: resolves the backend, composites the region, rasterises
    /// the mask, then generates each candidate. Called by <see cref="Queue"/>, one batch at a time.
    /// </summary>
    /// <remarks>
    /// The token is the queue's epoch for this batch and exists before the first await here. Pre-flight
    /// is the longest part of a cold engine run — EnsureRunningAsync spawns python and polls readiness
    /// for up to two minutes — and Cancel is live for all of it.
    /// </remarks>
    private async Task RunQueuedBatchAsync(CanvasQueuedBatchViewModel item, PendingBatch batch, CancellationToken token)
    {
        string? regionImagePath = null;
        string? maskImagePath = null;
        CanvasMaskCompositor? keptPixels = null;

        // False until the candidates start running. A batch refused or failed before that has produced
        // nothing, so its slots leave the strip with it.
        var reachedTheBackend = false;
        _preflightBatch = batch;

        try
        {
            StatusText = "Resolving backend…";
            BackendUnavailableMessage = null;

            var backend = await ResolveBackendAsync(batch.BackendKey, token).ConfigureAwait(true);
            if (backend is null)
                return;

            EmitInfo($"Backend resolved: {backend.DisplayName}.");

            var descriptor = ResolveDescriptor(backend, batch.ModelKey, batch.ModelDisplayName);
            if (descriptor is null)
                return;

            EmitInfo($"Model resolved: {descriptor.DisplayName} (alignment {descriptor.DimensionAlignment}).");

            var region = batch.Region;
            var width = batch.Width;
            var height = batch.Height;
            var settings = batch.Settings;

            // The box was snapped to the selected model's lattice at the press. The backend's own
            // descriptor is the authority, though, and if it disagrees the request would only fail later,
            // per candidate, inside the backend.
            var alignment = Math.Max(1, descriptor.DimensionAlignment);
            if (width % alignment != 0 || height % alignment != 0)
            {
                StatusText = $"The box must be a multiple of {alignment} px for {descriptor.DisplayName}.";
                EmitWarning($"Refused batch #{item.Number}: box {width}x{height} is not aligned to {alignment}.");
                return;
            }

            if (settings.Loras.Count > 0 && !batch.LorasSupported)
            {
                // The picker is disabled on this backend and says why, but rows picked before switching
                // survive in the list. Dropping them silently would be exactly the failure the capability
                // gating exists to prevent.
                EmitWarning(
                    $"{settings.Loras.Count} selected LoRA(s) will not be applied: {batch.LoraLimitation}");
            }

            // Composite off the UI thread. The region work decodes a PNG per overlapping result, walks
            // every output pixel to measure coverage, re-encodes at quality 100 and writes a file —
            // seconds of frozen window at 2048x2048 over several results.
            var composed = await Task
                .Run(() => BuildRegionInitImage(batch.Rasters, region, width, height), token)
                .ConfigureAwait(true);

            regionImagePath = composed.Path;
            token.ThrowIfCancellationRequested();

            if (composed.Degraded)
            {
                // The readout promised image-to-image (it counts rasters with a saved path), but the
                // pixels could not be read back. Running anyway would silently execute as text-to-image
                // with the user's denoise setting meaning nothing — refuse instead and say why.
                StatusText = "The result(s) under the box could not be read back as an image-to-image input " +
                             "(see the Unified Console). Move the box or clear the canvas.";
                EmitWarning("Refused to generate: the region under the box is occupied but yielded no usable pixels.");
                return;
            }

            var strength = batch.Denoise;
            if (batch.Mask is { } maskSnapshot)
            {
                // A snapshot exists only over results (EvaluateMask), and a region over results that did
                // not composite returned above as degraded, so the region path is set here.
                var regionPath = regionImagePath!;
                CanvasMaskEmptyReason emptyReason;
                (maskImagePath, keptPixels, emptyReason) = await Task
                    .Run(() => BuildRegionMask(maskSnapshot, regionPath, region, width, height), token)
                    .ConfigureAwait(true);

                token.ThrowIfCancellationRequested();

                if (maskImagePath is null)
                {
                    // The readout promised inpaint from the strokes' bounds, but the mask itself asks for no
                    // repaint. Running unmasked would repaint the whole box the user meant to protect, and
                    // running on what is left would change nothing. Each cause gets its own way out, judged
                    // by the rasteriser from the paint near the box.
                    var (status, warning) = emptyReason switch
                    {
                        CanvasMaskEmptyReason.InvertedCoversAll => (
                            "The inverted mask leaves nothing to repaint: the painting, once feathered, covers the whole box, and the painting is what is kept. " +
                            "Paint less, lower the feather, or turn Invert off.",
                            "Refused to generate: the inverted mask covers the whole box, so nothing would be repainted."),
                        CanvasMaskEmptyReason.Erased => (
                            "The mask has nothing painted near the box: everything there was erased. " +
                            "Paint what to repaint, or hide the mask to run image to image.",
                            "Refused to generate: the mask is erased empty near the box."),
                        CanvasMaskEmptyReason.TooFaint => (
                            "Inside the box the feathered painting is too faint to change anything. " +
                            "Paint closer to or inside the box, use a bigger brush, or lower the feather.",
                            "Refused to generate: after the feather the mask is too faint inside the box."),
                        _ => (
                            "The painting does not reach the box. Paint inside the box or move the box onto it, " +
                            "or hide the mask to run image to image.",
                            "Refused to generate: no painted stroke reaches the box."),
                    };

                    StatusText = status;
                    EmitWarning(warning);
                    return;
                }

                strength = maskSnapshot.Denoise;
                if (batch.BackendKey == CanvasBackendKeys.Local && strength > StableDiffusionCppBackend.MaxMaskedStrength)
                {
                    EmitInfo(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Diffusion Nexus Core runs a masked image at denoise {StableDiffusionCppBackend.MaxMaskedStrength:0.00} at most: at 1.00 it does not encode the image to keep."));
                }
            }
            if (regionImagePath is not null && maskImagePath is null)
                EmitInfo(string.Create(CultureInfo.InvariantCulture, $"Running image to image at denoise {strength:0.00}."));

            var initImage = regionImagePath is null
                ? null
                : new DiffusionReferenceImage(regionImagePath, (float)strength);
            var maskImage = maskImagePath is null ? null : new DiffusionReferenceImage(maskImagePath);

            reachedTheBackend = true;
            _preflightBatch = null;
            await RunBatchAsync(item, backend, descriptor, batch.Candidates, settings, initImage, maskImage, keptPixels, width, height, composed.Coverage, token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Caught before the generic handler so a user cancel is never reported as a failure. The
            // filter matters: HttpClient signals its own timeout as a TaskCanceledException, and an engine
            // that is alive but wedged would otherwise be reported as "Cancelled." with no diagnostic.
            StatusText = "Cancelled.";
            EmitInfo($"Batch #{item.Number} cancelled by the user.");
            var pruned = Staging.PruneAfterCancel(batch.Candidates);
            if (pruned > 0)
                EmitInfo($"Removed {pruned} cancelled slot(s) from staging.");
        }
        catch (Exception ex)
        {
            EmitError($"Generation failed: {ex.Message}", ex);
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            // Before the slots of a batch that did not run are removed below: that removal is the
            // batch's own, not the user discarding it.
            _preflightBatch = null;
            DeleteScratchFile(regionImagePath);
            DeleteScratchFile(maskImagePath);
            keptPixels?.Dispose();
            Staging.EndFollow();

            if (!reachedTheBackend)
            {
                var removed = Staging.RemoveBatch(batch.Candidates);
                if (removed > 0)
                    EmitInfo($"Batch #{item.Number} did not run; removed its {removed} slot(s) from staging.");
            }

            Staging.RefreshCommands();
        }
    }

    // The engine backend does not need the local provider, so requiring both would disable Generate on
    // an engine-only view model. Not gated on IsGenerating: a press while a batch runs queues another.
    private bool CanGenerate() =>
        SelectedModel is not null && (_backendProvider is not null || _engineBackend is not null);

    /// <summary>
    /// Stops the running batch; its pending candidates are dropped immediately and the next queued batch
    /// starts. Queued batches are removed from the queue list, not here.
    /// </summary>
    /// <remarks>
    /// What this can and cannot interrupt differs per backend, and the tooltip says so: the engine
    /// (ComfyUI) is interrupted mid-sampling, while the local stable-diffusion.cpp backend observes the
    /// token only at phase boundaries — its native <c>GenerateImage</c> call has no cancel hook, so the
    /// image currently sampling finishes and is then discarded.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => Queue.CancelRunning();

    private bool CanCancel() => IsGenerating;

    /// <summary>The running batch while it is still before its first image, otherwise null.</summary>
    private PendingBatch? _preflightBatch;

    /// <summary>
    /// The user discarded every slot of the batch that is still starting up. Nothing is left to make, so
    /// it is stopped here instead of waking the backend (up to two minutes on a cold engine) and
    /// compositing a region for images nobody will see, with later batches waiting behind it. Once the
    /// images run, each discarded slot is skipped at no cost, so only the start-up needs this.
    /// </summary>
    private void StopAPreflightWithNothingLeftToMake()
    {
        if (_preflightBatch is not { } batch || !batch.Candidates.All(c => c.IsDiscarded))
            return;

        _preflightBatch = null;
        EmitInfo("Every slot of the batch that was starting up was discarded; stopping it.");
        Queue.CancelRunning();
    }

    /// <summary>
    /// The running batch is being cancelled, from the Cancel button or from its own row in the queue
    /// list: say so and drop its unfinished slots at once, before the backend has unwound.
    /// </summary>
    private void OnBatchCancelling(PendingBatch batch)
    {
        // First: the pruning below empties the batch's slots, which is not the user discarding them.
        _preflightBatch = null;
        StatusText = "Cancelling…";

        var pruned = Staging.PruneAfterCancel(batch.Candidates);
        if (pruned > 0)
            EmitInfo($"Removed {pruned} cancelled slot(s) from staging.");
    }

    /// <summary>A batch left the queue without running: its slots leave the strip with it.</summary>
    private void OnQueuedBatchDropped(CanvasQueuedBatchViewModel item, PendingBatch batch)
    {
        var removed = Staging.RemoveBatch(batch.Candidates);
        if (removed > 0)
            EmitInfo($"Batch #{item.Number} left the queue; removed its {removed} slot(s) from staging.");
    }

    /// <param name="backendKey">The backend the batch was queued for, not whatever is selected now.</param>
    private async Task<IDiffusionBackend?> ResolveBackendAsync(string? backendKey, CancellationToken token)
    {
        if (backendKey == CanvasBackendKeys.Engine)
        {
            var engine = _engineBackend;
            if (engine is null)
            {
                BackendUnavailableMessage = "The Diffusion Nexus Engine is not available in this session.";
                StatusText = "Backend unavailable";
                return null;
            }

            if (!await engine.IsAvailableAsync(token).ConfigureAwait(true))
            {
                BackendUnavailableMessage = string.Join(" ", engine.MissingRequirements);
                StatusText = "Backend unavailable";
                EmitWarning("The engine backend is not ready.", BackendUnavailableMessage);
                return null;
            }

            return engine;
        }

        if (_backendProvider is null)
        {
            BackendUnavailableMessage = "The local diffusion backend is not available in this session.";
            StatusText = "Backend unavailable";
            return null;
        }

        var local = await _backendProvider.TryGetAsync(token).ConfigureAwait(true);
        if (local is null)
        {
            BackendUnavailableMessage =
                "Cannot locate the models folder. The local backend generates entirely on your GPU (no ComfyUI process), " +
                "but it expects a ComfyUI-layout models folder (DiffusionModels/, TextEncoders/, VAE/). " +
                "Check the Unified Logger for details, or ensure at least one installation is registered as 'ComfyUI' type in the Installer Manager.";
            StatusText = "Backend unavailable";
            EmitWarning("The local backend could not resolve its models root.");
        }

        return local;
    }

    /// <param name="modelKey">The model the batch was queued with, not whatever is selected now.</param>
    private ModelDescriptor? ResolveDescriptor(IDiffusionBackend backend, string modelKey, string modelDisplayName)
    {
        var descriptor = backend.Catalog.TryGet(modelKey);
        if (descriptor is not null)
            return descriptor;

        var roots = _backendProvider?.ResolvedModelsRoots ?? [];
        var rootsText = roots.Count == 0 ? "(unknown)" : string.Join(" | ", roots);
        var searched = (backend.Catalog as ComfyUiModelCatalog)?.SearchedLocationCount ?? 0;
        BackendUnavailableMessage =
            $"'{modelDisplayName}' files were not found under the configured model roots. " +
            $"Searched {searched} location(s) recursively across {roots.Count} root(s): {rootsText}";
        StatusText = "Model unavailable";
        EmitWarning($"Model '{modelKey}' is not resolvable.", BackendUnavailableMessage);
        return null;
    }

    /// <summary>
    /// Composites whatever lies under the box into a temp PNG for the backend to use as an init image,
    /// or returns null when the box is over empty canvas (a plain text2img run).
    /// </summary>
    /// <remarks>
    /// Partial coverage is honest about its limits: the uncovered part is flattened onto neutral grey, and
    /// without an inpaint mask the whole region is denoised, so the known pixels are regenerated rather
    /// than preserved. A mask (#595) confines the repaint; filling the uncovered part automatically
    /// (outpainting) is not done, because the local backend cannot run a masked area at denoise 1.0.
    /// </remarks>
    /// <param name="rasters">
    /// A snapshot of the accepted results, taken on the UI thread. This method runs on the thread pool,
    /// and <see cref="Frames"/> is an <c>ObservableCollection</c> that must not be enumerated off it.
    /// </param>
    /// <returns>
    /// The scratch file path (or null for a text2img run), the opaque coverage fraction, and whether the
    /// result is <b>degraded</b>: a raster the readout counted could not be read back, or the composite
    /// came out empty. A degraded region must not run — it would execute as something other than what
    /// the UI promised.
    /// </returns>
    private (string? Path, double Coverage, bool Degraded) BuildRegionInitImage(
        IReadOnlyList<ICanvasRaster> rasters, Rect region, int width, int height)
    {
        // The same rule the readout applies, so "degraded" means exactly "the readout counted it and it
        // could not be loaded".
        var expected = rasters.Count(r => CanContribute(r) && r.WorldRect.Intersects(region));
        var sources = CanvasRegionCompositor.LoadIntersecting(
            rasters, region, skipped => EmitWarning($"Skipped a raster while compositing the region: {skipped}."));

        try
        {
            if (sources.Count < expected)
            {
                // The skip reason is already in the console. Compositing the rest would hand the backend
                // a region with known pixels missing from it.
                return (null, 0, Degraded: true);
            }

            if (sources.Count == 0)
            {
                EmitInfo("Region is empty — running text to image.");
                return (null, 0, Degraded: false);
            }

            using var composite = CanvasRegionCompositor.Composite(sources, region, width, height);
            var coverage = composite.Coverage;

            if (composite.IsEmpty)
            {
                EmitWarning("Region overlaps results but contains no opaque pixels.");
                return (null, 0, Degraded: true);
            }

            var png = CanvasRegionCompositor.EncodeAsPng(composite.Bitmap, CanvasRegionCompositor.NeutralFill);
            Directory.CreateDirectory(ScratchDirectory);
            File.WriteAllBytes(RegionScratchPath, png);

            var percent = (int)Math.Round(coverage * 100);
            EmitInfo($"Region composited: {percent}% covered.");
            if (!composite.IsFullyCovered)
            {
                EmitWarning(
                    $"The box is only {percent}% over existing pixels. The uncovered area is neutral grey input, and " +
                    "unless an inpaint mask limits the repaint the known pixels are regenerated rather than preserved.");
            }

            return (RegionScratchPath, coverage, Degraded: false);
        }
        finally
        {
            foreach (var source in sources)
                source.Bitmap.Dispose();
        }
    }

    /// <summary>
    /// Rasterises the mask over the box to the mask scratch PNG, and keeps the region and the mask in
    /// memory so each candidate can have the kept pixels put back (<see cref="CanvasMaskCompositor"/>).
    /// Returns a null path, and the rasteriser's reason, when the mask asks for no repaint
    /// (<see cref="CanvasMaskRaster.IsEmpty"/>: judged on the paint within the feather's reach of the box
    /// before the feather, and on the feathered strength inside it). The kept-pixel compositor is
    /// optional: when it cannot be built the run goes ahead and the results come back as the backend
    /// returns them. Runs on the thread pool, from a snapshot.
    /// </summary>
    private (string? Path, CanvasMaskCompositor? Kept, CanvasMaskEmptyReason Reason) BuildRegionMask(
        MaskSnapshot mask, string regionImagePath, Rect region, int width, int height)
    {
        using var raster = CanvasMaskRasterizer.Rasterize(mask.Strokes, region, width, height, mask.Feather, mask.Invert);
        if (raster.IsEmpty)
            return (null, null, raster.EmptyReason);

        Directory.CreateDirectory(ScratchDirectory);
        File.WriteAllBytes(MaskScratchPath, raster.EncodePng());

        EmitInfo(string.Create(
            CultureInfo.InvariantCulture,
            $"Mask '{mask.Name}' rasterised: {raster.RepaintFraction * 100:0.#}% of the box repainted, {mask.Strokes.Count} stroke(s), feather {mask.Feather:0} px{(mask.Invert ? ", inverted" : string.Empty)}, denoise {mask.Denoise:0.00} — running inpaint."));
        // Decoded from the region PNG rather than kept from the composite: the PNG is what the backend
        // received (flattened onto the neutral fill), so it is the "original" the result must match.
        try
        {
            using var original = SKBitmap.Decode(regionImagePath);
            if (original is null || original.Width != width || original.Height != height)
            {
                EmitWarning("The region could not be re-read, so the kept pixels will come back as the backend returns them.");
                return (MaskScratchPath, null, CanvasMaskEmptyReason.None);
            }

            return (MaskScratchPath, new CanvasMaskCompositor(original, raster.Bitmap), CanvasMaskEmptyReason.None);
        }
        catch (Exception ex)
        {
            // Putting the kept pixels back is a refinement; failing to prepare it must not cost the run.
            EmitWarning("The kept pixels cannot be put back for this batch, so they will come back as the backend returns them.", ex.ToString());
            return (MaskScratchPath, null, CanvasMaskEmptyReason.None);
        }
    }

    /// <summary>
    /// The generate panel's values, frozen for the duration of one batch.
    /// </summary>
    /// <param name="Seed">
    /// The base seed, or null for "roll one per image". A batch adds the candidate index to a fixed seed.
    /// </param>
    private sealed record BatchSettings(
        string Prompt,
        string? NegativePrompt,
        int Steps,
        float Cfg,
        string Sampler,
        string Scheduler,
        long? Seed,
        IReadOnlyList<LoraReference> Loras);

    /// <summary>
    /// Freezes the panel for a batch.
    /// </summary>
    /// <remarks>
    /// Steps, guidance, sampler and scheduler are sent explicitly rather than left null. Before region B
    /// they were null so each backend applied the model's own defaults; the panel now shows those defaults
    /// (seeded in <see cref="OnSelectedModelChanged"/>) and therefore has to send them, or what the user
    /// reads and what runs would differ.
    /// </remarks>
    private BatchSettings CaptureBatchSettings(ModelDescriptor? descriptor) => new(
        Prompt: PromptText,
        NegativePrompt: string.IsNullOrWhiteSpace(NegativePromptText) ? null : NegativePromptText,
        Steps: Steps,
        Cfg: Cfg,
        Sampler: SelectedSampler,
        Scheduler: SelectedScheduler,
        Seed: UseRandomSeed ? null : Seed,
        Loras: ResolveLoraReferences(descriptor));

    private async Task RunBatchAsync(
        CanvasQueuedBatchViewModel queued,
        IDiffusionBackend backend,
        ModelDescriptor descriptor,
        IReadOnlyList<StagedCandidateViewModel> candidates,
        BatchSettings settings,
        DiffusionReferenceImage? initImage,
        DiffusionReferenceImage? maskImage,
        CanvasMaskCompositor? keptPixels,
        int width,
        int height,
        double coverage,
        CancellationToken token)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var candidate = candidates[i];

            // The strip stays interactive during a batch, so the user can discard a slot that has not
            // run yet. Re-selecting a disposed candidate would republish its released bitmap as the
            // canvas preview and strand the strip showing a candidate it no longer contains.
            if (candidate.IsDisposed)
            {
                EmitInfo($"Batch #{queued.Number}: skipping candidate {i + 1}/{candidates.Count} — it was discarded before it ran.");
                Queue.ImageSkipped();
                continue;
            }

            // Follow, not assign: the user may have stepped back to compare an earlier candidate, and
            // the next image starting must not pull the selection forward again.
            Staging.Follow(candidate);
            candidate.Prompt = settings.Prompt;
            candidate.State = StagedCandidateState.Loading;
            candidate.StatusText = "Preparing…";
            StatusText = $"Generating {i + 1}/{candidates.Count}…";
            EmitInfo($"Batch #{queued.Number}: starting candidate {i + 1}/{candidates.Count} at {Describe(candidate.WorldRect)}.");
            Queue.ImageStarted();

            var request = new DiffusionRequest
            {
                ModelKey = descriptor.Key,
                Prompt = settings.Prompt,
                Width = width,
                Height = height,
                NegativePrompt = settings.NegativePrompt,
                Steps = settings.Steps,
                Cfg = settings.Cfg,
                Sampler = settings.Sampler,
                Scheduler = settings.Scheduler,
                Loras = settings.Loras,
                // Each image in a batch needs its own seed, or every candidate comes back identical.
                Seed = settings.Seed + i,
                InitImage = initImage,
                MaskImage = maskImage,
            };

            try
            {
                await foreach (var item in backend.GenerateAsync(request, token).ConfigureAwait(true))
                {
                    var delivered = item;
                    if (keptPixels is not null && item.Result is { } result)
                    {
                        // Both backends VAE-decode the whole latent, so the "kept" area comes back slightly
                        // altered. Put the original pixels back outside the mask, blending on its feather.
                        // A failure here (memory, a Skia encode) must not cost a result the GPU already paid
                        // for: it is delivered as the backend returned it, with one warning that says why.
                        byte[]? pasted;
                        string? failure = null;
                        try
                        {
                            pasted = await Task
                                .Run(() => PasteKeptPixels(keptPixels, result.PngBytes), token)
                                .ConfigureAwait(true);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            failure = ex.ToString();
                            pasted = null;
                        }

                        if (pasted is null)
                        {
                            EmitWarning(
                                failure is null
                                    ? $"Candidate {i + 1}: the result is not decodable or not the box's size, so the pixels outside the mask are as the backend returned them."
                                    : $"Candidate {i + 1}: putting the kept pixels back failed, so the pixels outside the mask are as the backend returned them.",
                                failure);
                        }
                        else
                        {
                            delivered = item with { Result = result with { PngBytes = pasted } };
                            EmitInfo($"Candidate {i + 1}: the original pixels outside the mask were put back.");
                        }
                    }

                    ApplyProgress(candidate, delivered);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Filtered so a backend's own timeout (HttpClient reports it as TaskCanceledException)
                // lands in the failure branch below and the batch carries on, instead of being recorded
                // as a user cancel that aborts everything.
                candidate.State = StagedCandidateState.Cancelled;
                candidate.StatusText = "Cancelled";
                Queue.ImageFinished(succeeded: false);
                throw;
            }
            catch (Exception ex)
            {
                candidate.State = StagedCandidateState.Failed;
                candidate.StatusText = ex.Message;
                EmitError($"Candidate {i + 1} failed: {ex.Message}", ex);
            }

            // Only a finished image feeds the measured pace: a failure usually dies early and would make
            // the ETA optimistic.
            Queue.ImageFinished(candidate.IsReady);
            Staging.RefreshCommands();
        }

        var ready = candidates.Count(c => c.IsReady);
        StatusText = ready == candidates.Count
            ? $"{ready} candidate(s) ready — Enter accepts, Del discards."
            : $"{ready}/{candidates.Count} candidate(s) ready — see the Unified Console for the rest.";
        EmitInfo($"Batch #{queued.Number} finished: {ready}/{candidates.Count} ready (region coverage {(int)Math.Round(coverage * 100)}%).");
    }

    private void ApplyProgress(StagedCandidateViewModel candidate, DiffusionStreamItem item)
    {
        // Always marshal to the UI thread — the backend producer runs on a Task.Run thread.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyProgress(candidate, item));
            return;
        }

        // Checked after the marshal, because the discard can land while this hop is queued. Writing a
        // freshly decoded bitmap into a disposed, already-detached candidate would leak it: nothing
        // holds that candidate any more, so nothing will ever dispose it again.
        if (candidate.IsDisposed)
            return;

        switch (item.Progress.Phase)
        {
            case DiffusionPhase.Loading:
                candidate.State = StagedCandidateState.Loading;
                candidate.StatusText = item.Progress.Message ?? "Loading…";
                StatusText = candidate.StatusText;
                break;

            case DiffusionPhase.Encoding:
                candidate.State = StagedCandidateState.Loading;
                candidate.StatusText = item.Progress.Message ?? "Encoding the prompt…";
                StatusText = candidate.StatusText;
                break;

            case DiffusionPhase.Sampling:
                candidate.State = StagedCandidateState.Sampling;
                candidate.StepCurrent = item.Progress.Step;
                candidate.StepTotal = item.Progress.TotalSteps;
                candidate.StatusText = $"Sampling {item.Progress.Step}/{item.Progress.TotalSteps}";
                StatusText = candidate.StatusText;
                // The local backend computes the step rate; the status bar's it/s and ETA read it here.
                Queue.ReportStep(item.Progress.Step, item.Progress.TotalSteps, item.Progress.IterationsPerSecond);
                break;

            case DiffusionPhase.Decoding:
                candidate.State = StagedCandidateState.Sampling;
                candidate.StatusText = item.Progress.Message ?? "Decoding…";
                StatusText = candidate.StatusText;
                break;

            case DiffusionPhase.Completed:
                ApplyCompleted(candidate, item);
                break;
        }
    }

    private void ApplyCompleted(StagedCandidateViewModel candidate, DiffusionStreamItem item)
    {
        if (item.Result is not { } result)
        {
            // Error path — the backend reports failure as a Completed message with no result.
            if (!string.IsNullOrEmpty(item.Progress.Message))
            {
                candidate.State = StagedCandidateState.Failed;
                candidate.StatusText = item.Progress.Message!;
                StatusText = candidate.StatusText;
                EmitWarning($"Candidate {candidate.Ordinal} returned no image.", item.Progress.Message);
            }

            return;
        }

        candidate.Seed = result.Seed;
        candidate.PngBytes = result.PngBytes;

        // The backend reports the seed it actually used, including the one it rolled itself. Keeping it is
        // what makes "I liked that one, vary the prompt" possible after a random run.
        LastUsedSeed = result.Seed;

        var bitmap = TryDecode(result.PngBytes);
        if (bitmap is null)
        {
            candidate.State = StagedCandidateState.Failed;
            candidate.StatusText = "Image decode failed (see the Unified Console).";
            StatusText = candidate.StatusText;
            return;
        }

        candidate.Image = bitmap;
        candidate.State = StagedCandidateState.Ready;
        candidate.StatusText = $"Ready in {result.Duration.TotalSeconds.ToString("N1", CultureInfo.InvariantCulture)}s";
        StatusText = candidate.StatusText;
        EmitInfo($"Candidate {candidate.Ordinal} ready: {result.Width}x{result.Height}, seed {result.Seed}.");
        Staging.RefreshCommands();
    }

    // ────────────────────────────── Accept ──────────────────────────────

    /// <summary>
    /// Turns an accepted candidate into a raster on the canvas. Only accepted results are written to
    /// disk: discarded candidates never reach the outputs folder, which is what keeps the Generation
    /// Gallery a record of work kept rather than of every attempt.
    /// </summary>
    private void OnCandidateAccepted(object? sender, StagedCandidateViewModel candidate)
    {
        string? path = null;
        try
        {
            if (candidate.PngBytes is { Length: > 0 })
                path = OutputsWriter(candidate.PngBytes, candidate.Seed);
        }
        catch (Exception ex)
        {
            EmitError($"Failed to save the accepted result: {ex.Message}", ex);
        }

        // Transfer bitmap ownership to the frame rather than disposing it — the candidate has already
        // been detached from the strip by the time this runs.
        var image = candidate.Image;
        candidate.Image = null;

        var frame = new GenerationFrameViewModel
        {
            CanvasX = candidate.WorldRect.X,
            CanvasY = candidate.WorldRect.Y,
            Width = (int)Math.Round(candidate.WorldRect.Width),
            Height = (int)Math.Round(candidate.WorldRect.Height),
            // The candidate's own snapshot, not the prompt box: the user may have typed the next idea
            // while judging this one, and the frame's prompt is its provenance.
            Prompt = candidate.Prompt,
            Seed = candidate.Seed,
            FrameImage = image,
            ImagePath = path,
            State = GenerationFrameState.Completed,
            StatusText = candidate.StatusText,
        };

        Layers.AddAccepted(frame);
        EmitInfo($"Accepted candidate {candidate.Ordinal} onto the canvas at {Describe(candidate.WorldRect)}"
                 + (path is null ? " (not saved — no PNG bytes)." : $"; saved to {path}."));
    }

    /// <summary>
    /// Writes an accepted result to disk and returns its path.
    /// </summary>
    /// <remarks>
    /// Overridable as a test seam, alongside <see cref="BitmapDecoder"/>: accepting a candidate otherwise
    /// writes real PNGs into the test project's build output on every run. Reached through
    /// <c>InternalsVisibleTo("DiffusionNexus.Tests")</c>.
    /// </remarks>
    internal Func<byte[], long?, string> OutputsWriter { get; set; } = SaveToOutputs;

    /// <summary>
    /// Puts the kept pixels back into one masked result (<see cref="CanvasMaskCompositor.KeepUnmasked"/>).
    /// A test seam like <see cref="OutputsWriter"/>, so a failing paste can be simulated.
    /// </summary>
    internal Func<CanvasMaskCompositor, byte[], byte[]?> PasteKeptPixels { get; set; } =
        static (compositor, png) => compositor.KeepUnmasked(png);

    /// <summary>
    /// Writes an accepted result to the outputs folder the Generation Gallery scans, never overwriting an
    /// existing file — two results in the same second with the same seed would otherwise collide.
    /// </summary>
    private static string SaveToOutputs(byte[] pngBytes, long? seed)
    {
        Directory.CreateDirectory(OutputsFolderRegistrar.OutputsDirectory);
        var stem = $"{DateTime.Now:yyyyMMdd-HHmmss}-{seed?.ToString(CultureInfo.InvariantCulture) ?? "noseed"}";
        var path = Path.Combine(OutputsFolderRegistrar.OutputsDirectory, $"{stem}.png");

        for (var attempt = 2; File.Exists(path) && attempt < 1000; attempt++)
            path = Path.Combine(OutputsFolderRegistrar.OutputsDirectory, $"{stem}-{attempt}.png");

        File.WriteAllBytes(path, pngBytes);
        return path;
    }

    /// <summary>
    /// Turns the backend's PNG bytes into a bitmap for the strip and the canvas.
    /// </summary>
    /// <remarks>
    /// Overridable as a test seam: <c>DiffusionNexus.Tests</c> initialises no Avalonia platform, so a real
    /// <see cref="Bitmap"/> cannot be constructed there and every candidate would otherwise land in the
    /// Failed state. Reached through <c>InternalsVisibleTo("DiffusionNexus.Tests")</c>.
    /// </remarks>
    internal Func<byte[], Bitmap?> BitmapDecoder { get; set; } = static bytes =>
    {
        using var stream = new MemoryStream(bytes);
        return new Bitmap(stream);
    };

    private Bitmap? TryDecode(byte[]? pngBytes)
    {
        if (pngBytes is not { Length: > 0 })
        {
            EmitWarning("The backend returned an empty PNG byte array.");
            return null;
        }

        try
        {
            return BitmapDecoder(pngBytes);
        }
        catch (Exception ex)
        {
            EmitError($"Failed to decode the generated image ({pngBytes.Length} bytes): {ex.Message}", ex);
            return null;
        }
    }

    private void DeleteScratchFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch (IOException) { /* best-effort scratch cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort scratch cleanup */ }
    }

    private static string Describe(Rect rect) => string.Format(
        CultureInfo.InvariantCulture,
        "{0}x{1} @ {2},{3}",
        (int)Math.Round(rect.Width), (int)Math.Round(rect.Height),
        (int)Math.Round(rect.X), (int)Math.Round(rect.Y));

    /// <summary>
    /// Releases the run token and every held bitmap. This view model is a DI singleton that lives for the
    /// whole session, so nothing else will ever tear it down.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Drop what is queued and cancel whatever is in flight. The queue's worker disposes the running
        // batch's token once that batch has unwound — it still holds it and its backend still has
        // callbacks registered on it.
        Queue.Shutdown();

        Staging.DiscardAllCommand.Execute(null);

        foreach (var frame in Frames.ToList())
        {
            Frames.Remove(frame);
            frame.Dispose();
        }

        try
        {
            if (Directory.Exists(ScratchDirectory))
                Directory.Delete(ScratchDirectory, recursive: true);
        }
        catch (IOException) { /* best-effort scratch cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort scratch cleanup */ }

        GC.SuppressFinalize(this);
    }
}
