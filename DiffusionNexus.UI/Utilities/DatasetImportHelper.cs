using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Utilities;

public sealed class DatasetImportSummary
{
    public bool Cancelled { get; init; }
    public int Copied { get; init; }
    public int Overridden { get; init; }
    public int Renamed { get; init; }
    public int Ignored { get; init; }
    public IReadOnlyList<string> ProcessedSourceFiles { get; init; } = [];

    public int TotalAdded => Copied + Overridden + Renamed;

    public static DatasetImportSummary CancelledResult() => new() { Cancelled = true };
}

public static class DatasetImportHelper
{
    public static async Task<DatasetImportSummary> ImportFilesAsync(
        IDialogService dialogService,
        string destinationFolder,
        IEnumerable<string> sourceFiles,
        bool moveFiles)
    {
        var files = sourceFiles
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            return new DatasetImportSummary();
        }

        Directory.CreateDirectory(destinationFolder);

        var existingFileNames = Directory.Exists(destinationFolder)
            ? Directory.EnumerateFiles(destinationFolder)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var detection = FileConflictDetector.DetectConflicts(files, existingFileNames, destinationFolder);
        var resolution = new FileConflictResolutionResult { Confirmed = true, Conflicts = [] };

        if (detection.Conflicts.Count > 0)
        {
            resolution = await dialogService.ShowFileConflictDialogAsync(
                detection.Conflicts,
                detection.NonConflictingFiles);

            if (!resolution.Confirmed)
            {
                return DatasetImportSummary.CancelledResult();
            }
        }

        return ApplyResolution(
            destinationFolder,
            detection.NonConflictingFiles,
            resolution,
            moveFiles);
    }

    private static DatasetImportSummary ApplyResolution(
        string destinationFolder,
        List<string> nonConflictingFiles,
        FileConflictResolutionResult resolution,
        bool moveFiles)
    {
        var copied = 0;
        var overridden = 0;
        var renamed = 0;
        var ignored = 0;
        var processedSources = new List<string>();
        var renamedPairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceFile in nonConflictingFiles)
        {
            var fileName = Path.GetFileName(sourceFile);
            var destPath = Path.Combine(destinationFolder, fileName);
            TransferFile(sourceFile, destPath, moveFiles, overwrite: false);
            processedSources.Add(sourceFile);
            copied++;
        }

        foreach (var conflict in resolution.Conflicts)
        {
            switch (conflict.Resolution)
            {
                case FileConflictResolution.Override:
                    if (File.Exists(conflict.ExistingFilePath))
                    {
                        File.Delete(conflict.ExistingFilePath);
                    }
                    TransferFile(conflict.NewFilePath, conflict.ExistingFilePath, moveFiles, overwrite: true);
                    processedSources.Add(conflict.NewFilePath);

                    if (conflict.HasPairedCaption && conflict.PairedCaptionPath is not null)
                    {
                        var captionFileName = Path.GetFileName(conflict.PairedCaptionPath);
                        var destCaptionPath = Path.Combine(destinationFolder, captionFileName);
                        if (File.Exists(destCaptionPath))
                        {
                            File.Delete(destCaptionPath);
                        }
                        TransferFile(conflict.PairedCaptionPath, destCaptionPath, moveFiles, overwrite: true);
                        processedSources.Add(conflict.PairedCaptionPath);
                    }

                    overridden++;
                    break;

                case FileConflictResolution.Rename:
                    var baseName = Path.GetFileNameWithoutExtension(conflict.ConflictingName);
                    if (!renamedPairs.TryGetValue(baseName, out var newBaseName))
                    {
                        var uniquePath = GenerateUniqueFileName(destinationFolder, conflict.ConflictingName);
                        newBaseName = Path.GetFileNameWithoutExtension(uniquePath);
                        renamedPairs[baseName] = newBaseName;
                    }

                    var extension = Path.GetExtension(conflict.ConflictingName);
                    var finalName = newBaseName + extension;
                    var finalPath = Path.Combine(destinationFolder, finalName);
                    TransferFile(conflict.NewFilePath, finalPath, moveFiles, overwrite: false);
                    processedSources.Add(conflict.NewFilePath);

                    if (conflict.HasPairedCaption && conflict.PairedCaptionPath is not null)
                    {
                        var captionExtension = Path.GetExtension(conflict.PairedCaptionPath);
                        var captionPath = Path.Combine(destinationFolder, newBaseName + captionExtension);
                        TransferFile(conflict.PairedCaptionPath, captionPath, moveFiles, overwrite: false);
                        processedSources.Add(conflict.PairedCaptionPath);
                    }

                    renamed++;
                    break;

                case FileConflictResolution.Ignore:
                    ignored++;
                    break;
            }
        }

        return new DatasetImportSummary
        {
            Copied = copied,
            Overridden = overridden,
            Renamed = renamed,
            Ignored = ignored,
            ProcessedSourceFiles = processedSources
        };
    }

    private static void TransferFile(string sourcePath, string destinationPath, bool moveFile, bool overwrite)
    {
        if (moveFile)
        {
            if (overwrite && File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(sourcePath, destinationPath);
            return;
        }

        File.Copy(sourcePath, destinationPath, overwrite);
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
