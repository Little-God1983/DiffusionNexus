using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using DiffusionNexus.UI.ViewModels.Controls;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

/// <summary>
/// Image-to-Image pipeline: edits/transforms input images with <b>FLUX.2-klein</b> reference-image
/// conditioning following the user's text prompt, rendered locally by the DiffusionNexus core. This is
/// the same proven engine path as Anime-To-Real (which works reliably), with two differences: there are
/// <b>no mandatory LoRAs</b> (the LoRA picker is a general, optional loader the user fills in themselves),
/// and up to <b>3 reference images</b> can be supplied. Everything else (dataset / loose-image input,
/// output options, batch, progress, before/after) is inherited from <see cref="PipelineRunViewModel"/>.
/// </summary>
public sealed partial class ImageToImagePipelineRunViewModel : PipelineRunViewModel
{
    // Empty by default — the user supplies the instruction (e.g. "make it a watercolor painting").
    private const string DefaultPrompt = "";

    // Generation config proven by Anime-To-Real on FLUX.2-klein (a distilled flow model; CFG baked in).
    private const float Cfg = 1.0f;
    private const string Sampler = "euler";

    // Fixed seed for test renders so adjusting the prompt/steps is directly comparable.
    private const long TestSeed = 12345L;

    /// <summary>Sampling steps (bound to a 4–30 slider). FLUX.2-klein is distilled; 8 is the value the
    /// reference Anime-To-Real workflow uses. Raise it for more detail, or lower it with a speed LoRA.</summary>
    [ObservableProperty] private int _steps = 8;

    /// <summary>
    /// Two fixed reference-image slots (distinct from the input-image batch). Each holds one optional
    /// reference that guides the result (FLUX.2 kontext-style conditioning) and is applied to <b>every</b>
    /// input image. Together with the per-item input image (reference #1), that's up to 3 references —
    /// the model's limit. Bound to two <c>SingleImageSlotControl</c>s in the run view.
    /// </summary>
    [ObservableProperty] private string? _referenceImage1Path;

    [ObservableProperty] private string? _referenceImage2Path;

    // FLUX.2-klein base-model labels (raw Civitai strings) the optional-LoRA dropdown is filtered to.
    // Without a filter the picker eagerly loads EVERY installed LoRA (thousands), and only FLUX.2-klein
    // LoRAs are compatible with this model anyway (same filter Anime-To-Real uses).
    private static readonly string[] FluxKleinBaseModels =
    [
        "Flux.2 Klein 9B",
        "Flux.2 Klein 9B-base",
        "Flux.2 Klein 4B",
        "Flux.2 Klein 4B-base",
    ];

    public override string Title => "Image to Image";

    protected override IReadOnlyList<string> LoraBaseModels => FluxKleinBaseModels;

    /// <summary>Reusable output-resolution picker (aspect ratio + orientation + megapixels). Bound by the
    /// run view's <c>OutputResolutionControl</c>; <see cref="ComputeOutputDimensions"/> delegates to it.</summary>
    public OutputResolutionViewModel OutputResolution { get; } = new();

    public ImageToImagePipelineRunViewModel(
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
        if (string.IsNullOrWhiteSpace(Prompt))
            throw new InvalidOperationException("Enter a prompt describing the image you want before generating.");

        // Optional LoRAs the user picked (none are mandatory for this workflow).
        var loras = await ResolveLorasAsync(cancellationToken).ConfigureAwait(true);

        // The input image is reference #1 (and sets the output size), followed by whichever of the two
        // fixed reference slots are filled. Missing slots / files are skipped.
        var referenceImages = new[] { inputPath, ReferenceImage1Path, ReferenceImage2Path }
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Select(p => new DiffusionReferenceImage(p!))
            .ToList();

        var (width, height) = ComputeOutputDimensions(inputPath);

        // FLUX.2-klein reference conditioning (the proven Anime-To-Real path): each reference image is
        // VAE-encoded into the positive conditioning (AutoResize avoids the native size-mismatch crash),
        // and the prompt drives the result. Generated on an empty latent (full "denoise 1.0").
        var request = new DiffusionRequest
        {
            ModelKey = ModelKeys.Flux2Klein,
            Prompt = Prompt,
            Width = width,
            Height = height,
            Steps = Steps,
            Cfg = Cfg,
            Sampler = Sampler,
            ReferenceImages = referenceImages,
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

    /// <summary>
    /// Computes the output dimensions for one input by reading its size and delegating to the reusable
    /// <see cref="OutputResolution"/> picker (aspect ratio + orientation + megapixel budget). The
    /// reference images themselves are auto-resized by the backend.
    /// </summary>
    private (int Width, int Height) ComputeOutputDimensions(string path)
    {
        double width = 0, height = 0;
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
            // 0×0 → the picker falls back to square for "same as input".
        }

        return OutputResolution.ComputeDimensions(width, height);
    }
}
