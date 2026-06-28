using DiffusionNexus.Inference.Models;
using StableDiffusion.NET;
using SDNet = StableDiffusion.NET;

namespace DiffusionNexus.Inference.StableDiffusionCpp;

/// <summary>
/// Architecture-specific mapping from a <see cref="ModelDescriptor"/> to the
/// <see cref="SDNet.DiffusionModelParameter"/> shape that <c>stable-diffusion.cpp</c> expects.
/// Centralizes the per-architecture wiring (which file goes into which slot) so the
/// backend itself stays a thin orchestrator.
/// </summary>
internal static class StableDiffusionCppLoader
{
    /// <summary>
    /// Builds the load parameters for the given descriptor. Throws
    /// <see cref="NotSupportedException"/> for architectures the v1 loader doesn't handle yet.
    /// </summary>
    public static SDNet.DiffusionModelParameter Build(ModelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return descriptor.Kind switch
        {
            ModelKind.ZImageTurbo => BuildZImageTurbo(descriptor),
            ModelKind.Flux2Klein => BuildFlux2Klein(descriptor),

            // TODO(v2-models): add SDXL/QwenImageEdit cases here.
            _ => throw new NotSupportedException(
                $"ModelKind '{descriptor.Kind}' is not supported by the v1 loader.")
        };
    }

    /// <summary>
    /// FLUX.2-klein wiring: GGUF diffusion model (DiffusionModelPath) + Qwen-3-8B text encoder
    /// (WithLLMPath, NOT a CLIP slot) + FLUX.2 VAE, with the FLUX.2 flow prediction mode.
    /// The stable-diffusion.cpp backend auto-detects the GGUF quantization (incl. MXFP4).
    /// LoRAs are applied per-generation via <c>ImageGenerationParameter.Loras</c>, not here.
    /// </summary>
    private static SDNet.DiffusionModelParameter BuildFlux2Klein(ModelDescriptor d)
    {
        if (string.IsNullOrWhiteSpace(d.DiffusionModelPath))
            throw new InvalidOperationException("FLUX.2-klein requires DiffusionModelPath (the GGUF diffusion model).");
        if (!d.TextEncoders.TryGetValue(TextEncoderSlot.Llm, out var llmPath))
            throw new InvalidOperationException("FLUX.2-klein requires an LLM text encoder (TextEncoderSlot.Llm).");
        if (string.IsNullOrWhiteSpace(d.VaePath))
            throw new InvalidOperationException("FLUX.2-klein requires a VAE file.");

        return SDNet.DiffusionModelParameter.Create()
            .WithDiffusionModelPath(d.DiffusionModelPath)
            .WithLLMPath(llmPath)
            .WithVae(d.VaePath)
            .WithPrediction(SDNet.Prediction.Flux2Flow)
            // FLUX.2-klein is large (~24 GB VRAM for the BF16 weights alone). Tile the VAE decode so
            // the (otherwise multi-GB) decode buffer at 1024px doesn't push a 24 GB card into OOM.
            .WithVaeTiling()
            .WithMultithreading()
            .WithFlashAttention();
    }

    /// <summary>
    /// Z-Image-Turbo wiring proven by the spike:
    /// UNET (DiffusionModelPath) + Qwen-3-4B text encoder (WithLLMPath, NOT a CLIP slot) + Flux-style ae VAE.
    /// </summary>
    private static SDNet.DiffusionModelParameter BuildZImageTurbo(ModelDescriptor d)
    {
        if (string.IsNullOrWhiteSpace(d.DiffusionModelPath))
            throw new InvalidOperationException("Z-Image-Turbo requires DiffusionModelPath (the UNET file).");
        if (!d.TextEncoders.TryGetValue(TextEncoderSlot.Llm, out var llmPath))
            throw new InvalidOperationException("Z-Image-Turbo requires an LLM text encoder (TextEncoderSlot.Llm).");
        if (string.IsNullOrWhiteSpace(d.VaePath))
            throw new InvalidOperationException("Z-Image-Turbo requires a VAE file.");

        return SDNet.DiffusionModelParameter.Create()
            .WithDiffusionModelPath(d.DiffusionModelPath)
            .WithLLMPath(llmPath)
            .WithVae(d.VaePath)
            .WithMultithreading()
            .WithFlashAttention();
    }
}
