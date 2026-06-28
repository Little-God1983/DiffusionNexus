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
    /// Qwen-Image (2512) inpainting: Qwen-Image flow DiT (GGUF) + Qwen2.5-VL text encoder
    /// (WithLLMPath) + Qwen-Image VAE + the InstantX inpainting ControlNet (load-time
    /// <c>ControlNetPath</c>), with <c>Prediction.Flow</c>. The mask + control image are supplied
    /// per generation. Mirrors the ComfyUI inpaint workflow (Qwen-Image + ControlNetInpaintingAliMama
    /// + 4-step Lightning LoRA).
    /// </summary>
    QwenImageInpaint,

    // TODO(v2-models): Add SDXL, SD15, etc. as we extend the catalog.
}
