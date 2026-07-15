using System.Globalization;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Domain.Services;
using Microsoft.Data.Sqlite;
using Serilog;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Backs up the core user database (<c>Diffusion_Nexus-core.db</c>) via SQLite's online
/// <c>VACUUM INTO</c>, producing a consistent single-file snapshot even while the app has
/// the database open. See <see cref="IDatabaseBackupService"/>.
/// </summary>
public sealed class DatabaseBackupService : IDatabaseBackupService
{
    /// <summary>Pattern used to identify database backup files created by this service.</summary>
    private const string BackupFilePattern = "DatabaseBackup_*.db";

    private readonly IAppSettingsService _settingsService;
    private readonly Func<string> _sourceDbPathProvider;

    /// <param name="settingsService">Provides the backup location and retention count.</param>
    /// <param name="sourceDbPathProvider">
    /// Resolves the live core database file path. Defaults to
    /// <see cref="DiffusionNexusCoreDbContext.GetDatabaseFilePath(string?)"/>; overridable for tests.
    /// </param>
    public DatabaseBackupService(
        IAppSettingsService settingsService,
        Func<string>? sourceDbPathProvider = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _sourceDbPathProvider = sourceDbPathProvider ?? (() => DiffusionNexusCoreDbContext.GetDatabaseFilePath());
    }

    /// <inheritdoc />
    public async Task<BackupResult> BackupDatabaseAsync(
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(settings.AutoBackupLocation))
        {
            return BackupResult.Failed("Backup location is not configured.");
        }

        var sourceDbPath = _sourceDbPathProvider();
        if (string.IsNullOrWhiteSpace(sourceDbPath) || !File.Exists(sourceDbPath))
        {
            return BackupResult.Failed($"Database file not found: {sourceDbPath}");
        }

        try
        {
            Directory.CreateDirectory(settings.AutoBackupLocation);
        }
        catch (Exception ex)
        {
            return BackupResult.Failed($"Failed to create backup directory: {ex.Message}");
        }

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(settings.AutoBackupLocation, $"DatabaseBackup_{timestamp}.db");

        progress?.Report(new BackupProgress { Phase = "Backing up database", ProgressPercent = 0 });
        Log.Information("Starting database backup to {BackupPath}", backupPath);

        try
        {
            // VACUUM INTO runs on a pool thread: it is synchronous SQLite I/O and would
            // otherwise block the caller's thread. A read-only, non-pooled connection reads a
            // consistent snapshot of the live (WAL) database without disturbing the app's own
            // connection or leaving a pooled handle that locks the file afterwards.
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var connection = new SqliteConnection(
                    $"Data Source={sourceDbPath};Mode=ReadOnly;Pooling=False");
                connection.Open();

                using var command = connection.CreateCommand();
                // VACUUM INTO takes a string literal, not a bound parameter; escape embedded quotes.
                var escapedDest = backupPath.Replace("'", "''");
                command.CommandText = $"VACUUM INTO '{escapedDest}'";
                command.ExecuteNonQuery();
            }, cancellationToken).ConfigureAwait(false);

            var sizeBytes = new FileInfo(backupPath).Length;

            progress?.Report(new BackupProgress
            {
                Phase = "Cleaning up old backups",
                ProgressPercent = 95,
                FilesProcessed = 1,
                TotalFiles = 1
            });

            CleanupOldBackups(settings.AutoBackupLocation, settings.MaxBackups);

            progress?.Report(new BackupProgress
            {
                Phase = "Complete",
                ProgressPercent = 100,
                FilesProcessed = 1,
                TotalFiles = 1
            });

            var sizeMb = sizeBytes / 1024.0 / 1024.0;
            Log.Information("Database backup completed: {BackupPath} ({SizeMb:F1} MB)", backupPath, sizeMb);
            return BackupResult.Succeeded(backupPath, filesBackedUp: 1, totalSizeBytes: sizeBytes);
        }
        catch (OperationCanceledException)
        {
            TryDeletePartial(backupPath);
            return BackupResult.Failed("Database backup was cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Database backup failed");
            TryDeletePartial(backupPath);
            return BackupResult.Failed($"Database backup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes oldest database backups when the count exceeds <paramref name="maxBackups"/>.
    /// Retention is applied independently of the dataset-image backups (separate file pattern).
    /// </summary>
    private static void CleanupOldBackups(string backupLocation, int maxBackups)
    {
        if (maxBackups <= 0)
        {
            return;
        }

        try
        {
            var backupFiles = Directory.GetFiles(backupLocation, BackupFilePattern)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            foreach (var file in backupFiles.Skip(maxBackups))
            {
                try
                {
                    file.Delete();
                    Log.Information("Deleted old database backup: {FileName}", file.Name);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete old database backup: {FileName}", file.Name);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clean up old database backups");
        }
    }

    private static void TryDeletePartial(string backupPath)
    {
        if (File.Exists(backupPath))
        {
            try { File.Delete(backupPath); } catch { /* best-effort cleanup */ }
        }
    }
}
