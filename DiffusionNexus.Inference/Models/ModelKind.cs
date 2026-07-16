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

    /// <summary>
    /// Qwen-Image-2512 (Qwen-Image flow DiT distributed as a GGUF, Qwen-2.5-VL-7B LLM text
    /// encoder, Qwen-Image VAE). Loaded via the diffusion-model + LLM + VAE slots with
    /// <c>Prediction.Flow</c> and a flow shift, and always run with its mandatory 4-step
    /// Lightning LoRA (carried on the descriptor's <c>DefaultLoras</c>).
    /// </summary>
    QwenImage2512,

    /// <summary>
    /// Qwen-Image-Edit-2511 (Qwen-Image-Edit flow DiT as a GGUF, Qwen-2.5-VL-7B LLM text encoder +
    /// its mmproj vision projector, Qwen-Image VAE). An image-editing model: the input image is fed
    /// as a reference (VAE-encoded conditioning, like the FLUX.2 kontext / anime-to-real path) and
    /// edited per the text prompt. Loaded with <c>Prediction.Flow</c> + a flow shift, run with the
    /// mandatory 4-step Edit Lightning LoRA.
    /// </summary>
    QwenImageEdit2511,

    // TODO(v2-models): Add SDXL, SD15, etc. as we extend the catalog.
}
