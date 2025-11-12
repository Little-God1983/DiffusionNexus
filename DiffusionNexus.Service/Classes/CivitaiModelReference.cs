namespace DiffusionNexus.Service.Classes;

/// <summary>
/// Represents a parsed reference to a Civitai model or model version.
/// </summary>
/// <param name="ModelId">The model identifier, if available.</param>
/// <param name="ModelVersionId">The specific model version identifier, if provided.</param>
public record CivitaiModelReference(int? ModelId, int? ModelVersionId);
