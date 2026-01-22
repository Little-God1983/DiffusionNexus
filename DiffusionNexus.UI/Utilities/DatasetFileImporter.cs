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

public static class DatasetFileImporter
{
    public static async Task<DatasetImportResult> ImportWithDialogAsync(
        IEnumerable<string> sourceFiles,
        string destinationFolder,
        IDialogService dialogService,
        IVideoThumbnailService? videoThumbnailService,
        bool moveFiles)
    {
        var sourceList = sourceFiles
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sourceList.Count == 0)
        {
            return new DatasetImportResult();
        }

        Directory.CreateDirectory(destinationFolder);

        var existingFileNames = Directory.GetFiles(destinationFolder)
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

    public static async Task<DatasetImportResult> ImportResolvedAsync(
        IEnumerable<string> nonConflictingFiles,
        FileConflictResolutionResult? conflictResolutions,
        string destinationFolder,
        IVideoThumbnailService? videoThumbnailService,
        bool moveFiles)
    {
        var processedSources = new List<string>();
        var copied = 0;
        var overridden = 0;
        var renamed = 0;
        var ignored = 0;

        foreach (var sourceFile in nonConflictingFiles)
        {
            var fileName = Path.GetFileName(sourceFile);
            var destPath = Path.Combine(destinationFolder, fileName);

            await CopyOrMoveAsync(sourceFile, destPath, moveFiles);
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
                        if (File.Exists(conflict.ExistingFilePath))
                        {
                            File.Delete(conflict.ExistingFilePath);
                        }

                        await CopyOrMoveAsync(conflict.NewFilePath, conflict.ExistingFilePath, moveFiles);
                        processedSources.Add(conflict.NewFilePath);
                        overridden++;

                        await GenerateVideoThumbnailAsync(conflict.ExistingFilePath, videoThumbnailService);
                        break;

                    case FileConflictResolution.Rename:
                    {
                        var baseName = Path.GetFileNameWithoutExtension(conflict.ConflictingName);
                        if (!renamedPairs.TryGetValue(baseName, out var newBaseName))
                        {
                            var uniquePath = GenerateUniqueFileName(destinationFolder, conflict.ConflictingName);
                            newBaseName = Path.GetFileNameWithoutExtension(uniquePath);
                            renamedPairs[baseName] = newBaseName;
                        }

                        var extension = Path.GetExtension(conflict.ConflictingName);
                        var finalNewName = newBaseName + extension;
                        var finalRenamedPath = Path.Combine(destinationFolder, finalNewName);

                        await CopyOrMoveAsync(conflict.NewFilePath, finalRenamedPath, moveFiles);
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

    private static async Task CopyOrMoveAsync(string sourcePath, string destinationPath, bool moveFiles)
    {
        if (moveFiles)
        {
            await MoveFileSafelyAsync(sourcePath, destinationPath);
        }
        else
        {
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }
    }

    private static async Task MoveFileSafelyAsync(string sourcePath, string destinationPath)
    {
        try
        {
            File.Move(sourcePath, destinationPath, overwrite: true);
        }
        catch (IOException)
        {
            await Task.Run(() => File.Copy(sourcePath, destinationPath, overwrite: true));
            File.Delete(sourcePath);
        }
    }

    private static async Task GenerateVideoThumbnailAsync(string filePath, IVideoThumbnailService? videoThumbnailService)
    {
        if (videoThumbnailService is null) return;
        if (!MediaFileExtensions.IsVideoFile(filePath)) return;

        await videoThumbnailService.GenerateThumbnailAsync(filePath);
    }

    private static string GenerateUniqueFileName(string folderPath, string fileName)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var counter = 1;
        string newPath;

        do
        {
            var newName = $"{nameWithoutExt}_{counter}{extension}";
            newPath = Path.Combine(folderPath, newName);
            counter++;
        } while (File.Exists(newPath));

        return newPath;
    }
}
