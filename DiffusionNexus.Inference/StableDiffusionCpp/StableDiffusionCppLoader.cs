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

            // TODO(v2-models): add SDXL/Flux/QwenImageEdit cases here.
            _ => throw new NotSupportedException(
                $"ModelKind '{descriptor.Kind}' is not supported by the v1 loader.")
        };
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
