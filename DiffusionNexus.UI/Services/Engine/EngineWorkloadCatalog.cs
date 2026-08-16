namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// The curated set of workloads offered inside the Diffusion Nexus Engine tile. Deliberately a
/// short allow-list rather than "every ComfyUI configuration": the engine is app-owned, so only
/// workloads we have verified against it are offered. Adding one is a single entry here.
/// </summary>
public static class EngineWorkloadCatalog
{
    /// <summary>Krea 2 Turbo — the first supported engine workload, and the engine's torch source.</summary>
    public static readonly Guid Krea2Turbo = Guid.Parse("E79C079A-2FD7-4FE7-8086-23731092555D");

    /// <summary>Configurations offered in the engine tile, in display order.</summary>
    public static IReadOnlyList<Guid> WorkloadIds { get; } = [Krea2Turbo];

    /// <summary>True when the configuration is an offered engine workload.</summary>
    public static bool Contains(Guid id) => WorkloadIds.Contains(id);

    /// <summary>
    /// Picks the default VRAM tier for a card: the largest configured tier that fits in the
    /// detected VRAM, falling back to the smallest tier when VRAM is unknown or below every tier
    /// (a too-small quantization still runs; refusing to preselect anything would not help).
    /// Returns 0 when the workload declares no tiers — the workload installer reads 0 as
    /// "no VRAM filtering".
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
}
