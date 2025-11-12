namespace DiffusionNexus.Service.Services;

/// <summary>
/// Describes the identifiers encoded within a Civitai model URL.
/// </summary>
/// <param name="ModelId">The numeric model identifier.</param>
/// <param name="ModelVersionId">Optional model version identifier.</param>
public readonly record struct CivitaiLinkInfo(int ModelId, int? ModelVersionId);
