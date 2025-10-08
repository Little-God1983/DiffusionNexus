using System.Collections.Generic;

namespace DiffusionNexus.UI.Domain.Loras;

/// <summary>
/// Defines the contract for merging LoRA model seeds into grouped card entries.
/// </summary>
internal interface ILoraVariantMerger
{
    /// <summary>
    /// Merges the provided <see cref="LoraCardSeed"/> collection using variant classification rules.
    /// </summary>
    /// <param name="seeds">Seeds discovered on disk.</param>
    /// <returns>Ordered list of <see cref="LoraCardEntry"/> objects ready for rendering.</returns>
    IReadOnlyList<LoraCardEntry> Merge(IEnumerable<LoraCardSeed> seeds);
}
