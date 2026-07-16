using System.Collections.Generic;
using System.Linq;

namespace DiffusionNexus.UI.Models.Pipelines;

/// <summary>On-disk presence of a single pipeline asset.</summary>
/// <param name="Name">The asset's display name.</param>
/// <param name="Kind">The asset's role.</param>
/// <param name="IsPresent">Whether a satisfying file was found under the models root.</param>
/// <param name="ResolvedFileName">The matched filename, when present.</param>
public sealed record PipelineAssetState(string Name, PipelineAssetKind Kind, bool IsPresent, string? ResolvedFileName);

/// <summary>The result of checking a pipeline's required assets against the models folder.</summary>
public sealed record PipelineReadiness(IReadOnlyList<PipelineAssetState> Assets)
{
    /// <summary>True when every required asset was found on disk.</summary>
    public bool IsComplete => Assets.Count > 0 && Assets.All(a => a.IsPresent);

    /// <summary>The assets still missing.</summary>
    public IReadOnlyList<PipelineAssetState> Missing => Assets.Where(a => !a.IsPresent).ToList();
}
