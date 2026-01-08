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

    public DatasetBackupService(IAppSettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
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
            return BackupResult.Failed("Automatic backup is not enabled.");
        }

        if (string.IsNullOrWhiteSpace(settings.DatasetStoragePath))
        {
            return BackupResult.Failed("Dataset storage path is not configured.");
        }

        if (!Directory.Exists(settings.DatasetStoragePath))
        {
            return BackupResult.Failed($"Dataset storage path does not exist: {settings.DatasetStoragePath}");
        }

        if (string.IsNullOrWhiteSpace(settings.AutoBackupLocation))
        {
            return BackupResult.Failed("Backup location is not configured.");
        }

        // Ensure backup directory exists
        try
        {
            Directory.CreateDirectory(settings.AutoBackupLocation);
        }
        catch (Exception ex)
        {
            return BackupResult.Failed($"Failed to create backup directory: {ex.Message}");
        }

        // Generate backup filename with timestamp
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var backupFileName = $"DatasetBackup_{timestamp}.zip";
        var backupPath = Path.Combine(settings.AutoBackupLocation, backupFileName);

        Log.Information("Starting dataset backup to {BackupPath}", backupPath);

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
                        // Continue with other files
                    }
                }
            }

            // Update LastBackupAt timestamp
            settings.LastBackupAt = DateTimeOffset.UtcNow;
            await _settingsService.SaveSettingsAsync(settings, cancellationToken);

            progress?.Report(new BackupProgress
            {
                Phase = "Complete",
                ProgressPercent = 100,
                FilesProcessed = processedFiles,
                TotalFiles = totalFiles
            });

            Log.Information("Dataset backup completed: {FilesCount} files, {Size:N0} bytes", 
                processedFiles, totalSize);

            return BackupResult.Succeeded(backupPath, processedFiles, totalSize);
        }
        catch (OperationCanceledException)
        {
            // Clean up partial backup
            if (File.Exists(backupPath))
            {
                try { File.Delete(backupPath); } catch { }
            }
            return BackupResult.Failed("Backup was cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Dataset backup failed");
            
            // Clean up partial backup
            if (File.Exists(backupPath))
            {
                try { File.Delete(backupPath); } catch { }
            }
            
            return BackupResult.Failed($"Backup failed: {ex.Message}");
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
