using System.Collections.Generic;
using DiffusionNexus.UI.Models.Pipelines;

namespace DiffusionNexus.UI.Services.Pipelines;

/// <summary>
/// Loads <see cref="PipelineManifest"/> definitions (one per guided pipeline) from the
/// app's embedded <c>Assets/Pipelines/*.json</c> resources.
/// </summary>
public interface IPipelineManifestProvider
{
    /// <summary>All known pipeline manifests, in declaration order.</summary>
    IReadOnlyList<PipelineManifest> All();

    /// <summary>Returns the manifest with the given id, or null if none is defined.</summary>
    PipelineManifest? Get(string pipelineId);
}
