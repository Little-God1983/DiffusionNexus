using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.Inference.StableDiffusionCpp;
using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Diffusion;
using DiffusionNexus.UI.Services.Pipelines;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

/// <summary>
/// Anime-To-Real pipeline: img2img with FLUX.2-klein + the two anime-to-real LoRAs at a chosen
/// strength turns an anime image into a photoreal one. Adds only the LoRA-strength knob; everything
/// else (input source, output options, batch, progress) is inherited from <see cref="PipelineRunViewModel"/>.
/// </summary>
public sealed partial class AnimeToRealPipelineRunViewModel : PipelineRunViewModel
{
    // Fixed photoreal default prompt (editable in the UI). Kept simple for v1.
    private const string DefaultPrompt = "photorealistic, realistic skin texture, natural lighting, detailed, high quality";

    // Fixed seed for test renders so adjusting LoRA strength is directly comparable.
    private const long TestSeed = 12345L;

    /// <summary>Multiplier applied to both anime-to-real LoRAs (bound to a slider).</summary>
    [ObservableProperty] private double _loraStrength = 0.85;

    public override string Title => "Anime to Real";

    public AnimeToRealPipelineRunViewModel(
        PipelineManifest manifest,
        IPipelineAssetInstaller installer,
        LocalDiffusionBackendProvider backendProvider,
        IPipelineOutputWriter outputWriter,
        IDatasetState datasetState,
        IDialogService dialogs,
        IUnifiedLogger? unifiedLogger = null)
        : base(manifest, installer, backendProvider, outputWriter, datasetState, dialogs, unifiedLogger,
               defaultPrompt: DefaultPrompt, defaultImageInfluence: 0.6)
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

        var (width, height) = ReadAlignedDimensions(inputPath);

        var request = new DiffusionRequest
        {
            ModelKey = ModelKeys.Flux2Klein,
            Prompt = Prompt,
            Width = width,
            Height = height,
            InitImage = new DiffusionReferenceImage(inputPath, (float)ImageInfluence),
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

    private async Task<List<LoraReference>> ResolveLorasAsync(CancellationToken ct)
    {
        var loras = new List<LoraReference>();
        foreach (var asset in Manifest.Assets.Where(a => a.Kind == PipelineAssetKind.Lora && a.CivitaiModelId.HasValue))
        {
            var path = await Installer.FindLoraPathByModelIdAsync(asset.CivitaiModelId!.Value, ct).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(path))
                loras.Add(new LoraReference(path, (float)LoraStrength));
        }
        return loras;
    }

    /// <summary>
    /// Reads the input image's dimensions and clamps/rounds them to a FLUX.2-klein-valid size
    /// (multiple of 16, within a sane VRAM range). Falls back to 1024² if the size can't be read.
    /// </summary>
    private static (int Width, int Height) ReadAlignedDimensions(string path)
    {
        int width = 1024, height = 1024;
        try
        {
            using var codec = SkiaSharp.SKCodec.Create(path);
            if (codec is not null)
            {
                width = codec.Info.Width;
                height = codec.Info.Height;
            }
        }
        catch
        {
            // keep defaults
        }

        return (Align(width), Align(height));

        static int Align(int value)
        {
            value = Math.Clamp(value, 512, 1536);
            return value - (value % 16);
        }
    }
}
