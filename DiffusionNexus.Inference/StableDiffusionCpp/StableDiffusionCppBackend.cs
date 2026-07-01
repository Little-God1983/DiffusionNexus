using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.Inference.Models;
using HPPH;
using HPPH.SkiaSharp;
using StableDiffusion.NET;
using SDNet = StableDiffusion.NET;

namespace DiffusionNexus.Inference.StableDiffusionCpp;

/// <summary>
/// <see cref="IDiffusionBackend"/> implementation backed by the SciSharp/DarthAffe
/// <c>StableDiffusion.NET</c> binding to <c>stable-diffusion.cpp</c>.
///
/// Lifetime model (per design decision): contexts are loaded on first use and kept alive
/// until the host disposes the backend. Concurrent requests against the same model
/// serialize on a per-context lock; requests against different models run in parallel.
/// </summary>
public sealed class StableDiffusionCppBackend : IDiffusionBackend, IDisposable
{
    private const string NativeSource = "stable-diffusion.cpp";

    private static readonly Serilog.ILogger Logger = Serilog.Log.ForContext<StableDiffusionCppBackend>();
    private static readonly Serilog.ILogger NativeLog = Serilog.Log.ForContext("SourceContext", NativeSource);

    private readonly DiffusionContextHost _host = new();
    private readonly ComfyUiModelCatalog _catalog;
    private static int _eventsInitialized;

    /// <summary>
    /// Static so the (static) native <c>StableDiffusionCpp.Log</c> handler can route native engine
    /// output to the Unified Console. Set from the ctor; the backend is a singleton in practice.
    /// </summary>
    private static IUnifiedLogger? _unifiedLogger;

    public StableDiffusionCppBackend(string modelsRoot)
        : this(new[] { modelsRoot })
    {
    }

    public StableDiffusionCppBackend(IEnumerable<string> modelsRoots, IUnifiedLogger? unifiedLogger = null)
    {
        _catalog = new ComfyUiModelCatalog(modelsRoots);
        _unifiedLogger = unifiedLogger;
        EnsureNativeEventsInitialized();
    }

    /// <inheritdoc />
    public string DisplayName => "Diffusion Nexus Core";

    /// <summary>The catalog of models discovered under the configured ComfyUI root.</summary>
    public IModelCatalog Catalog => _catalog;

    /// <summary>Keys of the models currently resident in VRAM (empty when nothing is loaded).</summary>
    public IReadOnlyList<string> LoadedModelKeys => _host.LoadedModelKeys;

    /// <summary>Unloads all resident models, freeing the VRAM they hold.</summary>
    public Task UnloadAllAsync(CancellationToken cancellationToken = default) => _host.UnloadAllAsync(cancellationToken);

    /// <inheritdoc />
    public IReadOnlyList<string> MissingRequirements { get; private set; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> Warnings => [];

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // The native library is loaded eagerly via EnsureNativeEventsInitialized() in the ctor,
        // so the only remaining gate is "at least one runnable model is on disk."
        if (_catalog.ListAvailable().Count == 0)
        {
            MissingRequirements = ["No runnable diffusion models were found under the configured models root."];
            return Task.FromResult(false);
        }

        MissingRequirements = [];
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DiffusionStreamItem> GenerateAsync(
        DiffusionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var descriptor = _catalog.TryGet(request.ModelKey)
            ?? throw new InvalidOperationException(
                $"Model '{request.ModelKey}' is not available. Check that the required files exist under the configured models root.");

        ValidateRequest(request, descriptor);

        // Bounded channel: producer (native callbacks) → consumer (this enumerator).
        // Drop-oldest is fine for progress events; we don't want to back-pressure the native sampler.
        var channel = Channel.CreateBounded<DiffusionStreamItem>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        // Phase 0 — yield an initial Loading message so the UI can flip to "loading…" immediately.
        await channel.Writer.WriteAsync(
            new DiffusionStreamItem(new DiffusionProgress
            {
                Phase = DiffusionPhase.Loading,
                Message = $"Preparing {descriptor.DisplayName}…"
            }), cancellationToken).ConfigureAwait(false);

        // Background task drives the native call and writes events into the channel.
        var producer = Task.Run(async () =>
        {
            try
            {
                using var lease = await _host.GetOrLoadAsync(descriptor,
                    msg => TryWrite(channel, new DiffusionProgress { Phase = DiffusionPhase.Loading, Message = msg }),
                    cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                // Hook native progress for THIS generation only — we unsubscribe in finally.
                EventHandler<SDNet.StableDiffusionProgressEventArgs> onProgress = (_, args) =>
                    TryWrite(channel, new DiffusionProgress
                    {
                        Phase = DiffusionPhase.Sampling,
                        Step = args.Step,
                        TotalSteps = args.Steps,
                        IterationsPerSecond = args.IterationsPerSecond,
                    });

                SDNet.StableDiffusionCpp.Progress += onProgress;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var result = RunGeneration(lease.Model, descriptor, request);
                    sw.Stop();

                    // Final item carries the result + a Completed progress marker.
                    await channel.Writer.WriteAsync(new DiffusionStreamItem(
                        new DiffusionProgress { Phase = DiffusionPhase.Completed, Step = result.TotalSteps, TotalSteps = result.TotalSteps },
                        new DiffusionResult(result.PngBytes, result.Width, result.Height, result.Seed, sw.Elapsed)
                    ), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    SDNet.StableDiffusionCpp.Progress -= onProgress;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancellation is honored only at phase boundaries today (TODO(v2-cancel)).
            }
            catch (Exception ex)
            {
                // Log the full exception to Serilog + the Unified Console (the in-frame message is
                // necessarily short). Native engine detail, if any, was already routed via OnNativeLog.
                Logger.Error(ex, "Diffusion generation failed for model {ModelKey}", descriptor.Key);
                _unifiedLogger?.Error(LogCategory.General, "DiffusionNexus.Core",
                    $"Generation failed for '{descriptor.DisplayName}': {ex.GetType().Name}: {ex.Message}", ex);

                // Surface the failure as a final progress message; the consumer sees the error message.
                TryWrite(channel, new DiffusionProgress
                {
                    Phase = DiffusionPhase.Completed,
                    Message = $"Generation failed: {ex.GetType().Name}: {ex.Message}"
                });
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return item;

        // Surface producer-side exceptions that escaped the catch above (very rare path).
        await producer.ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous core: call into native, encode the resulting <see cref="IImage{ColorRGB}"/> to PNG.
    /// Runs on the producer Task — no UI thread risk.
    /// </summary>
    private static GenerationOutcome RunGeneration(SDNet.DiffusionModel model, ModelDescriptor d, DiffusionRequest req)
    {
        var seed = req.Seed ?? Random.Shared.NextInt64();

        // Image-to-image when an init image is supplied (e.g. anime → real), otherwise text-to-image.
        // The init image's Strength is the denoise strength (0 = keep input, 1 = ignore input).
        SDNet.ImageGenerationParameter genParams;
        if (req.InitImage is { } init && !string.IsNullOrWhiteSpace(init.FilePath))
        {
            var initImage = HPPH.SkiaSharp.ImageHelper.LoadImage(init.FilePath);
            genParams = SDNet.ImageGenerationParameter.ImageToImage(req.Prompt, initImage)
                .WithStrength(init.Strength);
        }
        else
        {
            genParams = SDNet.ImageGenerationParameter.TextToImage(req.Prompt);
        }

        genParams = genParams
            .WithSize(req.Width, req.Height)
            .WithSteps(req.Steps ?? d.DefaultSteps)
            .WithCfg(req.Cfg ?? d.DefaultCfg)
            .WithSampler(MapSampler(req.Sampler ?? d.DefaultSampler))
            .WithScheduler(MapScheduler(req.Scheduler ?? d.DefaultScheduler))
            .WithSeed(seed);

        // Flow-matching timestep shift for flow models (e.g. Qwen-Image uses 3.1). Models that don't
        // set DefaultFlowShift keep the engine default, so this is a no-op for Z-Image / FLUX.2-klein.
        if (d.DefaultFlowShift is { } flowShift)
            genParams = genParams.WithFlowShift(flowShift);

        // Tile the VAE at generation time for models with a heavy VAE (Qwen-Image's Wan VAE allocates
        // ~7-8 GB at 1 MP). Without this the VAE encode/decode spike can exceed VRAM on top of the
        // resident model and spill to system RAM (the generation then crawls / appears frozen).
        if (d.TileVae)
            genParams = genParams.WithVaeTiling(true);

        // FLUX.2 reference-image conditioning (kontext / edit). The reference image(s) are VAE-encoded
        // and injected into the conditioning while the latent stays empty — this is the "anime → real"
        // path. AutoResize fixes the size mismatch that otherwise crashes the native generator.
        if (req.ReferenceImages.Count > 0)
        {
            genParams = genParams.WithRefImageAutoResize(req.AutoResizeReferenceImages);
            genParams.RefImages = req.ReferenceImages
                .Where(r => !string.IsNullOrWhiteSpace(r.FilePath))
                .Select(r => HPPH.SkiaSharp.ImageHelper.LoadImage(r.FilePath))
                .ToArray();

            // With more than one reference, increment each reference's position index so the model
            // treats them as distinct images (image1/image2/image3) instead of collapsing them onto the
            // same slot. No effect for a single reference (e.g. the Anime-To-Real path).
            if (genParams.RefImages.Length > 1)
                genParams = genParams.WithRefIndexIncrease(true);
        }

        // LoRAs are applied per-generation (stable-diffusion.cpp loads them at runtime for this
        // call only), so the cached base context is shared across requests with different LoRAs.
        // The descriptor's DefaultLoras (e.g. Qwen-Image-2512's mandatory 4-step Lightning LoRA) are
        // applied first, then any per-request LoRAs stack on top.
        foreach (var lora in d.DefaultLoras.Concat(req.Loras))
        {
            if (string.IsNullOrWhiteSpace(lora.FilePath))
                continue;
            genParams.Loras.Add(new SDNet.Lora(lora.FilePath) { Multiplier = lora.Strength });
        }

        // TODO(v2-negative-prompt): apply req.NegativePrompt via .WithNegativePrompt(...) once enabled.
        // TODO(v2-controlnet):      apply req.ControlNets via .WithControlNet(image, strength).
        // TODO(v2-inpaint):         apply req.MaskImage via .WithMaskImage(...) for inpaint flows.

        var image = model.GenerateImage(genParams)
            ?? throw new InvalidOperationException("Native generator returned a null image.");

        var png = image.ToPng();
        return new GenerationOutcome(png, image.Width, image.Height, seed, req.Steps ?? d.DefaultSteps);
    }

    private static SDNet.Sampler MapSampler(string name) => name.ToLowerInvariant() switch
    {
        "euler" => SDNet.Sampler.Euler,
        "euler_a" or "euler-a" => SDNet.Sampler.Euler_A,
        "heun" => SDNet.Sampler.Heun,
        "dpm2" => SDNet.Sampler.DPM2,
        "dpm++2m" or "dpmpp2m" => SDNet.Sampler.DPMPP2M,
        "dpm++2m_v2" or "dpmpp2mv2" => SDNet.Sampler.DPMPP2Mv2,
        "lcm" => SDNet.Sampler.LCM,
        "ddim" => SDNet.Sampler.DDIM_Trailing,
        _ => SDNet.Sampler.Euler,
    };

    private static SDNet.Scheduler MapScheduler(string name) => name.ToLowerInvariant() switch
    {
        "simple" => SDNet.Scheduler.Simple,
        "karras" => SDNet.Scheduler.Karras,
        "exponential" => SDNet.Scheduler.Exponential,
        "ays" => SDNet.Scheduler.AYS,
        "discrete" => SDNet.Scheduler.Discrete,
        _ => SDNet.Scheduler.Simple,
    };

    private static void ValidateRequest(DiffusionRequest req, ModelDescriptor d)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(req));
        if (req.Width <= 0 || req.Height <= 0)
            throw new ArgumentException("Width and height must be positive.", nameof(req));
        if (req.Width % d.DimensionAlignment != 0 || req.Height % d.DimensionAlignment != 0)
            throw new ArgumentException(
                $"Width and height must be multiples of {d.DimensionAlignment} for {d.DisplayName}.", nameof(req));
    }

    private static void TryWrite(Channel<DiffusionStreamItem> channel, DiffusionProgress progress)
    {
        // Drop-oldest channel: TryWrite never blocks. We don't await — keeps native callback fast.
        channel.Writer.TryWrite(new DiffusionStreamItem(progress));
    }

    private static void EnsureNativeEventsInitialized()
    {
        if (Interlocked.Exchange(ref _eventsInitialized, 1) == 0)
        {
            SDNet.StableDiffusionCpp.InitializeEvents();
            // Route the native engine's own log (the ONLY place that explains *why* a model fails to
            // load, e.g. unsupported architecture / missing tensor / bad quant) to Serilog + the
            // Unified Console. Without this, failures surface only as the generic wrapper exception
            // "Failed to initialize diffusion-model." with no detail.
            SDNet.StableDiffusionCpp.Log += OnNativeLog;
        }
    }

    /// <summary>
    /// Forwards stable-diffusion.cpp's native log lines. Warn/Error go to the Unified Console so the
    /// user can see them; the full firehose (incl. Info/Debug) always goes to the Serilog file.
    /// </summary>
    private static void OnNativeLog(object? sender, SDNet.StableDiffusionLogEventArgs e)
    {
        var text = e.Text?.TrimEnd();
        if (string.IsNullOrEmpty(text))
            return;

        switch (e.Level)
        {
            case SDNet.LogLevel.Error:
                NativeLog.Error("{NativeText}", text);
                _unifiedLogger?.Error(LogCategory.General, NativeSource, text);
                break;
            case SDNet.LogLevel.Warn:
                NativeLog.Warning("{NativeText}", text);
                _unifiedLogger?.Warn(LogCategory.General, NativeSource, text);
                break;
            case SDNet.LogLevel.Info:
                NativeLog.Information("{NativeText}", text);
                break;
            default:
                NativeLog.Debug("{NativeText}", text);
                break;
        }
    }

    public void Dispose() => _host.Dispose();

    private readonly record struct GenerationOutcome(byte[] PngBytes, int Width, int Height, long Seed, int TotalSteps);
}
