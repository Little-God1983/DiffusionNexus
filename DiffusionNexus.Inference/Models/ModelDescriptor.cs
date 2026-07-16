using DiffusionNexus.Inference.Abstractions;

namespace DiffusionNexus.Inference.Models;

/// <summary>
/// Declarative description of a loadable diffusion model: its architecture family,
/// the on-disk file paths that satisfy each required slot, and per-model defaults.
/// Backends consume this to build the architecture-specific load parameters.
/// </summary>
public sealed class ModelDescriptor
{
    /// <summary>Stable identifier (e.g. "z-image-turbo"). Used as the cache key for loaded contexts.</summary>
    public required string Key { get; init; }

    /// <summary>Human-readable name for UI display (e.g. "Z-Image-Turbo").</summary>
    public required string DisplayName { get; init; }

    /// <summary>Architecture family — drives the loader strategy.</summary>
    public required ModelKind Kind { get; init; }

    /// <summary>
    /// Path to the diffusion / UNET / DiT weights file. For models distributed as a single
    /// "checkpoint" containing UNET + text encoders + VAE, use <see cref="CheckpointPath"/> instead.
    /// </summary>
    public string? DiffusionModelPath { get; init; }

    /// <summary>
    /// Path to a single-file checkpoint that bundles UNET + text encoder(s) + VAE
    /// (e.g. classic SDXL safetensors). Mutually exclusive with <see cref="DiffusionModelPath"/>.
    /// </summary>
    public string? CheckpointPath { get; init; }

    /// <summary>Path to the standalone VAE file. Optional when the checkpoint already contains one.</summary>
    public string? VaePath { get; init; }

    /// <summary>Map of text-encoder slot → file path. Different architectures use different slots.</summary>
    public IReadOnlyDictionary<TextEncoderSlot, string> TextEncoders { get; init; }
        = new Dictionary<TextEncoderSlot, string>();

    /// <summary>
    /// LoRAs that are an intrinsic part of this model and must be applied to <b>every</b> generation
    /// (e.g. Qwen-Image-2512's mandatory 4-step Lightning LoRA, which the model's low step count
    /// depends on). The backend stacks these before any per-request LoRAs. Empty for models that
    /// don't bake in a LoRA.
    /// </summary>
    public IReadOnlyList<LoraReference> DefaultLoras { get; init; } = [];

    /// <summary>
    /// Flow-matching timestep shift applied at generation time for flow models (e.g. 3.1 for
    /// Qwen-Image). <c>null</c> leaves the engine default in place (used by non-flow models and
    /// flow models that don't need an explicit shift).
    /// </summary>
    public float? DefaultFlowShift { get; init; }

    /// <summary>
    /// Tile the VAE encode/decode at generation time. Qwen-Image's Wan-style VAE allocates a very
    /// large compute buffer (multiple GB at ~1 MP), which can exhaust VRAM on top of the resident
    /// model; tiling caps that spike. On for the heavy Qwen-Image models, off for the lighter ones.
    /// </summary>
    public bool TileVae { get; init; }

    /// <summary>
    /// Keep the text encoder/conditioner on the CPU instead of resident in VRAM. stable-diffusion.cpp
    /// keeps every component (encoder + diffusion model + VAE) resident at once, unlike ComfyUI which
    /// offloads on demand; for the big Qwen models the ~8.5 GB Qwen2.5-VL encoder otherwise leaves no
    /// VRAM for the diffusion model + VAE. Offloading it (slower text/vision encode, but it runs once
    /// per generation) frees that VRAM so a Q8 diffusion model fits — matching ComfyUI's peak usage.
    /// </summary>
    public bool OffloadTextEncoderToCpu { get; init; }

    /// <summary>Recommended default sampling steps for this model (e.g. 9 for Z-Image-Turbo).</summary>
    public int DefaultSteps { get; init; } = 20;

    /// <summary>Recommended default classifier-free guidance scale.</summary>
    public float DefaultCfg { get; init; } = 7.0f;

    /// <summary>Recommended default sampler name (e.g. "euler", "dpm++2m_karras").</summary>
    public string DefaultSampler { get; init; } = "euler";

    /// <summary>Recommended default scheduler name (e.g. "simple", "karras").</summary>
    public string DefaultScheduler { get; init; } = "simple";

    /// <summary>Native-aligned width (must be a multiple of this) for txt2img generations.</summary>
    public int DimensionAlignment { get; init; } = 64;

    /// <summary>Recommended default output width.</summary>
    public int DefaultWidth { get; init; } = 1024;

    /// <summary>Recommended default output height.</summary>
    public int DefaultHeight { get; init; } = 1024;
}
