namespace DiffusionNexus.Inference.Models;

/// <summary>
/// Identifies a diffusion-model architecture family. Each value implies a specific
/// loader strategy (which file slots to populate, which sampler defaults apply, etc.).
/// </summary>
public enum ModelKind
{
    /// <summary>Z-Image-Turbo (Lumina2 family DiT, Qwen-3-4B LLM text encoder, Flux-style VAE).</summary>
    ZImageTurbo,

    /// <summary>
    /// FLUX.2-klein (FLUX.2 flow DiT distributed as a GGUF, Qwen-3-8B LLM text encoder,
    /// FLUX.2 VAE). Loaded via the same diffusion-model + LLM + VAE slots as Z-Image-Turbo,
    /// with <c>Prediction.Flux2Flow</c>.
    /// </summary>
    Flux2Klein,

    // TODO(v2-models): Add SDXL, SD15, QwenImageEdit, etc. as we extend the catalog.
}
