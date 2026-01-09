using System.IO.Compression;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Service for backing up dataset folders.
/// Creates timestamped ZIP archives of the dataset storage folder.
/// </summary>
public class DatasetBackupService : IDatasetBackupService
{
    private readonly IAppSettingsService _settingsService;
    private readonly IActivityLogService? _activityLog;

    /// <summary>
    /// Pattern used to identify backup files created by this service.
    /// </summary>
    private const string BackupFilePattern = "DatasetBackup_*.zip";

    public DatasetBackupService(IAppSettingsService settingsService, IActivityLogService? activityLog = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _activityLog = activityLog;
    }

    /// <inheritdoc />
    public async Task<BackupResult> BackupDatasetsAsync(
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync(cancellationToken);

        // Validate configuration
        if (!settings.AutoBackupEnabled)
        {
            _activityLog?.LogWarning("Backup", "Automatic backup is not enabled");
            return BackupResult.Failed("Automatic backup is not enabled.");
        }

        if (string.IsNullOrWhiteSpace(settings.DatasetStoragePath))
        {
            _activityLog?.LogError("Backup", "Dataset storage path is not configured");
            return BackupResult.Failed("Dataset storage path is not configured.");
        }

        if (!Directory.Exists(settings.DatasetStoragePath))
        {
            _activityLog?.LogError("Backup", $"Dataset storage path does not exist: {settings.DatasetStoragePath}");
            return BackupResult.Failed($"Dataset storage path does not exist: {settings.DatasetStoragePath}");
        }

        if (string.IsNullOrWhiteSpace(settings.AutoBackupLocation))
        {
            _activityLog?.LogError("Backup", "Backup location is not configured");
            return BackupResult.Failed("Backup location is not configured.");
        }

        // Ensure backup directory exists
        try
        {
            Directory.CreateDirectory(settings.AutoBackupLocation);
        }
        catch (Exception ex)
        {
            _activityLog?.LogError("Backup", $"Failed to create backup directory", ex);
            return BackupResult.Failed($"Failed to create backup directory: {ex.Message}");
        }

        // Generate backup filename with timestamp
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var backupFileName = $"DatasetBackup_{timestamp}.zip";
        var backupPath = Path.Combine(settings.AutoBackupLocation, backupFileName);

        Log.Information("Starting dataset backup to {BackupPath}", backupPath);
        
        // Start tracked operation for progress display
        using var operation = _activityLog?.StartOperation("Backing up datasets", "Backup", isCancellable: false);

        progress?.Report(new BackupProgress
        {
            Phase = "Scanning",
            ProgressPercent = 0
        });

        try
        {
            // Count files first for progress reporting
            var allFiles = Directory.EnumerateFiles(settings.DatasetStoragePath, "*", SearchOption.AllDirectories)
                .ToList();
            
            var totalFiles = allFiles.Count;
            var processedFiles = 0;
            long totalSize = 0;

            _activityLog?.LogInfo("Backup", $"Found {totalFiles} files to backup");
            operation?.ReportProgress(5, $"Found {totalFiles} files");

            progress?.Report(new BackupProgress
            {
                Phase = "Creating backup",
                ProgressPercent = 5,
                TotalFiles = totalFiles
            });

            // Create ZIP archive
            using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
            {
                foreach (var filePath in allFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var relativePath = Path.GetRelativePath(settings.DatasetStoragePath, filePath);
                    var fileInfo = new FileInfo(filePath);
                    totalSize += fileInfo.Length;

                    try
                    {
                        archive.CreateEntryFromFile(filePath, relativePath, CompressionLevel.Optimal);
                        processedFiles++;

                        if (processedFiles % 10 == 0 || processedFiles == totalFiles)
                        {
                            var percent = 5 + (int)(90.0 * processedFiles / totalFiles);
                            operation?.ReportProgress(percent, $"Backing up file {processedFiles} of {totalFiles}");
                            progress?.Report(new BackupProgress
                            {
                                Phase = "Creating backup",
                                CurrentFile = relativePath,
                                ProgressPercent = percent,
                                FilesProcessed = processedFiles,
                                TotalFiles = totalFiles
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to add file to backup: {FilePath}", filePath);
                        _activityLog?.LogWarning("Backup", $"Skipped file: {Path.GetFileName(filePath)}");
                        // Continue with other files
                    }
                }
            }

            // Update only the LastBackupAt timestamp
            await _settingsService.UpdateLastBackupAtAsync(DateTimeOffset.UtcNow, cancellationToken);

            operation?.ReportProgress(98, "Cleaning up old backups");
            progress?.Report(new BackupProgress
            {
                Phase = "Cleaning up old backups",
                ProgressPercent = 98,
                FilesProcessed = processedFiles,
                TotalFiles = totalFiles
            });

            // Delete oldest backups if we exceed MaxBackups
            var deletedCount = CleanupOldBackups(settings.AutoBackupLocation, settings.MaxBackups);
            if (deletedCount > 0)
            {
                _activityLog?.LogInfo("Backup", $"Removed {deletedCount} old backup(s)");
            }

            progress?.Report(new BackupProgress
            {
                Phase = "Complete",
                ProgressPercent = 100,
                FilesProcessed = processedFiles,
                TotalFiles = totalFiles
            });

            var sizeInMb = totalSize / 1024.0 / 1024.0;
            Log.Information("Dataset backup completed: {FilesCount} files, {Size:N0} bytes", 
                processedFiles, totalSize);
            _activityLog?.LogSuccess("Backup", $"Backup completed: {processedFiles} files ({sizeInMb:F1} MB)");

            return BackupResult.Succeeded(backupPath, processedFiles, totalSize);
        }
        catch (OperationCanceledException)
        {
            // Clean up partial backup
            if (File.Exists(backupPath))
            {
                try { File.Delete(backupPath); } catch { }
            }
            _activityLog?.LogWarning("Backup", "Backup was cancelled");
            return BackupResult.Failed("Backup was cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Dataset backup failed");
            _activityLog?.LogError("Backup", "Backup failed", ex);
            
            // Clean up partial backup
            if (File.Exists(backupPath))
            {
                try { File.Delete(backupPath); } catch { }
            }
            
            return BackupResult.Failed($"Backup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes oldest backup files if the total count exceeds maxBackups.
    /// </summary>
    /// <param name="backupLocation">The backup folder path.</param>
    /// <param name="maxBackups">Maximum number of backups to keep.</param>
    /// <returns>Number of backups deleted.</returns>
    private int CleanupOldBackups(string backupLocation, int maxBackups)
    {
        if (maxBackups <= 0)
        {
            return 0;
        }

        try
        {
            var backupFiles = Directory.GetFiles(backupLocation, BackupFilePattern)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            if (backupFiles.Count <= maxBackups)
            {
                return 0;
            }

            // Delete oldest files beyond the limit
            var filesToDelete = backupFiles.Skip(maxBackups).ToList();
            foreach (var file in filesToDelete)
            {
                try
                {
                    file.Delete();
                    Log.Information("Deleted old backup: {FileName}", file.Name);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete old backup: {FileName}", file.Name);
                }
            }

            Log.Information("Cleaned up {Count} old backups, keeping {MaxBackups} most recent", 
                filesToDelete.Count, maxBackups);
            
            return filesToDelete.Count;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to cleanup old backups");
            return 0;
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsBackupDueAsync(CancellationToken cancellationToken = default)
    {
        var nextBackupTime = await GetNextBackupTimeAsync(cancellationToken);
        return nextBackupTime.HasValue && nextBackupTime.Value <= DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetNextBackupTimeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync(cancellationToken);

        if (!settings.AutoBackupEnabled ||
            string.IsNullOrWhiteSpace(settings.AutoBackupLocation) ||
            string.IsNullOrWhiteSpace(settings.DatasetStoragePath))
        {
            return null;
        }

        var intervalTicks = TimeSpan.FromDays(settings.AutoBackupIntervalDays).Ticks
                          + TimeSpan.FromHours(settings.AutoBackupIntervalHours).Ticks;
        var interval = TimeSpan.FromTicks(intervalTicks);

        if (interval.TotalMinutes < 1)
        {
            interval = TimeSpan.FromHours(1); // Minimum 1 hour
        }

        var lastBackup = settings.LastBackupAt ?? DateTimeOffset.MinValue;
        return lastBackup + interval;
    }
}
