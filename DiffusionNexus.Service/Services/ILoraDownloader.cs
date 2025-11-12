namespace DiffusionNexus.Service.Services;

/// <summary>
/// Coordinates preparing and downloading LoRA models from Civitai.
/// </summary>
public interface ILoraDownloader
{
    /// <summary>
    /// Resolves the concrete model version and download metadata.
    /// </summary>
    Task<LoraDownloadPlan> PrepareAsync(int modelId, int? modelVersionId, string? apiKey, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads the file described by the supplied plan to the given path.
    /// </summary>
    Task<LoraDownloadResult> DownloadAsync(
        LoraDownloadPlan plan,
        string targetFilePath,
        IProgress<LoraDownloadProgress>? progress,
        CancellationToken cancellationToken);
}
