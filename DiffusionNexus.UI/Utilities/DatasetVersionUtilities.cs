using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Utilities;

public static class DatasetVersionUtilities
{
    public static Task EnsureVersionedStructureAsync(DatasetCardViewModel dataset)
    {
        if (dataset.IsVersionedStructure || dataset.ImageCount == 0)
        {
            return Task.CompletedTask;
        }

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

    public static async Task<int> CreateEmptyVersionAsync(
        DatasetCardViewModel dataset,
        int parentVersion,
        IDatasetEventAggregator? eventAggregator)
    {
        var nextVersion = dataset.GetNextVersionNumber();
        var destPath = dataset.GetVersionFolderPath(nextVersion);

        if (!dataset.IsVersionedStructure && dataset.ImageCount > 0)
        {
            await EnsureVersionedStructureAsync(dataset);
        }

        Directory.CreateDirectory(destPath);

        var parentNsfw = dataset.VersionNsfwFlags.GetValueOrDefault(parentVersion, false);
        dataset.VersionNsfwFlags[nextVersion] = parentNsfw;

        dataset.RecordBranch(nextVersion, parentVersion);
        dataset.CurrentVersion = nextVersion;
        dataset.IsVersionedStructure = true;
        dataset.SaveMetadata();
        dataset.RefreshImageInfo();

        eventAggregator?.PublishVersionCreated(new VersionCreatedEventArgs
        {
            Dataset = dataset,
            NewVersion = nextVersion,
            BranchedFromVersion = parentVersion
        });

        return nextVersion;
    }
}
