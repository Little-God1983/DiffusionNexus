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
/// Service for backing up dataset folders.
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
}
