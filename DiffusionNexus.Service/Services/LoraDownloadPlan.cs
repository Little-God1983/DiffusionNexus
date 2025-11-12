namespace DiffusionNexus.Service.Services;

/// <summary>
/// Represents a prepared download for a LoRA model version.
/// </summary>
/// <param name="ModelId">The Civitai model identifier.</param>
/// <param name="ModelVersionId">The resolved model version identifier.</param>
/// <param name="FileName">Sanitised file name suggested by Civitai.</param>
/// <param name="DownloadUri">Direct download URI.</param>
/// <param name="TotalBytes">Known file size in bytes (if provided by the API).</param>
public sealed record LoraDownloadPlan(int ModelId, int ModelVersionId, string FileName, Uri DownloadUri, long? TotalBytes);
