using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Central registry mapping each <see cref="Feature"/> to its <see cref="FeatureRequirements"/>.
/// Today every feature is backed by an Installer SDK workload — <see cref="ComfyUIFeatureBackend"/>
/// uses the <see cref="FeatureRequirements.WorkloadConfigurationId"/> to delegate readiness to
/// the same disk-walking checker the Installer Manager workload dialog uses.
/// </summary>
public static class FeatureRegistry
{
    // SDK workload ids backing each feature. The ComfyUI backend uses these to delegate to
    // IWorkloadInstallationChecker (same source the Installer Manager workload dialog uses).
    //
    // IMPORTANT: these must be declared BEFORE the Registry field below — static field
    // initializers run in textual order, and Registry's BuildRegistry() call references
    // these values. If they're declared afterwards they read as Guid.Empty.
    private static readonly Guid CaptioningWorkloadId   = new("701DA214-2B25-44B4-A904-E4B036621564"); // Captioning-Qwen-3-VL
    private static readonly Guid InpaintingWorkloadId   = new("4C486765-A4C1-4E94-ACC2-BBAC0E405B6A"); // Inpainting-Qwen 2512
    private static readonly Guid OutpaintWorkloadId     = new("137929E4-5C05-4304-80D4-5D785D45FD3F"); // Outpainting-Qwen 2512 (covers Vision variant - same workload installs Qwen3-VL nodes)
    private static readonly Guid BatchUpscaleWorkloadId = new("B853EB7C-0A0E-48A6-985E-E32B2F8848F5"); // Upscaling-Z-Image-Turbo (covers Vision variant)

    private static readonly Dictionary<Feature, FeatureRequirements> Registry = BuildRegistry();

    /// <summary>Gets the requirements for a given feature, or <c>null</c> if unregistered.</summary>
    public static FeatureRequirements? GetRequirements(Feature feature) =>
        Registry.GetValueOrDefault(feature);

    /// <summary>Returns all registered feature requirements. Useful for bulk UI display.</summary>
    public static IReadOnlyCollection<FeatureRequirements> GetAll() => Registry.Values;

    private static Dictionary<Feature, FeatureRequirements> BuildRegistry() => new()
    {
        // Captioning — Qwen-3VL-autocaption.json
        [Feature.Captioning] = new FeatureRequirements(
            Feature.Captioning,
            "AI Captioning (Qwen3-VL)",
            WorkloadConfigurationId: CaptioningWorkloadId),

        // Inpainting — Inpaint-Qwen-2512.json
        [Feature.Inpainting] = new FeatureRequirements(
            Feature.Inpainting,
            "Inpainting (Qwen-2512)",
            WorkloadConfigurationId: InpaintingWorkloadId),

        // Batch Upscale — Z-Image-Turbo-Upscale.json
        [Feature.BatchUpscale] = new FeatureRequirements(
            Feature.BatchUpscale,
            "Batch Upscale",
            WorkloadConfigurationId: BatchUpscaleWorkloadId),

        // Batch Upscale + Vision — Vision-Z-Image-Turbo-Upscale.json
        // Same workload as BatchUpscale; the Z-Image-Turbo workload already brings the
        // Qwen3-VL custom node + model needed for the Vision auto-prompt variant.
        [Feature.BatchUpscaleVision] = new FeatureRequirements(
            Feature.BatchUpscaleVision,
            "Batch Upscale (Vision Auto-Prompt)",
            WorkloadConfigurationId: BatchUpscaleWorkloadId),

        // Outpaint — Qwen-Image-2512-outpaint-nonVision.json
        [Feature.Outpaint] = new FeatureRequirements(
            Feature.Outpaint,
            "Outpaint (Qwen-2512)",
            WorkloadConfigurationId: OutpaintWorkloadId),

        // Outpaint + Vision — Qwen-Image-2512-outpaint-Vision.json
        // Shares the Outpaint workload; the SDK workload already includes Qwen3-VL pieces.
        [Feature.OutpaintVision] = new FeatureRequirements(
            Feature.OutpaintVision,
            "Outpaint (Vision Auto-Prompt)",
            WorkloadConfigurationId: OutpaintWorkloadId),
    };
}
