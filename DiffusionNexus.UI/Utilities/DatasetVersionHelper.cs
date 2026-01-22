using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Utilities;

public static class DatasetVersionHelper
{
    public static Task MigrateLegacyToVersionedAsync(DatasetCardViewModel dataset)
    {
        var rootPath = dataset.FolderPath;
        var v1Path = dataset.GetVersionFolderPath(1);

        Directory.CreateDirectory(v1Path);

        var filesToMove = Directory.EnumerateFiles(rootPath)
            .Where(f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                var fileName = Path.GetFileName(f);
                if (fileName.StartsWith(".")) return false;
                return MediaFileExtensions.MediaExtensions.Contains(ext) || ext == ".txt";
            })
            .ToList();

        foreach (var sourceFile in filesToMove)
        {
            var fileName = Path.GetFileName(sourceFile);
            var destFile = Path.Combine(v1Path, fileName);
            File.Move(sourceFile, destFile);
        }

        dataset.IsVersionedStructure = true;
        dataset.CurrentVersion = 1;
        dataset.TotalVersions = 1;
        dataset.SaveMetadata();

        return Task.CompletedTask;
    }
}
