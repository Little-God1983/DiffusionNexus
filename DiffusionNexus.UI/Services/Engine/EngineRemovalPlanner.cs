namespace DiffusionNexus.UI.Services.Engine;

/// <summary>A file a removal would delete, with its size.</summary>
public sealed record PlannedFile(string Path, long Bytes);

/// <summary>An item a removal keeps because another installed feature still uses it.</summary>
public sealed record KeptItem(string Name, string Path, string NeededBy);

/// <summary>What removing one feature deletes, keeps, and asks about.</summary>
public sealed record EngineRemovalPlan(
    IReadOnlyList<string> NodePackFolders,
    IReadOnlyList<PlannedFile> EngineModels,
    IReadOnlyList<PlannedFile> OutsideModels,
    IReadOnlyList<KeptItem> Kept)
{
    public long EngineBytes => EngineModels.Sum(f => f.Bytes);

    public bool HasAnythingToDelete => NodePackFolders.Count > 0 || EngineModels.Count > 0 || OutsideModels.Count > 0;
}

/// <summary>
/// Turns disk checks into removal plans. A file inside the Engine folder is one the Engine downloaded
/// (feature downloads land in its models folder); anything else is in one of the user's own folders.
/// A node pack or model that another present feature also uses is kept, matched by full path.
/// </summary>
public static class EngineRemovalPlanner
{
    public static EngineRemovalPlan PlanFeatureRemoval(
        string engineRoot, IReadOnlyList<EngineFeatureCheck> checks, EngineFeature feature, Func<string, long>? sizeOf = null)
    {
        sizeOf ??= FileSize;
        var target = checks.Single(c => c.Feature == feature);
        var others = checks.Where(c => c.Feature != feature && c.IsPresent).ToList();

        var nodeOwners = Owners(others, c => c.Results
            .SelectMany(r => r.CustomNodeResults).Where(n => n.IsInstalled).Select(n => n.ExpectedPath));
        var modelOwners = Owners(others, c => c.Results
            .SelectMany(r => r.ModelResults).Where(m => m.IsInstalled).SelectMany(m => m.AllFoundPaths));

        var nodePacks = new List<string>();
        var engineModels = new List<PlannedFile>();
        var outside = new List<PlannedFile>();
        var kept = new List<KeptItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in target.Results.SelectMany(r => r.CustomNodeResults).Where(n => n.IsInstalled))
        {
            if (!seen.Add(Key(node.ExpectedPath))) continue;
            if (nodeOwners.TryGetValue(Key(node.ExpectedPath), out var owner))
                kept.Add(new KeptItem(node.Name, node.ExpectedPath, owner));
            else
                nodePacks.Add(node.ExpectedPath);
        }

        foreach (var path in target.Results.SelectMany(r => r.ModelResults)
                     .Where(m => m.IsInstalled && !m.IsPlaceholder).SelectMany(m => m.AllFoundPaths))
        {
            if (!seen.Add(Key(path))) continue;
            if (modelOwners.TryGetValue(Key(path), out var owner))
                kept.Add(new KeptItem(System.IO.Path.GetFileName(path), path, owner));
            else if (FolderPathMatch.Contains(engineRoot, path))
                engineModels.Add(new PlannedFile(path, sizeOf(path)));
            else
                outside.Add(new PlannedFile(path, sizeOf(path)));
        }

        return new EngineRemovalPlan(nodePacks, engineModels, outside, kept);
    }

    /// <summary>Models of present features that sit outside the Engine folder, each once.</summary>
    public static IReadOnlyList<PlannedFile> PlanUninstallOutsideModels(
        string engineRoot, IReadOnlyList<EngineFeatureCheck> checks, Func<string, long>? sizeOf = null)
    {
        sizeOf ??= FileSize;
        return checks.Where(c => c.IsPresent)
            .SelectMany(c => c.Results).SelectMany(r => r.ModelResults)
            .Where(m => m.IsInstalled && !m.IsPlaceholder)
            .SelectMany(m => m.AllFoundPaths)
            .Where(p => !FolderPathMatch.Contains(engineRoot, p))
            .DistinctBy(Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new PlannedFile(p, sizeOf(p)))
            .ToList();
    }

    /// <summary>
    /// False for a recorded Engine folder the app must never delete, size or sweep settings by: blank,
    /// not a full path, or a drive (or network share) root. A corrupted row can hold any of these.
    /// </summary>
    public static bool IsSafeEngineRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) return false;
        var full = FolderPathMatch.Normalize(root);
        if (full is null) return false;
        var driveRoot = Path.GetPathRoot(full)?.TrimEnd('\\', '/');
        return !string.Equals(driveRoot, full.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Total size of the files under <paramref name="folder"/>, not following junctions; 0 when it is gone.</summary>
    public static long FolderSize(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
        };
        return new DirectoryInfo(folder).EnumerateFiles("*", options).Sum(f => f.Length);
    }

    private static Dictionary<string, string> Owners(
        IEnumerable<EngineFeatureCheck> features, Func<EngineFeatureCheck, IEnumerable<string>> paths)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in features)
            foreach (var path in paths(feature))
                owners.TryAdd(Key(path), feature.DisplayName);
        return owners;
    }

    private static string Key(string path) => FolderPathMatch.Normalize(path) ?? path;

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception) { return 0; }
    }
}
