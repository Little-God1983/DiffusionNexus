namespace DiffusionNexus.Inference.Models;

/// <summary>
/// Identifies which text-encoder slot a model file occupies in stable-diffusion.cpp.
/// Different architectures wire their text encoder(s) differently; this enum keeps the
/// mapping declarative on the <see cref="ModelDescriptor"/>.
/// </summary>
public enum TextEncoderSlot
{
    /// <summary>OpenAI CLIP ViT-L (used by SD1.5 and as one half of SDXL's encoder pair).</summary>
    ClipL,

    /// <summary>OpenCLIP ViT-bigG (the second half of SDXL's encoder pair).</summary>
    ClipG,

    /// <summary>Google T5-XXL text encoder (used by SD3, Flux, etc.).</summary>
    T5Xxl,

    /// <summary>Generic LLM text encoder slot (e.g. Qwen-3-4B for Z-Image-Turbo, Qwen-2.5-VL for Qwen-Image).</summary>
    Llm,

    /// <summary>LLM vision projector ("mmproj") for vision-language conditioning (Qwen-Image-Edit etc.).</summary>
    LlmVision,
}
