using DiffusionNexus.Civitai.Models;

namespace DiffusionNexus.UI.Services;

/// <summary>Transport + persister seam so CivitaiModelDownloader (Task 5) is unit-testable.</summary>
public interface ILoraDownloadService
{
    /// <summary>
    /// Streams <paramref name="downloadUrl"/> to <paramref name="targetPath"/>.
    /// </summary>
    /// <remarks>
    /// <b>Liveness contract:</b> an implementation MUST invoke exactly one of
    /// <paramref name="completed"/> or <paramref name="failed"/> before returning, on every path
    /// including cancellation and exceptions. <c>CivitaiModelDownloader</c> awaits a
    /// <see cref="TaskCompletionSource{TResult}"/> that only those two callbacks resolve, so an
    /// implementation that returns without calling either hangs the caller's download forever.
    /// </remarks>
    Task DownloadFileAsync(
        string downloadUrl, string targetPath, CivitaiModelVersion civitaiVersion, string taskName,
        Action<double, string>? reportProgress = null, Action? completed = null, Action? failed = null,
        int? existingModelId = null, CancellationToken externalCancellationToken = default,
        bool reportToActivityLog = true, Action? metadataIncomplete = null);

    Task<MetadataPersistOutcome> PersistDownloadedModelAsync(
        string filePath, CivitaiModelVersion civitaiVersion, int? existingModelId = null);
}
