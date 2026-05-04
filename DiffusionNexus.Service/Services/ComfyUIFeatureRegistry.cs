using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Central registry mapping each <see cref="ComfyUIFeature"/> to its required custom nodes and models.
/// 
/// <para>
/// When a workflow changes (e.g. a new model or custom node is added), update the corresponding
/// entry here. The <see cref="ComfyUIReadinessService"/> consults this registry at runtime to
/// know what to verify against the live ComfyUI server.
/// </para>
/// 
/// <para>
/// This design keeps requirements declarative and separate from the checking logic,
/// making it easy to add new features or swap diffusion backends in the future.
/// </para>
/// </summary>
public static class ComfyUIFeatureRegistry
{
    private static readonly Dictionary<ComfyUIFeature, ComfyUIFeatureRequirements> Registry = BuildRegistry();

    /// <summary>
    /// Gets the requirements for a given feature, or <c>null</c> if the feature is not registered.
    /// </summary>
    public static ComfyUIFeatureRequirements? GetRequirements(ComfyUIFeature feature) =>
        Registry.GetValueOrDefault(feature);

    /// <summary>
    /// Returns all registered feature requirements. Useful for bulk UI display.
    /// </summary>
    public static IReadOnlyCollection<ComfyUIFeatureRequirements> GetAll() => Registry.Values;

    private static Dictionary<ComfyUIFeature, ComfyUIFeatureRequirements> BuildRegistry()
    {
        var registry = new Dictionary<ComfyUIFeature, ComfyUIFeatureRequirements>();

        // ── Captioning (Qwen-3VL-autocaption.json) ──────────────────────────
        // Nodes: Qwen3_VQA, ShowText|pysssss (LoadImage is built-in)
        // Model: Qwen3-VL-4B-Instruct-FP8 (auto-downloads on first run)
        registry[ComfyUIFeature.Captioning] = new ComfyUIFeatureRequirements(
            ComfyUIFeature.Captioning,
            "AI Captioning (Qwen3-VL)",
            RequiredNodeTypes: ["Qwen3_VQA", "ShowText|pysssss"],
            RequiredModels:
            [
                new ModelRequirement(
                    NodeType: "Qwen3_VQA",
                    InputName: "model",
                    ExpectedModelSubstring: "Qwen3-VL-4B-Instruct-FP8",
                    DisplayName: "Qwen3-VL-4B-Instruct-FP8",
                    ApproximateSizeDescription: "~8 GB",
                    AutoDownloads: true)
            ]);

        // ── Inpainting (Inpaint-Qwen-2512.json) ────────────────────────────
        // Custom nodes: UnetLoaderGGUF, ControlNetInpaintingAliMamaApply
        // All other nodes (VAELoader, CLIPLoader, KSampler, etc.) are built-in.
        // Model: qwen-image-2512 GGUF variants (auto-downloads on first run)
        registry[ComfyUIFeature.Inpainting] = new ComfyUIFeatureRequirements(
            ComfyUIFeature.Inpainting,
            "Inpainting (Qwen-2512)",
            RequiredNodeTypes: ["UnetLoaderGGUF", "ControlNetInpaintingAliMamaApply"],
            RequiredModels:
            [
                new ModelRequirement(
                    NodeType: "UnetLoaderGGUF",
                    InputName: "unet_name",
                    ExpectedModelSubstring: "qwen-image-2512",
                    DisplayName: "Qwen-Image-2512 GGUF",
                    ApproximateSizeDescription: "~8 GB",
                    AutoDownloads: true)
            ]);

        // ── Batch Upscale (Z-Image-Turbo-Upscale.json) ─────────────────────
        // Custom nodes: UltimateSDUpscale, Power Lora Loader (rgthree)
        // Built-in nodes: CLIPTextEncode, VAELoader, CLIPLoader, UNETLoader,
        //   UpscaleModelLoader, SaveImage, LoadImage
        registry[ComfyUIFeature.BatchUpscale] = new ComfyUIFeatureRequirements(
            ComfyUIFeature.BatchUpscale,
            "Batch Upscale",
            RequiredNodeTypes: ["UltimateSDUpscale", "Power Lora Loader (rgthree)"],
            RequiredModels: []);

        // ── Batch Upscale + Vision (Vision-Z-Image-Turbo-Upscale.json) ──────
        // Same as BatchUpscale plus Qwen3_VQA and SomethingToString for auto-prompt.
        registry[ComfyUIFeature.BatchUpscaleVision] = new ComfyUIFeatureRequirements(
            ComfyUIFeature.BatchUpscaleVision,
            "Batch Upscale (Vision Auto-Prompt)",
            RequiredNodeTypes: ["UltimateSDUpscale", "Power Lora Loader (rgthree)", "Qwen3_VQA", "SomethingToString"],
            RequiredModels:
            [
                new ModelRequirement(
                    NodeType: "Qwen3_VQA",
                    InputName: "model",
                    ExpectedModelSubstring: "Qwen3-VL-4B-Instruct-FP8",
                    DisplayName: "Qwen3-VL-4B-Instruct-FP8",
                    ApproximateSizeDescription: "~8 GB",
                    AutoDownloads: true)
            ]);

        // ── Outpaint (Qwen-Image-2512-outpaint-nonVision.json) ──────────────
        // Custom nodes: UnetLoaderGGUF, ControlNetInpaintingAliMamaApply,
        //   ImageScaleToMaxDimension (KJNodes), ImagePadForOutpaint (built-in),
        //   ImageBlur, GrowMask, ImageToMask, MaskToImage (built-in mask utilities).
        // Model: qwen-image-2512 GGUF + Qwen-Image-InstantX-ControlNet-Inpainting.
        registry[ComfyUIFeature.Outpaint] = new ComfyUIFeatureRequirements(
            ComfyUIFeature.Outpaint,
            "Outpaint (Qwen-2512)",
            RequiredNodeTypes: ["UnetLoaderGGUF", "ControlNetInpaintingAliMamaApply", "ImageScaleToMaxDimension"],
            RequiredModels:
            [
                new ModelRequirement(
                    NodeType: "UnetLoaderGGUF",
                    InputName: "unet_name",
                    ExpectedModelSubstring: "qwen-image-2512",
                    DisplayName: "Qwen-Image-2512 GGUF",
                    ApproximateSizeDescription: "~8 GB",
                    AutoDownloads: true),
                new ModelRequirement(
                    NodeType: "ControlNetLoader",
                    InputName: "control_net_name",
                    ExpectedModelSubstring: "Qwen-Image-InstantX-ControlNet-Inpainting",
                    DisplayName: "Qwen-Image-InstantX-ControlNet-Inpainting",
                    ApproximateSizeDescription: "~2 GB",
                    AutoDownloads: true)
            ]);

        // ── Outpaint Vision (Qwen-Image-2512-outpaint-Vision.json) ──────────
        // Same as Outpaint plus Qwen3_VQA + SomethingToString for auto-prompt.
        registry[ComfyUIFeature.OutpaintVision] = new ComfyUIFeatureRequirements(
            ComfyUIFeature.OutpaintVision,
            "Outpaint (Vision Auto-Prompt)",
            RequiredNodeTypes: ["UnetLoaderGGUF", "ControlNetInpaintingAliMamaApply", "ImageScaleToMaxDimension", "Qwen3_VQA", "SomethingToString"],
            RequiredModels:
            [
                new ModelRequirement(
                    NodeType: "UnetLoaderGGUF",
                    InputName: "unet_name",
                    ExpectedModelSubstring: "qwen-image-2512",
                    DisplayName: "Qwen-Image-2512 GGUF",
                    ApproximateSizeDescription: "~8 GB",
                    AutoDownloads: true),
                new ModelRequirement(
                    NodeType: "ControlNetLoader",
                    InputName: "control_net_name",
                    ExpectedModelSubstring: "Qwen-Image-InstantX-ControlNet-Inpainting",
                    DisplayName: "Qwen-Image-InstantX-ControlNet-Inpainting",
                    ApproximateSizeDescription: "~2 GB",
                    AutoDownloads: true),
                new ModelRequirement(
                    NodeType: "Qwen3_VQA",
                    InputName: "model",
                    ExpectedModelSubstring: "Qwen3-VL-4B-Instruct-FP8",
                    DisplayName: "Qwen3-VL-4B-Instruct-FP8",
                    ApproximateSizeDescription: "~8 GB",
                    AutoDownloads: true)
            ]);

        return registry;
    }
}
