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
using Serilog;
using SkiaSharp;

namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>A selectable local model in the canvas toolbar (e.g. "Local (FLUX.2-klein)").</summary>
public sealed record CanvasModelOption(string Key, string DisplayName);

/// <summary>Stable keys for the canvas backend dropdown.</summary>
public static class CanvasBackendKeys
{
    /// <summary>In-process stable-diffusion.cpp backend (the original canvas engine).</summary>
    public const string Local = "local";

    /// <summary>The app-owned ComfyUI engine.</summary>
    public const string Engine = "engine";
}

/// <summary>A selectable generation backend in the canvas toolbar.</summary>
public sealed record CanvasBackendOption(string Key, string DisplayName);

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

    /// <summary>
    /// Guards <see cref="_runCts"/>. Generate joins the epoch and Cancel cancels-and-nulls it; without a
    /// lock those two can interleave and leave a cancelled token installed for the next batch.
    /// </summary>
    private readonly object _runLock = new();

    /// <summary>
    /// The batch epoch. The invariant, copied from <c>CivitaiDownloadQueue</c>: <b>never cancel without
    /// nulling</b>. Cancelling and leaving the field in place makes the next Generate join a dead token
    /// and abort instantly.
    /// </summary>
    private CancellationTokenSource? _runCts;

    private bool _disposed;

    /// <summary>
    /// File name of the composited region handed to a backend as the init image. Fixed, and deliberately
    /// distinctive — see the remarks on <see cref="_regionScratchPath"/>.
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
    private readonly string _regionScratchPath = Path.Combine(
        Path.GetTempPath(), "DiffusionNexus", $"canvas-{Environment.ProcessId}", RegionScratchFileName);

    /// <summary>All accepted results on the canvas, in z-order (last = top).</summary>
    public ObservableCollection<GenerationFrameViewModel> Frames { get; } = [];

    /// <summary>The generation region — the canvas's entire spatial model.</summary>
    public GenerationBoundingBox Box { get; } = new();

    /// <summary>Candidates awaiting a verdict.</summary>
    public CanvasStagingViewModel Staging { get; } = new();

    /// <summary>The canvas-level prompt.</summary>
    [ObservableProperty]
    private string _promptText = string.Empty;

    /// <summary>
    /// True while a batch is running. Carries <c>NotifyCanExecuteChangedFor</c> deliberately: once
    /// Generate stops awaiting the whole run, the toolkit's own "no concurrent execution" protection on
    /// an async RelayCommand no longer covers a second click, and a second click would clobber the shared
    /// run token and silently break Cancel.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isGenerating;

    /// <summary>Toolbar status text ("Idle", "Loading Z-Image-Turbo…", "Sampling 5/9", "Done", "Error: …").</summary>
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
    private double _denoiseStrength = 0.65;

    /// <summary>
    /// Describes what pressing Generate will actually do, given where the box currently sits — the
    /// difference between text2img and img2img is a drag, so the UI has to say which one it is.
    /// </summary>
    [ObservableProperty]
    private string _regionModeText = "Text to image — the box is over empty canvas";

    /// <summary>True when the box overlaps at least one accepted result, so the denoise control matters.</summary>
    [ObservableProperty]
    private bool _isRegionOccupied;

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

    #endregion

    /// <summary>Local models discovered under the configured model roots (Diffusion Nexus core).</summary>
    public ObservableCollection<CanvasModelOption> AvailableModels { get; } = [];

    /// <summary>The model the Generate command will load and run.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private CanvasModelOption? _selectedModel;

    /// <summary>Backends the canvas can generate with.</summary>
    public ObservableCollection<CanvasBackendOption> AvailableBackends { get; } =
    [
        new(CanvasBackendKeys.Local, "Diffusion Nexus Core (local)"),
        new(CanvasBackendKeys.Engine, "Diffusion Nexus Engine (ComfyUI)")
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
        if (value?.Key == CanvasBackendKeys.Engine && _engineBackend is not null)
        {
            AvailableModels.Clear();
            foreach (var descriptor in _engineBackend.Catalog.ListAvailable())
                AvailableModels.Add(new CanvasModelOption(descriptor.Key, descriptor.DisplayName));
            SelectedModel = AvailableModels.FirstOrDefault();
        }
        else if (value?.Key == CanvasBackendKeys.Local)
        {
            _ = LoadModelsAsync();
        }
    }

    #region v2 Placeholder commands (wired to disabled UI controls)

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

    // TODO(v2-layers): the layer stack (issue #518 region D) replaces this placeholder.
    [RelayCommand(CanExecute = nameof(AlwaysFalse))]
    private void ToggleLayerPanel() { /* placeholder */ }

    // TODO(v2-undo): a shallow undo stack over layer operations (issue #518, deliberately not unbounded).
    [RelayCommand(CanExecute = nameof(AlwaysFalse))]
    private void Undo() { /* placeholder */ }

    // TODO(v2-undo): real redo via a Command stack.
    [RelayCommand(CanExecute = nameof(AlwaysFalse))]
    private void Redo() { /* placeholder */ }

    private static bool AlwaysFalse() => false;

    #endregion

    public DiffusionCanvasViewModel()
    {
        // Design-time ctor: no backend. MUST stay parameterless — CanvasBackendSelectionTests
        // constructs the view model this way, so a required parameter here breaks the test project's build.
        _backendProvider = null;
        _selectedBackend = AvailableBackends[0];
        DeleteFrameCommand = new RelayCommand<GenerationFrameViewModel?>(DeleteFrame);
        WireCanvasEvents();
    }

    /// <summary>GPU/RAM monitor widget shown in the canvas toolbar (null at design time).</summary>
    public ResourceMonitorViewModel? ResourceMonitor { get; }

    public DiffusionCanvasViewModel(
        LocalDiffusionBackendProvider backendProvider,
        ResourceMonitorViewModel? resourceMonitor = null,
        IDiffusionBackend? engineBackend = null,
        IUnifiedLogger? unifiedLogger = null)
    {
        _backendProvider = backendProvider ?? throw new ArgumentNullException(nameof(backendProvider));
        ResourceMonitor = resourceMonitor;
        _engineBackend = engineBackend;
        _unifiedLogger = unifiedLogger;
        _selectedBackend = AvailableBackends[0];
        DeleteFrameCommand = new RelayCommand<GenerationFrameViewModel?>(DeleteFrame);
        WireCanvasEvents();

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
    {
        ArgumentNullException.ThrowIfNull(engineBackend);

        _backendProvider = null;
        _engineBackend = engineBackend;
        _unifiedLogger = unifiedLogger;
        DeleteFrameCommand = new RelayCommand<GenerationFrameViewModel?>(DeleteFrame);
        WireCanvasEvents();

        // Assigning through the property runs OnSelectedBackendChanged, which fills AvailableModels
        // from the engine's own catalog — the same path the toolbar takes.
        SelectedBackend = AvailableBackends.First(b => b.Key == CanvasBackendKeys.Engine);
    }

    private void WireCanvasEvents()
    {
        Box.Changed += (_, _) => RefreshRegionMode();
        Frames.CollectionChanged += (_, _) => RefreshRegionMode();
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

        IsRegionOccupied = overlapping > 0;
        RegionModeText = overlapping switch
        {
            0 => "Text to image — the box is over empty canvas",
            1 => "Image to image — the box is over 1 result",
            _ => $"Image to image — the box is over {overlapping} results",
        };
    }

    /// <summary>True when the raster has a saved file the compositor could read the region back from.</summary>
    private static bool CanContribute(ICanvasRaster raster) => !string.IsNullOrWhiteSpace(raster.ImagePath);

    /// <summary>
    /// Right-click on a result → Delete. The surface opens the flyout and passes the raster under the
    /// pointer as the parameter.
    /// </summary>
    public IRelayCommand<GenerationFrameViewModel?>? DeleteFrameCommand { get; }

    private void DeleteFrame(GenerationFrameViewModel? frame)
    {
        if (frame is null)
            return;

        // Detach before disposing: a bitmap still bound into the visual tree faults the render.
        Frames.Remove(frame);
        frame.Dispose();
        EmitInfo("Removed a result from the canvas.");
    }

    /// <summary>Removes every accepted result from the canvas, releasing their bitmaps.</summary>
    [RelayCommand]
    private void ClearCanvas()
    {
        var count = Frames.Count;
        foreach (var frame in Frames.ToList())
        {
            Frames.Remove(frame);
            frame.Dispose();
        }

        EmitInfo($"Cleared the canvas ({count} result(s) removed).");
    }

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
                    AvailableModels.Add(new CanvasModelOption(descriptor.Key, $"Local ({descriptor.DisplayName})"));

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

    private static void PostToUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    // ────────────────────────────── Generate ──────────────────────────────

    /// <summary>
    /// Enqueues a batch for the current bounding box and runs it.
    /// </summary>
    /// <remarks>
    /// The batch runs <b>sequentially</b> on purpose. <c>DiffusionContextHost</c> holds a per-model
    /// <c>SemaphoreSlim(1,1)</c> with a single-resident policy, so parallel canvas generations would
    /// either serialise behind that lock anyway or thrash VRAM by loading a second model.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (string.IsNullOrWhiteSpace(PromptText))
        {
            StatusText = "Please enter a prompt before generating.";
            return;
        }

        IsGenerating = true;
        StatusText = "Resolving backend…";
        BackendUnavailableMessage = null;
        EmitInfo($"Generate requested: batch={BatchCount}, box={Describe(Box.WorldRect)}.");

        // Open the epoch BEFORE the first await. Cancel goes live the moment IsGenerating flips, and
        // pre-flight is the longest part of a cold engine run — EnsureRunningAsync spawns python and
        // polls readiness for up to two minutes. An epoch created after the resolve left every Cancel in
        // that window a silent no-op, which the next line then papered over with a fresh token.
        var cts = new CancellationTokenSource();
        lock (_runLock)
        {
            _runCts?.Dispose();
            _runCts = cts;
        }

        var token = cts.Token;
        string? regionImagePath = null;

        try
        {
            var backend = await ResolveBackendAsync(token).ConfigureAwait(true);
            if (backend is null)
                return;

            EmitInfo($"Backend resolved: {backend.DisplayName}.");

            var descriptor = ResolveDescriptor(backend);
            if (descriptor is null)
                return;

            EmitInfo($"Model resolved: {descriptor.DisplayName} (alignment {descriptor.DimensionAlignment}).");

            // Adopt the model's alignment before validating: the backend's own ValidateRequest throws
            // lazily, on the first MoveNextAsync inside the caller's await foreach, which is long after
            // the candidate slots already exist. Read the value back from the box rather than reusing the
            // descriptor's raw field — the box sanitises it, and a catalog entry with alignment 0 would
            // otherwise divide by zero on the very next line.
            Box.Alignment = descriptor.DimensionAlignment;
            var alignment = Box.Alignment;
            if (Box.Width % alignment != 0 || Box.Height % alignment != 0)
            {
                StatusText = $"The box must be a multiple of {alignment} px for {descriptor.DisplayName}.";
                EmitWarning($"Refused to generate: box {Box.Width}x{Box.Height} is not aligned to {alignment}.");
                return;
            }

            var region = Box.WorldRect;
            var width = Box.Width;
            var height = Box.Height;

            // Snapshot the prompt once for the whole batch. The user is free to type the next idea while a
            // batch runs, and reading the box per candidate (or on accept) would pick that up.
            var prompt = PromptText;

            // Snapshot the rasters on the UI thread, then composite off it. The region work decodes a
            // PNG per overlapping result, walks every output pixel to measure coverage, re-encodes at
            // quality 100 and writes a file — seconds of frozen window at 2048x2048 over several
            // results. Frames is an ObservableCollection, so it must not be enumerated off the UI thread.
            var rasters = Frames.Cast<ICanvasRaster>().ToArray();
            var composed = await Task
                .Run(() => BuildRegionInitImage(rasters, region, width, height), token)
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

            var initImage = regionImagePath is null
                ? null
                : new DiffusionReferenceImage(regionImagePath, (float)DenoiseStrength);

            var candidates = Staging.BeginBatch(BatchCount, region);
            EmitInfo($"Staged {candidates.Count} candidate slot(s).");

            await RunBatchAsync(backend, descriptor, candidates, prompt, initImage, width, height, composed.Coverage, token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Caught before the generic handler so a user cancel is never reported as a failure. The
            // filter matters: HttpClient signals its own timeout as a TaskCanceledException, and an engine
            // that is alive but wedged would otherwise be reported as "Cancelled." with no diagnostic.
            StatusText = "Cancelled.";
            EmitInfo("Batch cancelled by the user.");
            var pruned = Staging.PruneAfterCancel();
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
            DeleteScratchFile(regionImagePath);
            EndRunEpoch(cts);
            IsGenerating = false;
            Staging.RefreshCommands();
        }
    }

    // The engine backend does not need the local provider, so requiring both would disable Generate on
    // an engine-only view model.
    private bool CanGenerate() =>
        !IsGenerating && SelectedModel is not null && (_backendProvider is not null || _engineBackend is not null);

    /// <summary>
    /// Stops the batch. Pending candidates are dropped immediately.
    /// </summary>
    /// <remarks>
    /// What this can and cannot interrupt differs per backend, and the tooltip says so: the engine
    /// (ComfyUI) is interrupted mid-sampling, while the local stable-diffusion.cpp backend observes the
    /// token only at phase boundaries — its native <c>GenerateImage</c> call has no cancel hook, so the
    /// image currently sampling finishes and is then discarded.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_runLock)
        {
            // Both halves are the invariant — never cancel without nulling. Leaving a cancelled source
            // installed would make the next Generate join a dead epoch and abort instantly.
            cts = _runCts;
            _runCts = null;
        }

        if (cts is null)
            return;

        EmitInfo("Cancel requested — dropping the rest of the batch.");
        StatusText = "Cancelling…";

        try
        {
            // Cancelled but deliberately NOT disposed here: the running batch still holds this token and
            // both backends register callbacks on it, and registering on a disposed source throws. The
            // batch that opened the epoch disposes it in its own finally, once nothing can use it.
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The owning batch finished and disposed it as we cancelled; nothing left to stop.
        }

        var pruned = Staging.PruneAfterCancel();
        if (pruned > 0)
            EmitInfo($"Removed {pruned} cancelled slot(s) from staging.");
    }

    private bool CanCancel() => IsGenerating;

    /// <summary>
    /// Closes the epoch this batch opened: clears the field if it is still ours, then disposes.
    /// </summary>
    /// <remarks>
    /// The reference check matters because <see cref="Cancel"/> nulls the field itself. Without it, a
    /// cancelled batch's teardown could null out an epoch a later batch had already installed.
    /// </remarks>
    private void EndRunEpoch(CancellationTokenSource cts)
    {
        lock (_runLock)
        {
            if (ReferenceEquals(_runCts, cts))
                _runCts = null;
        }

        cts.Dispose();
    }

    private async Task<IDiffusionBackend?> ResolveBackendAsync(CancellationToken token)
    {
        if (SelectedBackend?.Key == CanvasBackendKeys.Engine)
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

    private ModelDescriptor? ResolveDescriptor(IDiffusionBackend backend)
    {
        var modelKey = SelectedModel?.Key;
        if (string.IsNullOrEmpty(modelKey))
        {
            StatusText = "Select a model before generating.";
            return null;
        }

        var descriptor = backend.Catalog.TryGet(modelKey);
        if (descriptor is not null)
            return descriptor;

        var roots = _backendProvider?.ResolvedModelsRoots ?? [];
        var rootsText = roots.Count == 0 ? "(unknown)" : string.Join(" | ", roots);
        var searched = (backend.Catalog as ComfyUiModelCatalog)?.SearchedLocationCount ?? 0;
        BackendUnavailableMessage =
            $"'{SelectedModel?.DisplayName}' files were not found under the configured model roots. " +
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
    /// Partial coverage is honest about its limits: with no <c>MaskImage</c> support in either backend,
    /// the uncovered part is flattened onto neutral grey and the whole region is denoised, so the known
    /// pixels are regenerated rather than preserved. True masked outpainting needs the layer stack
    /// (issue #518 region D).
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
            Directory.CreateDirectory(Path.GetDirectoryName(_regionScratchPath)!);
            File.WriteAllBytes(_regionScratchPath, png);

            var percent = (int)Math.Round(coverage * 100);
            EmitInfo($"Region composited: {percent}% covered, denoise {DenoiseStrength:0.00} — running image to image.");
            if (!composite.IsFullyCovered)
            {
                EmitWarning(
                    $"The box is only {percent}% over existing pixels. Without mask support the uncovered area is " +
                    "neutral grey input, so the known pixels are regenerated rather than preserved.");
            }

            return (_regionScratchPath, coverage, Degraded: false);
        }
        finally
        {
            foreach (var source in sources)
                source.Bitmap.Dispose();
        }
    }

    private async Task RunBatchAsync(
        IDiffusionBackend backend,
        ModelDescriptor descriptor,
        IReadOnlyList<StagedCandidateViewModel> candidates,
        string prompt,
        DiffusionReferenceImage? initImage,
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
                EmitInfo($"Skipping candidate {i + 1}/{candidates.Count} — it was discarded before it ran.");
                continue;
            }

            Staging.Current = candidate;
            candidate.Prompt = prompt;
            candidate.State = StagedCandidateState.Loading;
            candidate.StatusText = "Preparing…";
            StatusText = $"Generating {i + 1}/{candidates.Count}…";
            EmitInfo($"Starting candidate {i + 1}/{candidates.Count} at {Describe(candidate.WorldRect)}.");

            var request = new DiffusionRequest
            {
                ModelKey = descriptor.Key,
                Prompt = prompt,
                Width = width,
                Height = height,
                // v1 leaves Steps/Cfg/Sampler/Scheduler null so the backend uses the model's own defaults.
                // TODO(v2-advanced): pass through Steps / Cfg / SelectedSampler when the panel (region B) ships.
                // TODO(v2-negative-prompt): pass NegativePromptText.
                // Each image in a batch needs its own seed, or every candidate comes back identical.
                Seed = UseRandomSeed ? null : Seed + i,
                InitImage = initImage,
            };

            try
            {
                await foreach (var item in backend.GenerateAsync(request, token).ConfigureAwait(true))
                    ApplyProgress(candidate, item);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Filtered so a backend's own timeout (HttpClient reports it as TaskCanceledException)
                // lands in the failure branch below and the batch carries on, instead of being recorded
                // as a user cancel that aborts everything.
                candidate.State = StagedCandidateState.Cancelled;
                candidate.StatusText = "Cancelled";
                throw;
            }
            catch (Exception ex)
            {
                candidate.State = StagedCandidateState.Failed;
                candidate.StatusText = ex.Message;
                EmitError($"Candidate {i + 1} failed: {ex.Message}", ex);
            }

            Staging.RefreshCommands();
        }

        var ready = candidates.Count(c => c.IsReady);
        StatusText = ready == candidates.Count
            ? $"{ready} candidate(s) ready — Enter accepts, Del discards."
            : $"{ready}/{candidates.Count} candidate(s) ready — see the Unified Console for the rest.";
        EmitInfo($"Batch finished: {ready}/{candidates.Count} ready (region coverage {(int)Math.Round(coverage * 100)}%).");
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

        Frames.Add(frame);
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

        // Cancel whatever is in flight, then let the owning batch's own finally dispose the source —
        // it is still holding the token and its backends still have callbacks registered on it.
        CancellationTokenSource? inFlight;
        lock (_runLock)
        {
            inFlight = _runCts;
            _runCts = null;
        }

        try
        {
            inFlight?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down by the batch that opened it.
        }

        Staging.DiscardAllCommand.Execute(null);

        foreach (var frame in Frames.ToList())
        {
            Frames.Remove(frame);
            frame.Dispose();
        }

        try
        {
            var scratchDirectory = Path.GetDirectoryName(_regionScratchPath);
            if (scratchDirectory is not null && Directory.Exists(scratchDirectory))
                Directory.Delete(scratchDirectory, recursive: true);
        }
        catch (IOException) { /* best-effort scratch cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort scratch cleanup */ }

        GC.SuppressFinalize(this);
    }
}
