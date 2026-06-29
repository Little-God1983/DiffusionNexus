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
            ModelKind.QwenImageInpaint => BuildQwenImageInpaint(descriptor),

            // TODO(v2-models): add SDXL cases here.
            _ => throw new NotSupportedException(
                $"ModelKind '{descriptor.Kind}' is not supported by the v1 loader.")
        };
    }

    /// <summary>
    /// Qwen-Image inpaint wiring: GGUF diffusion model + Qwen2.5-VL text encoder (WithLLMPath) +
    /// Qwen-Image VAE, with <c>Prediction.Flow</c>. Inpaint is done natively via the per-generation
    /// init image + mask.
    ///
    /// IMPORTANT: the InstantX Qwen-Image inpainting ControlNet is NOT loaded. This stable-diffusion.cpp
    /// build's ControlNet loader only understands the classic SD/SDXL UNet architecture
    /// (<c>input_hint_block</c> / <c>zero_convs</c> / <c>input_blocks</c>); the InstantX model is a
    /// Qwen-Image DiT ControlNet, so loading it fails with "load control net tensors from model loader
    /// failed" and aborts model init. We therefore fall back to native masked inpaint (the masked
    /// region is regenerated, unmasked pixels preserved). The control file is only used by the ComfyUI
    /// path. If a future engine build supports Qwen-Image DiT ControlNets, set
    /// <see cref="ModelDescriptor.ControlNetPath"/> in the catalog and it loads via the guard below.
    /// </summary>
    private static SDNet.DiffusionModelParameter BuildQwenImageInpaint(ModelDescriptor d)
    {
        if (string.IsNullOrWhiteSpace(d.DiffusionModelPath))
            throw new InvalidOperationException("Qwen-Image inpaint requires DiffusionModelPath (the GGUF diffusion model).");
        if (!d.TextEncoders.TryGetValue(TextEncoderSlot.Llm, out var llmPath))
            throw new InvalidOperationException("Qwen-Image inpaint requires a Qwen2.5-VL text encoder (TextEncoderSlot.Llm).");
        if (string.IsNullOrWhiteSpace(d.VaePath))
            throw new InvalidOperationException("Qwen-Image inpaint requires a VAE file.");

        var p = SDNet.DiffusionModelParameter.Create()
            .WithDiffusionModelPath(d.DiffusionModelPath)
            .WithLLMPath(llmPath)
            .WithVae(d.VaePath)
            .WithPrediction(SDNet.Prediction.Flow)
            // Qwen-Image is large; tile the VAE decode so the decode buffer doesn't OOM at 1024px.
            .WithVaeTiling()
            .WithMultithreading()
            .WithFlashAttention();

        // Only load a ControlNet if the catalog supplied one (it deliberately does NOT for Qwen on
        // this engine build — see the note above).
        if (!string.IsNullOrWhiteSpace(d.ControlNetPath))
            p = p.WithControlNet(d.ControlNetPath);

        return p;
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
