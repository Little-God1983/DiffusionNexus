using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Helper;

namespace DiffusionNexus.Service.Services;

public class FolderNode
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public List<FolderNode> Children { get; } = new();
    public int ModelCount { get; set; }
    public bool IsExpanded { get; set; } = false;

    /// <summary>
    /// False when this folder (or one of its ancestors, while it was being scanned) could not be
    /// enumerated - e.g. an access-denied ACL, a broken junction, or another filesystem error.
    /// When false, <see cref="ModelCount"/> reflects 0 for the unreadable part of the tree rather
    /// than the true (unknown) count, and no further descendants could be discovered.
    /// </summary>
    public bool IsAccessible { get; set; } = true;
}

public class ModelDiscoveryService
{
    private readonly Func<DirectoryInfo, DirectoryInfo[]> _getSubDirectories;
    private readonly Func<string, SearchOption, IEnumerable<string>> _enumerateFiles;

    public ModelDiscoveryService()
        : this(dir => dir.GetDirectories(), (path, option) => Directory.EnumerateFiles(path, "*", option))
    {
    }

    /// <summary>
    /// Test-only seam (see DiffusionNexus.Tests, InternalsVisibleTo). Lets tests observe how many
    /// times/how many files the enumeration primitives touch (efficiency regression guard), and
    /// substitute a throwing delegate to simulate an unreadable directory (UnauthorizedAccessException,
    /// broken junction, etc.) without needing real restricted ACLs on disk.
    /// </summary>
    internal ModelDiscoveryService(
        Func<DirectoryInfo, DirectoryInfo[]> getSubDirectories,
        Func<string, SearchOption, IEnumerable<string>> enumerateFiles)
    {
        _getSubDirectories = getSubDirectories ?? throw new ArgumentNullException(nameof(getSubDirectories));
        _enumerateFiles = enumerateFiles ?? throw new ArgumentNullException(nameof(enumerateFiles));
    }

    public FolderNode BuildFolderTree(string rootDirectory)
    {
        var rootDir = new DirectoryInfo(rootDirectory);
        var node = new FolderNode { Name = rootDir.Name, FullPath = rootDir.FullName };
        BuildNode(node, rootDir);
        return node;
    }

    private void BuildNode(FolderNode node, DirectoryInfo dir)
    {
        // Single-pass rollup (#435): count each directory's OWN files exactly once
        // (TopDirectoryOnly) and add the child totals as recursion unwinds, instead of
        // re-walking the entire subtree (AllDirectories) at every ancestor node.
        DirectoryInfo[] subDirectories;
        try
        {
            subDirectories = _getSubDirectories(dir);
        }
        catch (Exception ex) when (IsUnreadableDirectoryException(ex))
        {
            // Can't even list this directory's children - skip the subtree instead of
            // aborting the whole scan. No children are added; ModelCount stays 0.
            node.IsAccessible = false;
            return;
        }

        var count = 0;
        foreach (var subDir in subDirectories)
        {
            var child = new FolderNode { Name = subDir.Name, FullPath = subDir.FullName };
            BuildNode(child, subDir);
            node.Children.Add(child);
            count += child.ModelCount;
        }

        try
        {
            count += CountModelsInThisFolderOnly(dir.FullName);
        }
        catch (Exception ex) when (IsUnreadableDirectoryException(ex))
        {
            // Subdirectories were listable but this directory's own files could not be read
            // (e.g. a broken junction). Keep whatever children were already discovered; just
            // don't contribute an unknown own-file count.
            node.IsAccessible = false;
        }

        node.ModelCount = count;
    }

    private int CountModelsInThisFolderOnly(string directory)
    {
        return _enumerateFiles(directory, SearchOption.TopDirectoryOnly)
            .Count(f => StaticFileTypes.ModelExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsUnreadableDirectoryException(Exception ex) =>
        ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException;

    public List<ModelClass> CollectModels(string rootDirectory)
    {
        var models = JsonInfoFileReaderService.GroupFilesByPrefix(rootDirectory);

        foreach (var model in models)
        {
            model.AssociatedFilesInfo = model.AssociatedFilesInfo
                .Where(f => StaticFileTypes.ModelExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)
                         || StaticFileTypes.GeneralExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        return models
            .Where(m => m.AssociatedFilesInfo.Any(f => StaticFileTypes.ModelExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)))
            .ToList();
    }
}
