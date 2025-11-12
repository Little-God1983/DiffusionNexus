namespace DiffusionNexus.Service.Services;

/// <summary>
/// Result of executing a LoRA download operation.
/// </summary>
/// <param name="Succeeded">Indicates whether the download completed successfully.</param>
/// <param name="FilePath">Absolute path to the downloaded file.</param>
public sealed record LoraDownloadResult(bool Succeeded, string? FilePath = null);
