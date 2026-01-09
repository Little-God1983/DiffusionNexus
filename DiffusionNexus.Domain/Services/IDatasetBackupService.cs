namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Result of a backup operation.
/// </summary>
public class BackupResult
{
    /// <summary>
    /// Whether the backup was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message if the backup failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Path to the backup folder or archive.
    /// </summary>
    public string? BackupPath { get; init; }

    /// <summary>
    /// Number of files backed up.
    /// </summary>
    public int FilesBackedUp { get; init; }

    /// <summary>
    /// Total size of the backup in bytes.
    /// </summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>
    /// When the backup was completed.
    /// </summary>
    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// Creates a successful backup result.
    /// </summary>
    public static BackupResult Succeeded(string backupPath, int filesBackedUp, long totalSizeBytes)
        => new()
        {
            Success = true,
            BackupPath = backupPath,
            FilesBackedUp = filesBackedUp,
            TotalSizeBytes = totalSizeBytes,
            CompletedAt = DateTimeOffset.UtcNow
        };

    /// <summary>
    /// Creates a failed backup result.
    /// </summary>
    public static BackupResult Failed(string errorMessage)
        => new()
        {
            Success = false,
            ErrorMessage = errorMessage,
            CompletedAt = DateTimeOffset.UtcNow
        };
}

/// <summary>
/// Progress information for backup operations.
/// </summary>
public class BackupProgress
{
    /// <summary>
    /// Current phase of the backup.
    /// </summary>
    public string Phase { get; init; } = string.Empty;

    /// <summary>
    /// Current file being processed.
    /// </summary>
    public string? CurrentFile { get; init; }

    /// <summary>
    /// Progress percentage (0-100).
    /// </summary>
    public int ProgressPercent { get; init; }

    /// <summary>
    /// Number of files processed so far.
    /// </summary>
    public int FilesProcessed { get; init; }

    /// <summary>
    /// Total number of files to process.
    /// </summary>
    public int TotalFiles { get; init; }
}

/// <summary>
/// Result of analyzing a backup archive.
/// </summary>
public class BackupAnalysisResult
{
    /// <summary>
    /// Whether the analysis was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message if the analysis failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Path to the backup archive that was analyzed.
    /// </summary>
    public string? BackupPath { get; init; }

    /// <summary>
    /// Date the backup was created (parsed from filename).
    /// </summary>
    public DateTimeOffset? BackupDate { get; init; }

    /// <summary>
    /// Number of datasets in the backup.
    /// </summary>
    public int DatasetCount { get; init; }

    /// <summary>
    /// Number of image files in the backup.
    /// </summary>
    public int ImageCount { get; init; }

    /// <summary>
    /// Number of video files in the backup.
    /// </summary>
    public int VideoCount { get; init; }

    /// <summary>
    /// Number of caption (.txt) files in the backup.
    /// </summary>
    public int CaptionCount { get; init; }

    /// <summary>
    /// Total size of the backup in bytes.
    /// </summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>
    /// Creates a successful analysis result.
    /// </summary>
    public static BackupAnalysisResult Succeeded(
        string backupPath,
        DateTimeOffset? backupDate,
        int datasetCount,
        int imageCount,
        int videoCount,
        int captionCount,
        long totalSizeBytes)
        => new()
        {
            Success = true,
            BackupPath = backupPath,
            BackupDate = backupDate,
            DatasetCount = datasetCount,
            ImageCount = imageCount,
            VideoCount = videoCount,
            CaptionCount = captionCount,
            TotalSizeBytes = totalSizeBytes
        };

    /// <summary>
    /// Creates a failed analysis result.
    /// </summary>
    public static BackupAnalysisResult Failed(string errorMessage)
        => new()
        {
            Success = false,
            ErrorMessage = errorMessage
        };
}

/// <summary>
/// Statistics about the current dataset storage.
/// </summary>
public class CurrentStorageStats
{
    /// <summary>
    /// Number of datasets in the current storage.
    /// </summary>
    public int DatasetCount { get; init; }

    /// <summary>
    /// Number of image files in the current storage.
    /// </summary>
    public int ImageCount { get; init; }

    /// <summary>
    /// Number of video files in the current storage.
    /// </summary>
    public int VideoCount { get; init; }

    /// <summary>
    /// Number of caption (.txt) files in the current storage.
    /// </summary>
    public int CaptionCount { get; init; }

    /// <summary>
    /// Total size of all files in bytes.
    /// </summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>
    /// Current date/time when the stats were gathered.
    /// </summary>
    public DateTimeOffset CurrentDate { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// Result of a restore operation.
/// </summary>
public class RestoreResult
{
    /// <summary>
    /// Whether the restore was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message if the restore failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Number of files restored.
    /// </summary>
    public int FilesRestored { get; init; }

    /// <summary>
    /// When the restore was completed.
    /// </summary>
    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// Creates a successful restore result.
    /// </summary>
    public static RestoreResult Succeeded(int filesRestored)
        => new()
        {
            Success = true,
            FilesRestored = filesRestored,
            CompletedAt = DateTimeOffset.UtcNow
        };

    /// <summary>
    /// Creates a failed restore result.
    /// </summary>
    public static RestoreResult Failed(string errorMessage)
        => new()
        {
            Success = false,
            ErrorMessage = errorMessage,
            CompletedAt = DateTimeOffset.UtcNow
        };
}

/// <summary>
/// Service for backing up and restoring dataset folders.
/// </summary>
public interface IDatasetBackupService
{
    /// <summary>
    /// Performs a backup of all datasets to the configured backup location.
    /// </summary>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the backup operation.</returns>
    Task<BackupResult> BackupDatasetsAsync(
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a backup is due based on the configured interval.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if a backup should be performed.</returns>
    Task<bool> IsBackupDueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the next scheduled backup time.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The next backup time, or null if backup is not configured.</returns>
    Task<DateTimeOffset?> GetNextBackupTimeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes a backup ZIP file to get statistics about its contents.
    /// </summary>
    /// <param name="backupZipPath">Path to the backup ZIP file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Analysis result with counts and metadata.</returns>
    Task<BackupAnalysisResult> AnalyzeBackupAsync(
        string backupZipPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets statistics about the current dataset storage.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Current storage statistics.</returns>
    Task<CurrentStorageStats> GetCurrentStorageStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores datasets from a backup ZIP file.
    /// This will replace the current dataset storage with the backup contents.
    /// </summary>
    /// <param name="backupZipPath">Path to the backup ZIP file to restore.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the restore operation.</returns>
    Task<RestoreResult> RestoreBackupAsync(
        string backupZipPath,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets whether a backup or restore operation is currently in progress.
    /// </summary>
    bool IsOperationInProgress { get; }
}
