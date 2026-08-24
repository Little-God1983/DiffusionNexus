using DiffusionNexus.Civitai.Models;

namespace DiffusionNexus.UI.Services;

/// <summary>Transport + persister seam so CivitaiModelDownloader (Task 5) is unit-testable.</summary>
public interface ILoraDownloadService
{
    Task DownloadFileAsync(
        string downloadUrl, string targetPath, CivitaiModelVersion civitaiVersion, string taskName,
        Action<double, string>? reportProgress = null, Action? completed = null, Action? failed = null,
        int? existingModelId = null, CancellationToken externalCancellationToken = default,
        bool reportToActivityLog = true, Action? metadataIncomplete = null);

    Task<MetadataPersistOutcome> PersistDownloadedModelAsync(
        string filePath, CivitaiModelVersion civitaiVersion, int? existingModelId = null);
}
