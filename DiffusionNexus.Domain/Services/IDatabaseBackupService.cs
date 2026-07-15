namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Service for backing up the core user database (<c>Diffusion_Nexus-core.db</c>).
/// Produces a consistent, timestamped copy in the configured backup location.
/// Restore is intentionally not offered here — a live WAL database cannot be safely
/// replaced while the app holds it open, so restore is a manual copy performed while
/// the application is closed (see the Settings info box).
/// </summary>
public interface IDatabaseBackupService
{
    /// <summary>
    /// Creates a consistent, timestamped snapshot of the core database in the configured
    /// backup location using SQLite's online <c>VACUUM INTO</c> — safe to run while the
    /// application has the database open (unlike a plain file copy of a WAL database).
    /// Reuses <see cref="BackupResult"/>/<see cref="BackupProgress"/> from the dataset backup contract.
    /// </summary>
    /// <param name="progress">Optional progress reporter (phase + percent).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result describing the backup file, size and success/failure.</returns>
    Task<BackupResult> BackupDatabaseAsync(
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
