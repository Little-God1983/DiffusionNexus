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
