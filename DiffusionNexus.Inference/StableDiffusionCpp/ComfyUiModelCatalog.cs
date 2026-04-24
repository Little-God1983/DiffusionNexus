using DiffusionNexus.Inference.Models;

namespace DiffusionNexus.Inference.StableDiffusionCpp;

/// <summary>
/// Walks a ComfyUI-layout models root and reports every <see cref="ModelDescriptor"/>
/// the v1 backend can serve. Discovery is purely file-existence-based — no GGUF
/// header inspection, no metadata parsing — to keep startup fast and predictable.
/// </summary>
/// <remarks>
/// Expected ComfyUI subfolders:
/// <list type="bullet">
///   <item><description><c>DiffusionModels/</c> — UNET-only / DiT-only files (Z-Image-Turbo, Flux UNETs, Qwen-Image, …).</description></item>
///   <item><description><c>TextEncoders/</c> — CLIP / T5 / LLM text encoder files.</description></item>
///   <item><description><c>VAE/</c> — autoencoder weights.</description></item>
///   <item><description><c>StableDiffusion/</c> — single-file SDXL/SD1.5 checkpoints (future).</description></item>
/// </list>
/// </remarks>
public sealed class ComfyUiModelCatalog : IDiffusionBackendCatalog
{
    private readonly string _modelsRoot;
    private List<ModelDescriptor>? _cached;

    public ComfyUiModelCatalog(string modelsRoot)
    {
        if (string.IsNullOrWhiteSpace(modelsRoot))
            throw new ArgumentException("Models root is required.", nameof(modelsRoot));
        _modelsRoot = modelsRoot;
    }

    public IReadOnlyList<ModelDescriptor> ListAvailable() => _cached ??= Discover();

    public ModelDescriptor? TryGet(string key) =>
        ListAvailable().FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));

    /// <summary>Forces a fresh disk scan on the next call. Use after the user changes their models folder.</summary>
    public void Invalidate() => _cached = null;

    private List<ModelDescriptor> Discover()
    {
        var found = new List<ModelDescriptor>();

        TryAddZImageTurbo(found);

        // TODO(v2-models): add SDXL checkpoint discovery, Flux UNET discovery, Qwen-Image-Edit (incl. mmproj), etc.

        return found;
    }

    private void TryAddZImageTurbo(List<ModelDescriptor> sink)
    {
        var unet = Path.Combine(_modelsRoot, "DiffusionModels", "z_image_turbo_bf16.safetensors");
        var clip = Path.Combine(_modelsRoot, "TextEncoders", "qwen_3_4b.safetensors");
        var vae = Path.Combine(_modelsRoot, "VAE", "ae.safetensors");

        if (!File.Exists(unet) || !File.Exists(clip) || !File.Exists(vae))
            return;

        sink.Add(new ModelDescriptor
        {
            Key = ModelKeys.ZImageTurbo,
            DisplayName = "Z-Image-Turbo",
            Kind = ModelKind.ZImageTurbo,
            DiffusionModelPath = unet,
            VaePath = vae,
            TextEncoders = new Dictionary<TextEncoderSlot, string>
            {
                [TextEncoderSlot.Llm] = clip,
            },
            DefaultSteps = 9,
            DefaultCfg = 1.0f,
            DefaultSampler = "euler",
            DefaultScheduler = "simple",
            DimensionAlignment = 64,
            DefaultWidth = 1024,
            DefaultHeight = 1024,
        });
    }
}

/// <summary>Stable string identifiers for the models the v1 backend understands.</summary>
public static class ModelKeys
{
    public const string ZImageTurbo = "z-image-turbo";

    // TODO(v2-models): add SDXL/Flux/QwenImageEdit keys here.
}

// Bridge interface so DiffusionContextHost can stay independent of the public IModelCatalog
// while we expose only one concrete catalog today.
internal interface IDiffusionBackendCatalog : Abstractions.IModelCatalog
{
    void Invalidate();
}
