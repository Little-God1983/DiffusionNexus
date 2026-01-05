using System.Security.Cryptography;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Service for synchronizing local model files with the database.
/// Implements a database-first approach with background verification.
/// </summary>
public class ModelFileSyncService : IModelSyncService
{
    private readonly DiffusionNexusCoreDbContext _dbContext;
    private readonly IAppSettingsService _settingsService;

    /// <summary>
    /// Number of bytes to read for partial hash (10MB).
    /// </summary>
    private const int PartialHashBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Supported model file extensions.
    /// </summary>
    private static readonly string[] ModelExtensions = [".safetensors", ".pt", ".ckpt", ".pth"];

    public ModelFileSyncService(DiffusionNexusCoreDbContext dbContext, IAppSettingsService settingsService)
    {
        _dbContext = dbContext;
        _settingsService = settingsService;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Model>> LoadCachedModelsAsync(CancellationToken cancellationToken = default)
    {
        // Load ALL models that have been saved (simplified query for debugging)
        var models = await _dbContext.Models
            .Include(m => m.Creator)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Files)
            .Include(m => m.Versions)
                .ThenInclude(v => v.Images)
            .Include(m => m.Versions)
                .ThenInclude(v => v.TriggerWords)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        // Filter in memory to ensure we get models with local files
        var modelsWithLocalFiles = models
            .Where(m => m.Versions.Any(v => v.Files.Any(f => !string.IsNullOrEmpty(f.LocalPath))))
            .ToList();

        return modelsWithLocalFiles;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Model>> DiscoverNewFilesAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new SyncProgress
        {
            Phase = "Getting configured folders"
        });

        // Get enabled source folders
        var sourceFolders = await _settingsService.GetEnabledLoraSourcesAsync(cancellationToken);
        
        progress?.Report(new SyncProgress
        {
            Phase = $"Found {sourceFolders.Count} configured folders"
        });
        
        if (sourceFolders.Count == 0)
        {
            progress?.Report(new SyncProgress
            {
                Phase = "No source folders configured - add folders in Settings"
            });
            return [];
        }

        progress?.Report(new SyncProgress
        {
            Phase = "Scanning for model files",
            TotalCount = sourceFolders.Count
        });

        // Get all existing local paths from database
        var existingPaths = await _dbContext.ModelFiles
            .Where(f => f.LocalPath != null)
            .Select(f => f.LocalPath!)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, cancellationToken);

        // Scan all folders for model files
        var allFiles = new List<string>();
        foreach (var folder in sourceFolders)
        {
            if (Directory.Exists(folder))
            {
                var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                    .Where(f => ModelExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
                allFiles.AddRange(files);
                
                progress?.Report(new SyncProgress
                {
                    Phase = $"Scanned {folder}",
                    CurrentItem = $"Found {allFiles.Count} files so far"
                });
            }
            else
            {
                progress?.Report(new SyncProgress
                {
                    Phase = $"Folder not found: {folder}"
                });
            }
        }

        // Filter to only new files
        var newFiles = allFiles
            .Where(f => !existingPaths.Contains(f))
            .ToList();

        progress?.Report(new SyncProgress
        {
            Phase = $"Found {allFiles.Count} total files, {newFiles.Count} are new"
        });

        if (newFiles.Count == 0)
        {
            return [];
        }

        progress?.Report(new SyncProgress
        {
            Phase = "Processing new files",
            TotalCount = newFiles.Count
        });

        var newModels = new List<Model>();
        var processedCount = 0;

        foreach (var filePath in newFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(filePath);
            progress?.Report(new SyncProgress
            {
                Phase = "Processing new files",
                CurrentItem = fileName,
                ProcessedCount = processedCount,
                TotalCount = newFiles.Count
            });

            // First check if we can match by hash (file was moved)
            var fileInfo = new FileInfo(filePath);
            var matchedFile = await TryMatchByHashAndSizeAsync(filePath, fileInfo.Length, cancellationToken);

            if (matchedFile is not null)
            {
                // Update the existing file's path
                matchedFile.LocalPath = filePath;
                matchedFile.IsLocalFileValid = true;
                matchedFile.LocalFileVerifiedAt = DateTimeOffset.UtcNow;
                // The model already exists, we just updated the path
            }
            else
            {
                // Create new model entry
                var model = CreateModelFromFile(filePath, fileInfo);
                _dbContext.Models.Add(model);
                newModels.Add(model);
            }

            processedCount++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        progress?.Report(new SyncProgress
        {
            Phase = "Discovery complete",
            ProcessedCount = newFiles.Count,
            TotalCount = newFiles.Count
        });

        return newModels;
    }

    /// <inheritdoc />
    public async Task<FileSyncResult> VerifyAndSyncFilesAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new SyncProgress
        {
            Phase = "Loading files to verify"
        });

        // Get all files with local paths
        var files = await _dbContext.ModelFiles
            .Where(f => f.LocalPath != null)
            .ToListAsync(cancellationToken);

        if (files.Count == 0)
        {
            return new FileSyncResult();
        }

        var verified = 0;
        var missing = 0;
        var moved = 0;
        var processedCount = 0;

        progress?.Report(new SyncProgress
        {
            Phase = "Verifying files",
            TotalCount = files.Count
        });

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new SyncProgress
            {
                Phase = "Verifying files",
                CurrentItem = file.FileName,
                ProcessedCount = processedCount,
                TotalCount = files.Count
            });

            if (File.Exists(file.LocalPath))
            {
                file.IsLocalFileValid = true;
                file.LocalFileVerifiedAt = DateTimeOffset.UtcNow;
                verified++;
            }
            else
            {
                // File is missing - try to find by hash
                var newPath = await TryFindMovedFileAsync(file, cancellationToken);
                if (newPath is not null)
                {
                    file.LocalPath = newPath;
                    file.IsLocalFileValid = true;
                    file.LocalFileVerifiedAt = DateTimeOffset.UtcNow;
                    moved++;
                }
                else
                {
                    file.IsLocalFileValid = false;
                    file.LocalFileVerifiedAt = DateTimeOffset.UtcNow;
                    missing++;
                }
            }

            processedCount++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        progress?.Report(new SyncProgress
        {
            Phase = "Verification complete",
            ProcessedCount = files.Count,
            TotalCount = files.Count
        });

        return new FileSyncResult
        {
            VerifiedCount = verified,
            MissingCount = missing,
            MovedCount = moved
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Model>> FullSyncAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Phase 1: Load cached (fast)
        progress?.Report(new SyncProgress { Phase = "Loading cached models" });
        var cachedModels = await LoadCachedModelsAsync(cancellationToken);

        // Phase 2: Discover new files
        var newModels = await DiscoverNewFilesAsync(progress, cancellationToken);

        // Phase 3: Verify existing (background - can be slow)
        _ = Task.Run(async () =>
        {
            try
            {
                await VerifyAndSyncFilesAsync(progress, CancellationToken.None);
            }
            catch
            {
                // Log but don't throw - this is background work
            }
        }, CancellationToken.None);

        // Combine and return all models
        var allModels = cachedModels.Concat(newModels).ToList();
        return allModels;
    }

    /// <inheritdoc />
    public async Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        using var sha256 = SHA256.Create();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);

        // For large files, only hash first 10MB for performance
        var fileSize = stream.Length;
        if (fileSize > PartialHashBytes)
        {
            var buffer = new byte[PartialHashBytes];
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, PartialHashBytes), cancellationToken);
            var hash = sha256.ComputeHash(buffer, 0, bytesRead);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        else
        {
            var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    /// <summary>
    /// Tries to match a file by hash and size to find moved files.
    /// </summary>
    private async Task<ModelFile?> TryMatchByHashAndSizeAsync(string filePath, long fileSize, CancellationToken cancellationToken)
    {
        // First try to match by exact size (fast check)
        var candidatesBySize = await _dbContext.ModelFiles
            .Where(f => f.FileSizeBytes == fileSize && f.LocalPath != null && !f.IsLocalFileValid)
            .ToListAsync(cancellationToken);

        if (candidatesBySize.Count == 0)
        {
            return null;
        }

        // If we have candidates, compute hash and try to match
        var fileHash = await ComputeFileHashAsync(filePath, cancellationToken);

        // Try to find by SHA256 hash
        var matchByHash = candidatesBySize.FirstOrDefault(f =>
            string.Equals(f.HashSHA256, fileHash, StringComparison.OrdinalIgnoreCase));

        return matchByHash;
    }

    /// <summary>
    /// Tries to find a moved file by scanning configured folders.
    /// </summary>
    private async Task<string?> TryFindMovedFileAsync(ModelFile file, CancellationToken cancellationToken)
    {
        // Only try if we have hash or size info
        if (string.IsNullOrEmpty(file.HashSHA256) && !file.FileSizeBytes.HasValue)
        {
            return null;
        }

        var sourceFolders = await _settingsService.GetEnabledLoraSourcesAsync(cancellationToken);

        foreach (var folder in sourceFolders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            // Search for files with matching name first (common case: file renamed in same location)
            var matchingFiles = Directory.EnumerateFiles(folder, file.FileName, SearchOption.AllDirectories);

            foreach (var candidatePath in matchingFiles)
            {
                var candidateInfo = new FileInfo(candidatePath);

                // Quick size check
                if (file.FileSizeBytes.HasValue && candidateInfo.Length != file.FileSizeBytes.Value)
                {
                    continue;
                }

                // Hash check for confirmation
                if (!string.IsNullOrEmpty(file.HashSHA256))
                {
                    var candidateHash = await ComputeFileHashAsync(candidatePath, cancellationToken);
                    if (string.Equals(candidateHash, file.HashSHA256, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidatePath;
                    }
                }
                else
                {
                    // If no hash, trust size match with same filename
                    return candidatePath;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a new Model entity from a local file.
    /// </summary>
    private static Model CreateModelFromFile(string filePath, FileInfo fileInfo)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);

        var model = new Model
        {
            Name = fileName,
            Type = ModelType.LORA,
            Source = DataSource.LocalFile,
            CreatedAt = fileInfo.CreationTimeUtc
        };

        var version = new ModelVersion
        {
            Name = fileName,
            BaseModelRaw = "???", // Unknown without metadata
            BaseModel = BaseModelType.Other,
            CreatedAt = fileInfo.CreationTimeUtc,
            Model = model
        };

        var modelFile = new ModelFile
        {
            FileName = fileInfo.Name,
            LocalPath = filePath,
            SizeKB = fileInfo.Length / 1024.0,
            FileSizeBytes = fileInfo.Length,
            Format = GetFileFormat(fileInfo.Extension),
            IsPrimary = true,
            IsLocalFileValid = true,
            LocalFileVerifiedAt = DateTimeOffset.UtcNow,
            ModelVersion = version
        };

        version.Files.Add(modelFile);
        model.Versions.Add(version);

        return model;
    }

    private static FileFormat GetFileFormat(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".safetensors" => FileFormat.SafeTensor,
            ".pt" => FileFormat.PickleTensor,
            ".ckpt" => FileFormat.Other,
            ".pth" => FileFormat.PickleTensor,
            _ => FileFormat.Unknown
        };
    }
}

/// <summary>
/// Extension method for async HashSet creation.
/// </summary>
internal static class AsyncEnumerableExtensions
{
    public static async Task<HashSet<T>> ToHashSetAsync<T>(
        this IQueryable<T> source,
        IEqualityComparer<T>? comparer,
        CancellationToken cancellationToken = default)
    {
        var list = await source.ToListAsync(cancellationToken);
        return new HashSet<T>(list, comparer);
    }
}
