using System.Globalization;
using System.IO.Compression;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Service for backing up and restoring dataset folders.
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

    /// <summary>
    /// Image file extensions to count.
    /// </summary>
    private static readonly HashSet<string> ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

    /// <summary>
    /// Video file extensions to count.
    /// </summary>
    private static readonly HashSet<string> VideoExtensions = [".mp4", ".webm", ".mov", ".avi", ".mkv"];

    private bool _isOperationInProgress;

    /// <inheritdoc />
    public bool IsOperationInProgress => _isOperationInProgress;

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
        if (_isOperationInProgress)
        {
            return BackupResult.Failed("A backup or restore operation is already in progress.");
        }

        _isOperationInProgress = true;
        try
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
        finally
        {
            _isOperationInProgress = false;
        }
    }

    /// <summary>
    /// Deletes oldest backup files if the total count exceeds maxBackups.
    /// </summary>
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

    /// <inheritdoc />
    public Task<BackupAnalysisResult> AnalyzeBackupAsync(
        string backupZipPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backupZipPath);

        if (!File.Exists(backupZipPath))
        {
            return Task.FromResult(BackupAnalysisResult.Failed($"Backup file not found: {backupZipPath}"));
        }

        try
        {
            var backupDate = ParseBackupDateFromFilename(backupZipPath);
            var datasets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var imageCount = 0;
            var videoCount = 0;
            var captionCount = 0;
            long totalSize = 0;

            using var archive = ZipFile.OpenRead(backupZipPath);
            
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(entry.Name))
                    continue; // Skip directories

                totalSize += entry.Length;

                // Extract dataset name from path (first directory component)
                var parts = entry.FullName.Split('/', '\\');
                if (parts.Length > 0 && !string.IsNullOrEmpty(parts[0]))
                {
                    datasets.Add(parts[0]);
                }

                // Categorize by extension
                var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                
                if (ImageExtensions.Contains(ext))
                {
                    imageCount++;
                }
                else if (VideoExtensions.Contains(ext))
                {
                    videoCount++;
                }
                else if (ext == ".txt")
                {
                    captionCount++;
                }
            }

            return Task.FromResult(BackupAnalysisResult.Succeeded(
                backupZipPath,
                backupDate,
                datasets.Count,
                imageCount,
                videoCount,
                captionCount,
                totalSize));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to analyze backup: {BackupPath}", backupZipPath);
            return Task.FromResult(BackupAnalysisResult.Failed($"Failed to analyze backup: {ex.Message}"));
        }
    }

    /// <inheritdoc />
    public async Task<CurrentStorageStats> GetCurrentStorageStatsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(settings.DatasetStoragePath) || !Directory.Exists(settings.DatasetStoragePath))
        {
            return new CurrentStorageStats();
        }

        var datasets = Directory.GetDirectories(settings.DatasetStoragePath);
        var imageCount = 0;
        var videoCount = 0;
        var captionCount = 0;
        long totalSize = 0;

        var allFiles = Directory.EnumerateFiles(settings.DatasetStoragePath, "*", SearchOption.AllDirectories);
        
        foreach (var file in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileInfo = new FileInfo(file);
            totalSize += fileInfo.Length;

            var ext = Path.GetExtension(file).ToLowerInvariant();
            
            if (ImageExtensions.Contains(ext))
            {
                imageCount++;
            }
            else if (VideoExtensions.Contains(ext))
            {
                videoCount++;
            }
            else if (ext == ".txt")
            {
                captionCount++;
            }
        }

        return new CurrentStorageStats
        {
            DatasetCount = datasets.Length,
            ImageCount = imageCount,
            VideoCount = videoCount,
            CaptionCount = captionCount,
            TotalSizeBytes = totalSize,
            CurrentDate = DateTimeOffset.Now
        };
    }

    /// <inheritdoc />
    public async Task<RestoreResult> RestoreBackupAsync(
        string backupZipPath,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backupZipPath);

        if (_isOperationInProgress)
        {
            return RestoreResult.Failed("A backup or restore operation is already in progress.");
        }

        if (!File.Exists(backupZipPath))
        {
            return RestoreResult.Failed($"Backup file not found: {backupZipPath}");
        }

        _isOperationInProgress = true;
        try
        {
            var settings = await _settingsService.GetSettingsAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(settings.DatasetStoragePath))
            {
                return RestoreResult.Failed("Dataset storage path is not configured.");
            }

            Log.Information("Starting backup restore from {BackupPath} to {StoragePath}", 
                backupZipPath, settings.DatasetStoragePath);
            
            using var operation = _activityLog?.StartOperation("Restoring backup", "Restore", isCancellable: false);

            progress?.Report(new BackupProgress
            {
                Phase = "Preparing",
                ProgressPercent = 0
            });

            try
            {
                // Clear existing storage (but keep the folder)
                if (Directory.Exists(settings.DatasetStoragePath))
                {
                    progress?.Report(new BackupProgress
                    {
                        Phase = "Clearing existing data",
                        ProgressPercent = 5
                    });

                    var existingDirs = Directory.GetDirectories(settings.DatasetStoragePath);
                    var existingFiles = Directory.GetFiles(settings.DatasetStoragePath);

                    foreach (var dir in existingDirs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Directory.Delete(dir, recursive: true);
                    }

                    foreach (var file in existingFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        File.Delete(file);
                    }
                }
                else
                {
                    Directory.CreateDirectory(settings.DatasetStoragePath);
                }

                progress?.Report(new BackupProgress
                {
                    Phase = "Extracting backup",
                    ProgressPercent = 10
                });

                // Count entries for progress
                int totalEntries;
                using (var countArchive = ZipFile.OpenRead(backupZipPath))
                {
                    totalEntries = countArchive.Entries.Count(e => !string.IsNullOrEmpty(e.Name));
                }

                var extractedCount = 0;

                using (var archive = ZipFile.OpenRead(backupZipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (string.IsNullOrEmpty(entry.Name))
                            continue; // Skip directory entries

                        var destPath = Path.Combine(settings.DatasetStoragePath, entry.FullName);
                        var destDir = Path.GetDirectoryName(destPath);

                        if (!string.IsNullOrEmpty(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }

                        entry.ExtractToFile(destPath, overwrite: true);
                        extractedCount++;

                        if (extractedCount % 10 == 0 || extractedCount == totalEntries)
                        {
                            var percent = 10 + (int)(85.0 * extractedCount / totalEntries);
                            operation?.ReportProgress(percent, $"Extracting file {extractedCount} of {totalEntries}");
                            progress?.Report(new BackupProgress
                            {
                                Phase = "Extracting backup",
                                CurrentFile = entry.FullName,
                                ProgressPercent = percent,
                                FilesProcessed = extractedCount,
                                TotalFiles = totalEntries
                            });
                        }
                    }
                }

                progress?.Report(new BackupProgress
                {
                    Phase = "Complete",
                    ProgressPercent = 100,
                    FilesProcessed = extractedCount,
                    TotalFiles = totalEntries
                });

                Log.Information("Backup restore completed: {FilesCount} files extracted", extractedCount);
                _activityLog?.LogSuccess("Restore", $"Restored {extractedCount} files from backup");

                return RestoreResult.Succeeded(extractedCount);
            }
            catch (OperationCanceledException)
            {
                _activityLog?.LogWarning("Restore", "Restore was cancelled");
                return RestoreResult.Failed("Restore was cancelled.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Backup restore failed");
                _activityLog?.LogError("Restore", "Restore failed", ex);
                return RestoreResult.Failed($"Restore failed: {ex.Message}");
            }
        }
        finally
        {
            _isOperationInProgress = false;
        }
    }

    /// <summary>
    /// Parses the backup date from the filename pattern "DatasetBackup_yyyy-MM-dd_HH-mm-ss.zip".
    /// </summary>
    private static DateTimeOffset? ParseBackupDateFromFilename(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        
        // Expected format: DatasetBackup_2024-01-15_14-30-00
        if (!fileName.StartsWith("DatasetBackup_"))
        {
            return null;
        }

        var datePart = fileName["DatasetBackup_".Length..];
        
        if (DateTime.TryParseExact(
                datePart, 
                "yyyy-MM-dd_HH-mm-ss", 
                CultureInfo.InvariantCulture, 
                DateTimeStyles.None, 
                out var result))
        {
            return new DateTimeOffset(result, TimeSpan.Zero);
        }

        return null;
    }
}
