namespace DiffusionNexus.Domain.Services;

/// <summary>
/// App-level scheduler that owns the automatic-backup timer and orchestrates a backup pass across
/// the enabled payloads — dataset images (zipped) and the core user database (copied). Lifted out of
/// the LoRA Dataset Helper so backups run regardless of which module is open. Runs on a background
/// thread and narrates each step to the Unified Console.
/// </summary>
public interface IBackupScheduler
{
    /// <summary>
    /// Starts the scheduler: runs a due-check immediately and arms the periodic timer. Idempotent —
    /// calling it more than once has no additional effect.
    /// </summary>
    void Start();

    /// <summary>
    /// Runs a backup pass immediately, backing up whichever payloads are enabled in settings
    /// (used by the Settings "Backup Now" button). Safe to call from the UI thread — the work is
    /// offloaded to a background thread.
    /// </summary>
    /// <returns>A combined result: success only when every attempted payload succeeded.</returns>
    Task<BackupResult> RunBackupNowAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The next scheduled automatic backup time, or <c>null</c> when no backup type is enabled or the
    /// backup location is not configured.
    /// </summary>
    Task<DateTimeOffset?> GetNextBackupTimeAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether a backup pass is currently running.</summary>
    bool IsRunning { get; }

    /// <summary>Raised when <see cref="IsRunning"/> changes, so the UI can refresh backup state.</summary>
    event EventHandler? StateChanged;
}
