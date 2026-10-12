namespace DiffusionNexus.UI.Services.Engine;

/// <summary>A file a removal would delete, with its size.</summary>
public sealed record PlannedFile(string Path, long Bytes);

/// <summary>A number of files and their total size.</summary>
public sealed record ModelFileTotal(int Count, long Bytes);

/// <summary>An item a removal keeps because another installed feature still uses it.</summary>
public sealed record KeptItem(string Name, string Path, string NeededBy);

/// <summary>
/// What removing one feature deletes, keeps, and asks about. <paramref name="RemovedWithIt"/> names the
/// features that read as present only through this feature's files and keep none of them, so they go with it.
/// </summary>
public sealed record EngineRemovalPlan(
    IReadOnlyList<string> NodePackFolders,
    IReadOnlyList<PlannedFile> EngineModels,
    IReadOnlyList<PlannedFile> OutsideModels,
    IReadOnlyList<KeptItem> Kept,
    IReadOnlyList<string> RemovedWithIt)
{
    /// <summary>
    /// Features that protect nothing and lose some of their files to this removal but not all: a file of
    /// theirs another feature keeps stays. They show as Partial afterwards.
    /// </summary>
    public IReadOnlyList<string> LeftPartial { get; init; } = [];

    public long EngineBytes => EngineModels.Sum(f => f.Bytes);

    public bool HasAnythingToDelete => NodePackFolders.Count > 0 || EngineModels.Count > 0 || OutsideModels.Count > 0;
}

/// <summary>
/// Turns disk checks into removal plans. A file inside the Engine folder is one the Engine downloaded
/// (feature downloads land in its models folder); anything else is in one of the user's own folders.
/// A node pack or model that another feature also uses is kept, matched by full path, when that feature
/// is fully installed and present on its own: it has at least one file on disk that is not one of the
/// removed feature's. A Partial feature protects nothing.
/// </summary>
public static class EngineRemovalPlanner
{
    public static EngineRemovalPlan PlanFeatureRemoval(
        string engineRoot, IReadOnlyList<EngineFeatureCheck> checks, EngineFeature feature, Func<string, long>? sizeOf = null)
    {
        sizeOf ??= FileSize;
        var target = checks.Single(c => c.Feature == feature);

        // An owner must be fully installed and present on its own. A Partial feature does not work, so it
        // protects nothing: otherwise features each removed once (each left with files the others share)
        // would keep those files for one another and none could ever be removed. A feature whose files on
        // disk are all the target's own (a subset, e.g. Inpaint & Outpaint inside Outpaint Vision) reads as
        // present only because of the target and goes with it, for the same reason.
        // Every copy found counts here, deletable or not: whether a feature is installed and present on its
        // own is what the Features dialog shows, not which copies a removal may delete.
        var targetPaths = new HashSet<string>(FoundPaths(target).Select(Key), StringComparer.OrdinalIgnoreCase);
        var present = checks.Where(c => c.Feature != feature && c.IsPresent).ToList();
        var others = present.Where(c => IsFullyInstalled(c) && FoundPaths(c).Any(p => !targetPaths.Contains(Key(p)))).ToList();

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
                     .Where(m => m.IsInstalled && !m.IsPlaceholder).SelectMany(m => DeletableCopies(engineRoot, m)))
        {
            if (!seen.Add(Key(path))) continue;
            if (modelOwners.TryGetValue(Key(path), out var owner))
                kept.Add(new KeptItem(System.IO.Path.GetFileName(path), path, owner));
            else if (FolderPathMatch.Contains(engineRoot, path))
                engineModels.Add(new PlannedFile(path, sizeOf(path)));
            else
                outside.Add(new PlannedFile(path, sizeOf(path)));
        }

        // A feature that protects nothing and loses every file it has goes with the target; one that keeps a
        // file (another feature needs it, or it is the feature's own) is left partly installed; one that
        // loses nothing is not named.
        var deleted = new HashSet<string>(
            nodePacks.Concat(engineModels.Concat(outside).Select(f => f.Path)).Select(Key), StringComparer.OrdinalIgnoreCase);
        var removedWithIt = new List<string>();
        var leftPartial = new List<string>();
        foreach (var check in present.Except(others))
        {
            var paths = FoundPaths(check).Select(Key).ToList();
            var lost = paths.Where(deleted.Contains).ToList();
            if (lost.Count == 0) continue;
            (lost.Count == paths.Count ? removedWithIt : leftPartial).Add(check.DisplayName);
        }

        return new EngineRemovalPlan(nodePacks, engineModels, outside, kept, removedWithIt) { LeftPartial = leftPartial };
    }

    /// <summary>
    /// Every workload of the feature is in the catalog and nothing of it is missing: every node pack and
    /// every (non-placeholder) model is on disk. A model the checker cannot find by name (its download URL
    /// carries no file name) is not counted as missing: not finding it says nothing.
    /// </summary>
    private static bool IsFullyInstalled(EngineFeatureCheck check) =>
        check.IsComplete && check.Results.Count > 0 && check.Results.All(r =>
            r.CustomNodeResults.All(n => n.IsInstalled) &&
            r.ModelResults.All(m => m.IsInstalled || m.IsPlaceholder || !m.IsFindableByName));

    /// <summary>A feature's files on disk: installed node packs and every copy found of its (non-placeholder) models.</summary>
    private static IEnumerable<string> FoundPaths(EngineFeatureCheck check) =>
        check.Results.SelectMany(r => r.CustomNodeResults).Where(n => n.IsInstalled).Select(n => n.ExpectedPath)
            .Concat(check.Results.SelectMany(r => r.ModelResults)
                .Where(m => m.IsInstalled && !m.IsPlaceholder).SelectMany(m => m.AllFoundPaths));

    /// <summary>
    /// The copies of a model a removal may delete: every copy inside the Engine folder, and a copy outside
    /// it only when <see cref="IsOfferedOutsideCopy"/> says it is this model's.
    /// </summary>
    private static IEnumerable<string> DeletableCopies(string engineRoot, DiffusionNexus.UI.Services.ConfigurationChecker.Models.ModelCheckResult model) =>
        model.AllFoundPaths.Where(p => FolderPathMatch.Contains(engineRoot, p) || IsOfferedOutsideCopy(model, p));

    // ComfyUI's legacy folder names, either way round.
    private static readonly Dictionary<string, string> CategoryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["unet"] = "diffusion_models", ["diffusion_models"] = "unet",
        ["clip"] = "text_encoders", ["text_encoders"] = "clip"
    };

    /// <summary>
    /// A copy in the user's own folders is this model's only when it sits directly in a folder named like
    /// the model's category (<c>…\vae\ae.safetensors</c> for a <c>models/vae</c> model; ComfyUI's legacy
    /// aliases count) under a name the installer leaves on disk. A URL-derived name the installer renames
    /// to <c>&lt;Name&gt;&lt;ext&gt;</c> (a generic <c>diffusion_pytorch_model.safetensors</c>) belongs to some other model.
    /// </summary>
    private static bool IsOfferedOutsideCopy(DiffusionNexus.UI.Services.ConfigurationChecker.Models.ModelCheckResult model, string path)
    {
        if (string.IsNullOrWhiteSpace(model.Category)) return false;
        var folder = Path.GetFileName(Path.GetDirectoryName(path));
        var inCategory = string.Equals(folder, model.Category, StringComparison.OrdinalIgnoreCase)
            || (CategoryAliases.TryGetValue(model.Category, out var alias) && string.Equals(folder, alias, StringComparison.OrdinalIgnoreCase));
        return inCategory
            && DiffusionNexus.UI.Services.ConfigurationChecker.ConfigurationCheckerService.DeriveConfiguredFileName(model.Name, Path.GetFileName(path)) is null;
    }

    /// <summary>Models of present features that sit outside the Engine folder and are offered for deletion (<see cref="IsOfferedOutsideCopy"/>), each once.</summary>
    public static IReadOnlyList<PlannedFile> PlanUninstallOutsideModels(
        string engineRoot, IReadOnlyList<EngineFeatureCheck> checks, Func<string, long>? sizeOf = null)
    {
        sizeOf ??= FileSize;
        return checks.Where(c => c.IsPresent)
            .SelectMany(c => c.Results).SelectMany(r => r.ModelResults)
            .Where(m => m.IsInstalled && !m.IsPlaceholder)
            .SelectMany(m => m.AllFoundPaths.Where(p => !FolderPathMatch.Contains(engineRoot, p) && IsOfferedOutsideCopy(m, p)))
            .DistinctBy(Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new PlannedFile(p, sizeOf(p)))
            .ToList();
    }

    /// <summary>
    /// The model files in the Engine's own models folder that no Engine feature declares (a Core download
    /// that landed there, a file the user put there). Uninstall deletes them with the Engine.
    /// </summary>
    /// <remarks>
    /// Only model files count (<see cref="DiffusionNexus.Domain.Utilities.ModelFileExtensions.Recognized"/>),
    /// so ComfyUI's own <c>put_*_here</c> placeholders and <c>configs</c> are not reported; a junction or
    /// symlink inside is not entered (the uninstall removes it as a link). Null (not known) when the models
    /// folder, or a folder between it and the Engine root, is itself a link: only the link goes, so the
    /// files behind it are not the Engine's to count.
    /// </remarks>
    public static ModelFileTotal? PlanUninstallUnclaimedModels(string engineRoot, IReadOnlyList<EngineFeatureCheck> checks)
    {
        var models = ManagedEngineLocator.ModelsFolder(engineRoot);
        if (!Directory.Exists(models)) return new ModelFileTotal(0, 0);
        if (IsLinkedBelow(engineRoot, models)) return null;

        var declared = new HashSet<string>(
            checks.SelectMany(c => c.Results).SelectMany(r => r.ModelResults).SelectMany(m => m.AllFoundPaths).Select(Key),
            StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
        };
        var unclaimed = new DirectoryInfo(models).EnumerateFiles("*", options)
            .Where(f => DiffusionNexus.Domain.Utilities.ModelFileExtensions.Matches(f.Name, DiffusionNexus.Domain.Utilities.ModelFileExtensions.Recognized))
            .Where(f => !declared.Contains(Key(f.FullName)))
            .ToList();
        return new ModelFileTotal(unclaimed.Count, unclaimed.Sum(f => f.Length));
    }

    /// <summary>
    /// Whether <paramref name="folder"/> or any folder between it and <paramref name="root"/> (the root
    /// itself not included) is a junction or symlink. Unreadable attributes count as a link.
    /// </summary>
    private static bool IsLinkedBelow(string root, string folder)
    {
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var current = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
             current is not null && !string.Equals(current.FullName, top, StringComparison.OrdinalIgnoreCase);
             current = current.Parent)
        {
            try
            {
                if (current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }
        return false;
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
