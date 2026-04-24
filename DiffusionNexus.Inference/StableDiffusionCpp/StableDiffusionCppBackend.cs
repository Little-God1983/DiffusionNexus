using System.Runtime.CompilerServices;
using System.Threading.Channels;
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
    private readonly DiffusionContextHost _host = new();
    private readonly ComfyUiModelCatalog _catalog;
    private static int _eventsInitialized;

    public StableDiffusionCppBackend(string modelsRoot)
    {
        _catalog = new ComfyUiModelCatalog(modelsRoot);
        EnsureNativeEventsInitialized();
    }

    /// <summary>The catalog of models discovered under the configured ComfyUI root.</summary>
    public IModelCatalog Catalog => _catalog;

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

        var genParams = SDNet.ImageGenerationParameter.TextToImage(req.Prompt)
            .WithSize(req.Width, req.Height)
            .WithSteps(req.Steps ?? d.DefaultSteps)
            .WithCfg(req.Cfg ?? d.DefaultCfg)
            .WithSampler(MapSampler(req.Sampler ?? d.DefaultSampler))
            .WithScheduler(MapScheduler(req.Scheduler ?? d.DefaultScheduler))
            .WithSeed(seed);

        // TODO(v2-negative-prompt): apply req.NegativePrompt via .WithNegativePrompt(...) once enabled.
        // TODO(v2-loras):           apply req.Loras via DiffusionModelParameter.WithLora at load time.
        // TODO(v2-controlnet):      apply req.ControlNets via .WithControlNet(image, strength).
        // TODO(v2-img2img):         switch to ImageGenerationParameter.ImageToImage(...) when req.InitImage != null.
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
            SDNet.StableDiffusionCpp.InitializeEvents();
    }

    public void Dispose() => _host.Dispose();

    private readonly record struct GenerationOutcome(byte[] PngBytes, int Width, int Height, long Seed, int TotalSteps);
}
