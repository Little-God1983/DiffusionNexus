using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Utilities;

public sealed class DatasetImportResult
{
    public bool Cancelled { get; init; }
    public int Copied { get; init; }
    public int Overridden { get; init; }
    public int Renamed { get; init; }
    public int Ignored { get; init; }
    public IReadOnlyList<string> ProcessedSourceFiles { get; init; } = [];

    public int TotalAdded => Copied + Overridden + Renamed;

    public static DatasetImportResult CancelledResult() => new() { Cancelled = true };
}

/// <summary>
/// Imports files into a dataset folder, handling conflict detection, resolution, and file operations.
/// </summary>
public sealed class DatasetFileImporter
{
    private readonly IFileOperations _fileOps;

    public DatasetFileImporter(IFileOperations fileOperations)
    {
        ArgumentNullException.ThrowIfNull(fileOperations);
        _fileOps = fileOperations;
    }

    /// <summary>
    /// Imports source files into the destination folder, showing a conflict dialog when needed.
    /// </summary>
    public async Task<DatasetImportResult> ImportWithDialogAsync(
        IEnumerable<string> sourceFiles,
        string destinationFolder,
        IDialogService dialogService,
        IVideoThumbnailService? videoThumbnailService,
        bool moveFiles)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        ArgumentNullException.ThrowIfNull(dialogService);

        var sourceList = sourceFiles
            .Where(_fileOps.FileExists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sourceList.Count == 0)
        {
            return new DatasetImportResult();
        }

        _fileOps.CreateDirectory(destinationFolder);

        var existingFileNames = _fileOps.GetFiles(destinationFolder)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var conflictResult = FileConflictDetector.DetectConflicts(
            sourceList,
            existingFileNames,
            destinationFolder);

        FileConflictResolutionResult? resolution = null;
        if (conflictResult.Conflicts.Count > 0)
        {
            resolution = await dialogService.ShowFileConflictDialogAsync(
                conflictResult.Conflicts,
                conflictResult.NonConflictingFiles);

            if (resolution is null || !resolution.Confirmed)
            {
                return DatasetImportResult.CancelledResult();
            }
        }

        return await ImportResolvedAsync(
            conflictResult.NonConflictingFiles,
            resolution,
            destinationFolder,
            videoThumbnailService,
            moveFiles);
    }

    /// <summary>
    /// Imports files using pre-resolved conflict decisions.
    /// </summary>
    public async Task<DatasetImportResult> ImportResolvedAsync(
        IEnumerable<string> nonConflictingFiles,
        FileConflictResolutionResult? conflictResolutions,
        string destinationFolder,
        IVideoThumbnailService? videoThumbnailService,
        bool moveFiles)
    {
        ArgumentNullException.ThrowIfNull(nonConflictingFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);

        var processedSources = new List<string>();
        var copied = 0;
        var overridden = 0;
        var renamed = 0;
        var ignored = 0;

        // Track filenames used in this batch to prevent intra-batch collisions.
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceFile in nonConflictingFiles)
        {
            var fileName = Path.GetFileName(sourceFile);
            var destPath = Path.Combine(destinationFolder, fileName);

            if (!usedFileNames.Add(fileName))
            {
                // Duplicate filename within the same batch (different source directories).
                // Generate a unique name so we don't overwrite the file we just copied.
                destPath = GenerateUniqueFileName(destinationFolder, fileName, usedFileNames);
                usedFileNames.Add(Path.GetFileName(destPath));
            }

            CopyOrMove(sourceFile, destPath, moveFiles, overwrite: false);
            processedSources.Add(sourceFile);
            copied++;

            await GenerateVideoThumbnailAsync(destPath, videoThumbnailService);
        }

        if (conflictResolutions?.Confirmed == true)
        {
            var renamedPairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var conflict in conflictResolutions.Conflicts)
            {
                switch (conflict.Resolution)
                {
                    case FileConflictResolution.Override:
                        CopyOrMove(conflict.NewFilePath, conflict.ExistingFilePath, moveFiles, overwrite: true);
                        processedSources.Add(conflict.NewFilePath);
                        overridden++;

                        await GenerateVideoThumbnailAsync(conflict.ExistingFilePath, videoThumbnailService);
                        break;

                    case FileConflictResolution.Rename:
                    {
                        var baseName = Path.GetFileNameWithoutExtension(conflict.ConflictingName);
                        if (!renamedPairs.TryGetValue(baseName, out var newBaseName))
                        {
                            var uniquePath = GenerateUniqueFileName(
                                destinationFolder, conflict.ConflictingName, usedFileNames);
                            newBaseName = Path.GetFileNameWithoutExtension(uniquePath);
                            renamedPairs[baseName] = newBaseName;
                        }

                        var extension = Path.GetExtension(conflict.ConflictingName);
                        var finalNewName = newBaseName + extension;
                        var finalRenamedPath = Path.Combine(destinationFolder, finalNewName);

                        usedFileNames.Add(finalNewName);
                        CopyOrMove(conflict.NewFilePath, finalRenamedPath, moveFiles, overwrite: false);
                        processedSources.Add(conflict.NewFilePath);
                        renamed++;

                        await GenerateVideoThumbnailAsync(finalRenamedPath, videoThumbnailService);
                        break;
                    }

                    case FileConflictResolution.Ignore:
                        ignored++;
                        break;
                }
            }
        }

        return new DatasetImportResult
        {
            Copied = copied,
            Overridden = overridden,
            Renamed = renamed,
            Ignored = ignored,
            ProcessedSourceFiles = processedSources
        };
    }

    private void CopyOrMove(string sourcePath, string destinationPath, bool moveFiles, bool overwrite)
    {
        if (moveFiles)
        {
            _fileOps.MoveFile(sourcePath, destinationPath, overwrite);
        }
        else
        {
            _fileOps.CopyFile(sourcePath, destinationPath, overwrite);
        }
    }

    private static async Task GenerateVideoThumbnailAsync(
        string filePath, IVideoThumbnailService? videoThumbnailService)
    {
        if (videoThumbnailService is null) return;
        if (!MediaFileExtensions.IsVideoFile(filePath)) return;

        await videoThumbnailService.GenerateThumbnailAsync(filePath);
    }

    /// <summary>
    /// Generates a unique file name that does not collide with files on disk or names already
    /// claimed by the current import batch.
    /// </summary>
    internal string GenerateUniqueFileName(
        string folderPath, string fileName, HashSet<string> batchUsedNames)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var counter = 1;
        const int maxIterations = 10_000;

        while (counter <= maxIterations)
        {
            var candidate = $"{nameWithoutExt}_{counter}{extension}";
            var candidatePath = Path.Combine(folderPath, candidate);

            if (!_fileOps.FileExists(candidatePath) &&
                !batchUsedNames.Contains(candidate))
            {
                return candidatePath;
            }

            counter++;
        }

        throw new InvalidOperationException(
            $"Unable to generate a unique file name after {maxIterations} attempts for '{fileName}'.");
    }
}
