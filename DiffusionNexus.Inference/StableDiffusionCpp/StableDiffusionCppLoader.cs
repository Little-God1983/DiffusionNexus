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

        // Repair known-broken stale GGUF uploads (zero-offset sentinel tensor) in place before the
        // native engine tries to parse them — otherwise ggml rejects the file and the load hangs/fails.
        GgufSentinelFixer.EnsureLoadable(descriptor.DiffusionModelPath);

        return descriptor.Kind switch
        {
            ModelKind.ZImageTurbo => BuildZImageTurbo(descriptor),
            ModelKind.Flux2Klein => BuildFlux2Klein(descriptor),
            ModelKind.QwenImage2512 => BuildQwenImage2512(descriptor),
            ModelKind.QwenImageEdit2511 => BuildQwenImageEdit2511(descriptor),

            // TODO(v2-models): add SDXL cases here.
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
    /// Qwen-Image-2512 wiring: GGUF diffusion model (DiffusionModelPath) + Qwen-2.5-VL-7B text
    /// encoder (WithLLMPath, NOT a CLIP slot) + Qwen-Image VAE, with the flow prediction mode.
    /// The flow shift and the mandatory 4-step Lightning LoRA are applied per-generation by the
    /// backend (from the descriptor's DefaultFlowShift / DefaultLoras), not here.
    /// </summary>
    private static SDNet.DiffusionModelParameter BuildQwenImage2512(ModelDescriptor d)
    {
        if (string.IsNullOrWhiteSpace(d.DiffusionModelPath))
            throw new InvalidOperationException("Qwen-Image-2512 requires DiffusionModelPath (the GGUF diffusion model).");
        if (!d.TextEncoders.TryGetValue(TextEncoderSlot.Llm, out var llmPath))
            throw new InvalidOperationException("Qwen-Image-2512 requires an LLM text encoder (TextEncoderSlot.Llm).");
        if (string.IsNullOrWhiteSpace(d.VaePath))
            throw new InvalidOperationException("Qwen-Image-2512 requires a VAE file.");

        var p = SDNet.DiffusionModelParameter.Create()
            .WithDiffusionModelPath(d.DiffusionModelPath)
            .WithLLMPath(llmPath)
            .WithVae(d.VaePath)
            .WithPrediction(SDNet.Prediction.Flow)
            // Qwen-Image-2512 is a ~20B DiT; tile the VAE decode so the (multi-GB) 1024px decode
            // buffer doesn't push a mid-range card into OOM (same reasoning as FLUX.2-klein).
            .WithVaeTiling()
            .WithMultithreading()
            .WithFlashAttention();

        // Offload the Qwen2.5-VL encoder to CPU so it doesn't sit resident in VRAM alongside the
        // ~20 GB diffusion model + VAE (mirrors ComfyUI's on-demand offloading, which is why Q8 fits
        // there). Frees ~8.5 GB of VRAM at the cost of a slower (one-time) text encode.
        if (d.OffloadTextEncoderToCpu)
            p = p.WithClipNetOnCpu(true);

        return p;
    }

    /// <summary>
    /// Qwen-Image-Edit-2511 wiring: GGUF diffusion model + Qwen-2.5-VL-7B text encoder (WithLLMPath) +
    /// its vision projector / mmproj (WithLLMVisionPath, so the model can "see" the reference image
    /// being edited) + Qwen-Image VAE, with the flow prediction mode. The reference (input) image and
    /// the mandatory Edit Lightning LoRA are supplied per-generation by the backend.
    /// </summary>
    private static SDNet.DiffusionModelParameter BuildQwenImageEdit2511(ModelDescriptor d)
    {
        if (string.IsNullOrWhiteSpace(d.DiffusionModelPath))
            throw new InvalidOperationException("Qwen-Image-Edit-2511 requires DiffusionModelPath (the GGUF diffusion model).");
        if (!d.TextEncoders.TryGetValue(TextEncoderSlot.Llm, out var llmPath))
            throw new InvalidOperationException("Qwen-Image-Edit-2511 requires an LLM text encoder (TextEncoderSlot.Llm).");
        if (string.IsNullOrWhiteSpace(d.VaePath))
            throw new InvalidOperationException("Qwen-Image-Edit-2511 requires a VAE file.");

        var p = SDNet.DiffusionModelParameter.Create()
            .WithDiffusionModelPath(d.DiffusionModelPath)
            .WithLLMPath(llmPath)
            .WithVae(d.VaePath)
            .WithPrediction(SDNet.Prediction.Flow)
            // ~20B DiT — tile the VAE decode to bound peak VRAM at 1024² (same as Qwen-Image-2512).
            .WithVaeTiling()
            .WithMultithreading()
            .WithFlashAttention();

        // Vision projector (mmproj) for the edit model's image understanding, when present.
        if (d.TextEncoders.TryGetValue(TextEncoderSlot.LlmVision, out var visionPath)
            && !string.IsNullOrWhiteSpace(visionPath))
        {
            p = p.WithLLMVisionPath(visionPath);
        }

        // Offload the Qwen2.5-VL encoder to CPU so the ~20 GB diffusion model + VAE compute fit in VRAM
        // (sd.cpp keeps everything resident; ComfyUI offloads, which is why Q8 fits there at <80%).
        if (d.OffloadTextEncoderToCpu)
            p = p.WithClipNetOnCpu(true);

        return p;
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
