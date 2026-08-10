namespace DiffusionNexus.Domain.Services;

/// <summary>
/// The one translation between per-file model-download progress and the
/// download coordinator's task progress. Every call site that enqueues a
/// model download through <see cref="IDownloadCoordinator"/> needs exactly
/// this mapping — keep it here so throttling/format changes happen once.
/// </summary>
public static class DownloadProgressExtensions
{
    public static DownloadTaskProgress ToDownloadTaskProgress(this ModelDownloadProgress progress)
    {
        var percent = progress.TotalBytes > 0
            ? (int)((double)progress.BytesDownloaded / progress.TotalBytes * 100.0)
            : 0;
        return new DownloadTaskProgress(percent, progress.Status);
    }
}
