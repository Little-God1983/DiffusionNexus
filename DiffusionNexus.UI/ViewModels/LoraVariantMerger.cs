using DiffusionNexus.UI.Domain.Loras;
using System.Collections.Generic;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Provides UI-facing access to the default LoRA variant merger implementation.
/// </summary>
internal static class LoraVariantMerger
{
    private static readonly ILoraVariantMerger Implementation = new DefaultLoraVariantMerger();

    /// <summary>
    /// Merges the provided seeds into card entries while preserving observable behavior from the previous implementation.
    /// </summary>
    /// <param name="seeds">Seeds discovered on disk.</param>
    /// <returns>Ordered list of merged card entries.</returns>
    public static IReadOnlyList<LoraCardEntry> Merge(IEnumerable<LoraCardSeed> seeds) => Implementation.Merge(seeds);
}
