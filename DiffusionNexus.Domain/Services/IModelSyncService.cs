namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Result of a file sync operation.
/// </summary>
public record FileSyncResult
{
    /// <summary>Files that were already in the database and verified on disk.</summary>
    public int VerifiedCount { get; init; }

    /// <summary>New files discovered on disk and added to database.</summary>
    public int NewFilesCount { get; init; }

    /// <summary>Files in database that are no longer found on disk.</summary>
    public int MissingCount { get; init; }

    /// <summary>Files that were moved and their paths were updated.</summary>
    public int MovedCount { get; init; }

    /// <summary>Total files processed.</summary>
    public int TotalProcessed => VerifiedCount + NewFilesCount + MissingCount + MovedCount;
}

/// <summary>
/// Progress information for sync operations.
/// </summary>
public record SyncProgress
{
    /// <summary>Current phase of the sync operation.</summary>
    public required string Phase { get; init; }

    /// <summary>Current item being processed.</summary>
    public string? CurrentItem { get; init; }

    /// <summary>Number of items processed so far.</summary>
    public int ProcessedCount { get; init; }

    /// <summary>Total number of items to process (if known).</summary>
    public int? TotalCount { get; init; }

    /// <summary>Progress percentage (0-100) if calculable.</summary>
    public int? ProgressPercent => TotalCount > 0 ? (ProcessedCount * 100 / TotalCount) : null;
}

/// <summary>
/// Service for synchronizing local model files with the database.
/// Handles discovery of new files, verification of existing files,
/// and detection/resolution of moved files.
/// </summary>
public interface IModelSyncService
{
    /// <summary>
    /// Loads all models from the database that have valid local files.
    /// This is the fast path for displaying cached data immediately.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Models with local files from the database.</returns>
    Task<IReadOnlyList<Entities.Model>> LoadCachedModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans configured source folders for new safetensor files not yet in the database.
    /// Creates minimal Model/ModelVersion/ModelFile entries for discovered files.
    /// </summary>
    /// <param name="progress">Progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Newly discovered models.</returns>
    Task<IReadOnlyList<Entities.Model>> DiscoverNewFilesAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that files in the database still exist at their recorded paths.
    /// For missing files, attempts to find them by hash/size match.
    /// Updates IsLocalFileValid and LocalFileVerifiedAt accordingly.
    /// </summary>
    /// <param name="progress">Progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Summary of sync results.</returns>
    Task<FileSyncResult> VerifyAndSyncFilesAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a full sync: load cached, discover new, verify existing.
    /// Returns all models (both cached and newly discovered).
    /// </summary>
    /// <param name="progress">Progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All models with local files.</returns>
    Task<IReadOnlyList<Entities.Model>> FullSyncAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes and stores the SHA256 hash for a file.
    /// Uses first 10MB of file for performance on large files.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The computed hash.</returns>
    Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken = default);
}
