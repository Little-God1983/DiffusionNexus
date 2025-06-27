using DiffusionNexus.LoraSort.Service.Classes;
using DiffusionNexus.LoraSort.Service.Helper;

namespace DiffusionNexus.LoraSort.Service.Services;

public class FolderNode
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public List<FolderNode> Children { get; } = new();
    public int ModelCount { get; set; }
}

public class ModelDiscoveryService
{
    public FolderNode BuildFolderTree(string rootDirectory)
    {
        var rootDir = new DirectoryInfo(rootDirectory);
        var node = new FolderNode { Name = rootDir.Name, FullPath = rootDir.FullName };
        BuildNode(node, rootDir);
        return node;
    }

    private void BuildNode(FolderNode node, DirectoryInfo dir)
    {
        foreach (var subDir in dir.GetDirectories())
        {
            var child = new FolderNode { Name = subDir.Name, FullPath = subDir.FullName };
            BuildNode(child, subDir);
            node.Children.Add(child);
        }

        node.ModelCount = CountModels(dir.FullName);
    }

    private int CountModels(string directory)
    {
        return Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Count(f => StaticFileTypes.ModelExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
    }

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
