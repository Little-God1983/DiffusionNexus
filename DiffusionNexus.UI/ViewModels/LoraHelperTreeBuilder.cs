using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiffusionNexus.Service.Services;

namespace DiffusionNexus.UI.ViewModels;

internal static class LoraHelperTreeBuilder
{
    internal const string BaseLoraRootName = "Base Loras";
    internal const string UnknownBaseModelFolderName = "Unknown Base Model";

    private static readonly char[] PathSeparators =
        { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\\' };

    internal static IReadOnlyList<string> BuildMergedSegments(
        string sourcePath,
        string? folderPath,
        string? baseModel)
    {
        var segments = new List<string> { BaseLoraRootName };
        var normalizedBaseModel = NormalizeBaseModel(baseModel);
        var baseModelFolder = normalizedBaseModel ?? UnknownBaseModelFolderName;
        segments.Add(baseModelFolder);

        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            var relativeSegments = GetRelativeSegments(sourcePath, folderPath!);
            if (normalizedBaseModel != null &&
                relativeSegments.Count > 0 &&
                string.Equals(relativeSegments[0], normalizedBaseModel, StringComparison.OrdinalIgnoreCase))
            {
                relativeSegments.RemoveAt(0);
            }

            segments.AddRange(relativeSegments);
        }

        return segments;
    }

    internal static FolderNode? BuildMergedFolderTree(IEnumerable<IReadOnlyList<string>> entrySegments)
    {
        var segmentList = entrySegments
            .Where(s => s != null && s.Count > 0)
            .ToList();

        if (segmentList.Count == 0)
        {
            return null;
        }

        var comparer = StringComparer.OrdinalIgnoreCase;
        var nodes = new Dictionary<string, FolderNode>(comparer);

        FolderNode EnsureNode(string path, string name, string? parentPath)
        {
            if (!nodes.TryGetValue(path, out var node))
            {
                node = new FolderNode
                {
                    Name = name,
                    FullPath = path,
                    IsExpanded = parentPath is null || comparer.Equals(parentPath, BaseLoraRootName)
                };
                nodes[path] = node;

                if (parentPath != null && nodes.TryGetValue(parentPath, out var parent))
                {
                    parent.Children.Add(node);
                }
            }

            return node;
        }

        foreach (var segments in segmentList)
        {
            var cumulative = new List<string>(segments.Count);
            for (var i = 0; i < segments.Count; i++)
            {
                cumulative.Add(segments[i]);
                var path = string.Join(Path.DirectorySeparatorChar, cumulative);
                var parentPath = i == 0 ? null : string.Join(Path.DirectorySeparatorChar, cumulative.Take(i));
                var node = EnsureNode(path, segments[i], parentPath);
                node.ModelCount++;
            }
        }

        if (!nodes.TryGetValue(BaseLoraRootName, out var root))
        {
            return null;
        }

        SortChildren(root, comparer);
        return root;
    }

    private static void SortChildren(FolderNode node, StringComparer comparer)
    {
        node.Children.Sort((a, b) => comparer.Compare(a.Name, b.Name));
        foreach (var child in node.Children)
        {
            SortChildren(child, comparer);
        }
    }

    private static string? NormalizeBaseModel(string? baseModel)
    {
        if (string.IsNullOrWhiteSpace(baseModel))
        {
            return null;
        }

        var trimmed = baseModel.Trim();
        return string.Equals(trimmed, "UNKNOWN", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    private static List<string> GetRelativeSegments(string sourcePath, string folderPath)
    {
        var sourceSegments = SplitSegments(sourcePath);
        var folderSegments = SplitSegments(folderPath);

        var index = 0;
        while (index < sourceSegments.Count &&
               index < folderSegments.Count &&
               string.Equals(sourceSegments[index], folderSegments[index], StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }

        return folderSegments.Skip(index).ToList();
    }

    private static List<string> SplitSegments(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new List<string>();
        }

        return path
            .Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }
}
