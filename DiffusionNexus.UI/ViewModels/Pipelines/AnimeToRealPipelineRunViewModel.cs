using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.Inference.StableDiffusionCpp;
using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Diffusion;
using DiffusionNexus.UI.Services.Lora;
using DiffusionNexus.UI.Services.Pipelines;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

/// <summary>
/// Anime-To-Real pipeline: FLUX.2-klein reference-image conditioning + LoRAs turns an anime image into
/// a photoreal one. Supplies the two mandatory anime-to-real LoRAs (via the manifest) and filters the
/// Multi-LoRA Picker to the FLUX.2-klein base models; everything else (input source, output options,
/// batch, progress, LoRA picker) is inherited from <see cref="PipelineRunViewModel"/>.
/// </summary>
public sealed partial class AnimeToRealPipelineRunViewModel : PipelineRunViewModel
{
    // Default prompt (editable in the UI). Mirrors the reference ComfyUI workflow's intent.
    private const string DefaultPrompt = "turn this image into a photorealistic image";

    // Generation config proven against the reference workflow (FLUX.2-klein + the 2 A2R LoRAs).
    private const float Cfg = 1.0f;
    private const string Sampler = "euler";

    // Fixed seed for test renders so adjusting settings is directly comparable.
    private const long TestSeed = 12345L;

    // FLUX.2-klein base-model labels (raw Civitai strings) the LoRA picker is filtered to.
    private static readonly string[] FluxKleinBaseModels =
    [
        "Flux.2 Klein 9B",
        "Flux.2 Klein 9B-base",
        "Flux.2 Klein 4B",
        "Flux.2 Klein 4B-base",
    ];

    /// <summary>Sampling steps (bound to a 4–30 slider). Reference workflow uses 8.</summary>
    [ObservableProperty] private int _steps = 8;

    public override string Title => "Anime to Real";

    protected override IReadOnlyList<string> LoraBaseModels => FluxKleinBaseModels;

    protected override double DefaultLoraStrength => 0.75;

    public AnimeToRealPipelineRunViewModel(
        PipelineManifest manifest,
        IPipelineAssetInstaller installer,
        LocalDiffusionBackendProvider backendProvider,
        IPipelineOutputWriter outputWriter,
        IDatasetState datasetState,
        IDialogService dialogs,
        IDatasetEventAggregator eventAggregator,
        ILoraCatalog loraCatalog,
        IUnifiedLogger? unifiedLogger = null,
        IVideoThumbnailService? videoThumbnailService = null,
        IAppSettingsService? settingsService = null)
        : base(manifest, installer, backendProvider, outputWriter, datasetState, dialogs, eventAggregator,
               loraCatalog, unifiedLogger, videoThumbnailService, settingsService,
               defaultPrompt: DefaultPrompt, defaultImageInfluence: 1.0)
    {
    }

    protected override async Task<byte[]> ProcessOneImageAsync(
        string inputPath, bool isTestRun, IDiffusionBackend backend, CancellationToken cancellationToken)
    {
        var loras = await ResolveLorasAsync(cancellationToken).ConfigureAwait(true);
        if (loras.Count == 0)
        {
            throw new InvalidOperationException(
                "The Anime-To-Real LoRAs aren't on disk. Install them from Installer Manager → " +
                "Diffusion Nexus Core → Workloads → the Pipelines tab.");
        }

        var (width, height) = ComputeOutputDimensions(inputPath);

        // FLUX.2 reference-image conditioning (matches the ComfyUI workflow): the anime image is a
        // VAE-encoded reference injected into the conditioning, generated on an empty latent (full
        // "denoise 1.0"). AutoResize (on by default in the request) avoids size-mismatch crashes.
        var request = new DiffusionRequest
        {
            ModelKey = ModelKeys.Flux2Klein,
            Prompt = Prompt,
            Width = width,
            Height = height,
            Steps = Steps,
            Cfg = Cfg,
            Sampler = Sampler,
            ReferenceImages = [new DiffusionReferenceImage(inputPath)],
            Loras = loras,
            Seed = isTestRun ? TestSeed : null,
        };

        byte[]? png = null;
        await foreach (var item in backend.GenerateAsync(request, cancellationToken).ConfigureAwait(true))
        {
            if (item.Result is { } result)
                png = result.PngBytes;
        }

        return png ?? throw new InvalidOperationException("No image was produced.");
    }

    // Target output budget ≈ 1 megapixel (1024²). Keeps VRAM bounded while preserving aspect ratio.
    private const double TargetPixels = 1024.0 * 1024.0;

    /// <summary>
    /// Computes the output dimensions: the input image's aspect ratio scaled to ≈1 MP, each side
    /// rounded to a multiple of 16 and clamped to a FLUX.2-klein-valid VRAM range. Falls back to
    /// 1024² if the size can't be read. The reference image itself is auto-resized by the backend.
    /// </summary>
    private static (int Width, int Height) ComputeOutputDimensions(string path)
    {
        double width = 1024, height = 1024;
        try
        {
            using var codec = SkiaSharp.SKCodec.Create(path);
            if (codec is not null && codec.Info is { Width: > 0, Height: > 0 } info)
            {
                width = info.Width;
                height = info.Height;
            }
        }
        catch
        {
            // keep 1024² fallback
        }

        var scale = Math.Sqrt(TargetPixels / (width * height));
        return (Align(width * scale), Align(height * scale));

        static int Align(double value)
        {
            var v = (int)Math.Round(Math.Clamp(value, 512, 1536));
            return v - (v % 16);
        }
    }
}
