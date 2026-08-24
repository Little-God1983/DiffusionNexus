using DiffusionNexus.Civitai.Models;

namespace DiffusionNexus.UI.Services.Download;

/// <summary>Which UI surface asked for a download — log/telemetry context only.</summary>
public enum DownloadTrigger { Dialog, BrowseQueue, DetailPanel, Waitlist, Pipeline }

/// <summary>One request to put a Civitai model version on disk.</summary>
public sealed record DownloadRequest(
    CivitaiModelVersion Version,
    string TargetDirectory,          // resolved by the caller's picker (LoraPathBuilder-backed)
    DownloadTrigger Trigger)
{
    /// <summary>The file to fetch. Null falls back to <see cref="CivitaiVersionFiles.PickPrimary(CivitaiModelVersion?)"/>.</summary>
    public CivitaiModelFile? File { get; init; }

    /// <summary>Local model row this download belongs to, when the caller already knows it.</summary>
    public int? ExistingModelId { get; init; }

    /// <summary>Overrides the on-disk name; null uses the Civitai file name, then a synthesized one.</summary>
    public string? FileNameOverride { get; init; }

    /// <summary>Name shown in the download coordinator; null uses "Download {fileName}".</summary>
    public string? TaskName { get; init; }
}

/// <summary>Progress of a download, 0–100 plus the transport's own status line.</summary>
public sealed record DownloadProgress(int Percent, string Message);

/// <summary>How a download ended.</summary>
public enum DownloadStatus
{
    /// <summary>Transferred and persisted with metadata.</summary>
    Completed,

    /// <summary>Transferred, but model-page metadata was unavailable ("Done — no metadata").</summary>
    CompletedMetadataIncomplete,

    /// <summary>A byte-identical file was already on disk; no transfer happened.</summary>
    ReusedExisting,

    /// <summary>Transferred but SHA256 did not match; the file is left on disk for inspection.</summary>
    HashMismatch,

    /// <summary>The transfer failed; the transport has already logged the cause.</summary>
    Failed,

    /// <summary>The caller cancelled before the transfer finished.</summary>
    Cancelled
}

/// <summary>The result of a <see cref="ICivitaiModelDownloader.DownloadAsync"/> call.</summary>
public sealed record DownloadOutcome(
    DownloadStatus Status, string? FinalPath, int? ModelId, bool RenamedForCollision, string? Error)
{
    public bool Success => Status
        is DownloadStatus.Completed or DownloadStatus.CompletedMetadataIncomplete or DownloadStatus.ReusedExisting;
}

/// <summary>
/// The one Civitai download path (spec §4.4). Owns: file pick, collision policy, the
/// IDownloadCoordinator enqueue (callers must NOT wrap this call in the coordinator — D3),
/// SHA256 verification, persistence, the Tags+Thumbnails completion sync, and the
/// ILibraryChangeNotifier signal.
/// </summary>
public interface ICivitaiModelDownloader
{
    Task<DownloadOutcome> DownloadAsync(
        DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
}
