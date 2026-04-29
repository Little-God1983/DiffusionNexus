namespace DiffusionNexus.Inference.Models;

/// <summary>
/// Identifies a diffusion-model architecture family. Each value implies a specific
/// loader strategy (which file slots to populate, which sampler defaults apply, etc.).
/// </summary>
public enum ModelKind
{
    /// <summary>Z-Image-Turbo (Lumina2 family DiT, Qwen-3-4B LLM text encoder, Flux-style VAE).</summary>
    ZImageTurbo,

    // TODO(v2-models): Add SDXL, SD15, Flux, QwenImageEdit, etc. as we extend the catalog.
}
