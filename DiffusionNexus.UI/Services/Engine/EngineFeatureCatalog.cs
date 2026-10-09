using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>One row of the Engine's Features dialog.</summary>
public enum EngineFeature
{
    /// <summary>Image Editor Inpaint and non-Vision Outpaint (identical model set).</summary>
    InpaintOutpaint,

    /// <summary>Image Editor Outpaint with Vision: the Outpaint set plus Qwen3-VL and its node packs.</summary>
    OutpaintVision,

    /// <summary>Diffusion Canvas text-to-image with Krea 2 Turbo.</summary>
    Canvas
}

/// <summary>
/// What a Features row shows, which catalog workloads it installs, and which folder models (models a
/// node loads as a whole folder, which the catalog cannot describe) it downloads on top.
/// </summary>
public sealed record EngineFeatureDefinition(
    EngineFeature Feature,
    string DisplayName,
    string Description,
    IReadOnlyList<Guid> WorkloadIds,
    IReadOnlyList<EngineFolderModel>? FolderModelsOrNull = null)
{
    public IReadOnlyList<EngineFolderModel> FolderModels => FolderModelsOrNull ?? [];
}

/// <summary>
/// The app features the Diffusion Nexus Engine can be equipped with, each mapped to the catalog
/// workloads that carry its node packs and models. Rows map to whole workloads so the catalog stays
/// the single source of what a feature needs. The one exception is a folder model, which the catalog
/// cannot express (see <see cref="EngineFolderModel"/>). A row only appears once it has been verified
/// against the Engine: Batch Upscale arrives with #608.
/// </summary>
public static class EngineFeatureCatalog
{
    /// <summary>"Inpainting-Qwen 2512": ComfyUI-GGUF + the five Qwen-Image 2512 inpaint models.</summary>
    public static readonly Guid InpaintingQwen2512 = Guid.Parse("4C486765-A4C1-4E94-ACC2-BBAC0E405B6A");

    /// <summary>
    /// "Outpainting-Qwen 2512": the Inpainting set (same model names, so nothing is downloaded twice;
    /// the checker accepts any present Qwen-Image 2512 GGUF) plus ComfyUI_Qwen3-VL-Instruct,
    /// ComfyUI-Custom-Scripts (ShowText) and ComfyUI-KJNodes (SomethingToString). Its "Qwen 3 VL" model
    /// is a catalog placeholder; the files come from <see cref="EngineFolderModels.Qwen3VL4BInstructFp8"/>.
    /// </summary>
    public static readonly Guid OutpaintingQwen2512 = Guid.Parse("137929E4-5C05-4304-80D4-5D785D45FD3F");

    /// <summary>Krea 2 Turbo — the first Engine workload, and the Engine's torch source.</summary>
    public static readonly Guid Krea2Turbo = Guid.Parse("E79C079A-2FD7-4FE7-8086-23731092555D");

    /// <summary>Rows in display order.</summary>
    public static IReadOnlyList<EngineFeatureDefinition> All { get; } =
    [
        new(EngineFeature.InpaintOutpaint,
            "Inpaint & Outpaint",
            "Image Editor · Qwen-Image 2512 with the InstantX inpaint ControlNet and the Lightning LoRA",
            [InpaintingQwen2512]),
        new(EngineFeature.OutpaintVision,
            "Outpaint Vision",
            "Image Editor · Qwen3-VL describes the surroundings and writes the outpaint prompt (adds Qwen3-VL, 6 GB)",
            [OutpaintingQwen2512],
            [EngineFolderModels.Qwen3VL4BInstructFp8]),
        new(EngineFeature.Canvas,
            "Canvas · Krea 2 Turbo",
            "Text to image in the Diffusion Canvas",
            [Krea2Turbo]),
    ];

    /// <summary>Every workload id any row installs, without duplicates.</summary>
    public static IReadOnlyList<Guid> AllWorkloadIds { get; } =
        All.SelectMany(r => r.WorkloadIds).Distinct().ToList();

    public static EngineFeatureDefinition Get(EngineFeature feature) =>
        All.Single(r => r.Feature == feature);

    /// <summary>The row that equips the Engine for an app feature, or null when the Engine does not offer it yet.</summary>
    public static EngineFeature? ForAppFeature(Feature feature) => feature switch
    {
        Feature.Inpainting or Feature.Outpaint => EngineFeature.InpaintOutpaint,
        Feature.OutpaintVision => EngineFeature.OutpaintVision,
        _ => null
    };

    /// <summary>
    /// Picks the default VRAM tier: the largest configured tier that fits in the detected VRAM,
    /// falling back to the smallest tier when VRAM is unknown or below every tier (a too-small
    /// quantization still runs). Returns 0 when the workload declares no tiers — the workload
    /// installer reads 0 as "no VRAM filtering".
    /// </summary>
    public static int SuggestVramTier(long vramTotalMb, IReadOnlyList<int> configuredTiers)
    {
        if (configuredTiers is null || configuredTiers.Count == 0)
            return 0;

        var ordered = configuredTiers.OrderBy(t => t).ToList();
        var vramGb = vramTotalMb / 1024.0;

        var best = ordered.LastOrDefault(t => t <= vramGb);
        return best == 0 ? ordered[0] : best;
    }

    /// <summary>Parses the catalog's comma-separated VRAM profiles ("8,16,24,24+" or "8GB") into GB values.</summary>
    public static int[] ParseVramProfiles(string? vramProfiles)
    {
        if (string.IsNullOrWhiteSpace(vramProfiles))
            return [];

        return vramProfiles
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p.Replace("GB", "").Replace("+", ""), out var val) ? val : 0)
            .Where(v => v > 0)
            .ToArray();
    }
}
