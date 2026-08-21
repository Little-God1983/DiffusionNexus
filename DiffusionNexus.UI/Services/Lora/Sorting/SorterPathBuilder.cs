namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>
/// Pure path construction for the LoRA Sorter: folder naming, sanitization
/// (nothing in the download path sanitizes — this is deliberately new), and the
/// deterministic collision rename convention shared with
/// CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync
/// ({stem}_{versionId}{ext}), so re-runs are idempotent.
/// </summary>
public static class SorterPathBuilder
{
    public const string UnknownFolderName = "Unknown";

    public static bool IsPlaceholderBaseModel(string? baseModel)
        => string.IsNullOrWhiteSpace(baseModel) || baseModel == "???";

    /// <summary>
    /// Folder-name sanitization. Windows-only rules today.
    /// </summary>
    /// <remarks>
    /// TODO: Linux Implementation for LoRA Sorter: the TrimEnd('.', ' ') below is a Win32
    /// restriction, and Path.GetInvalidFileNameChars() returns only NUL and '/' on Linux —
    /// so a base-model or category name carrying \ : * ? " &lt; &gt; | would pass straight into a
    /// created directory name there, and such a folder is then awkward on any Windows client
    /// sharing the library. A Linux build should sanitize against the union of both platforms'
    /// invalid sets; this method is the single seam for that (make it an injectable policy
    /// rather than adding a second static).
    /// </remarks>
    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).TrimEnd('.', ' ');
        return sanitized.Length == 0 ? "_" : sanitized;
    }

    /// <summary>
    /// An unresolved category contributes NO segment, exactly as
    /// <c>DownloadDestinationViewModel.BuildTargetDirectory</c> omits it for a null/empty
    /// category: the file lands in <c>{root}\{BaseModel}\</c>. The sorter used to append
    /// <c>Unknown\</c> unconditionally, so — both features defaulting their folder toggles
    /// to true — every sort run dragged uncategorized downloads into <c>Unknown\</c> and the
    /// next download re-created them one level up, forever.
    /// A placeholder base model keeps its <c>Unknown\</c> folder: unlike the category, the
    /// base-model segment is the top-level bucket and files with no base model need one.
    /// </summary>
    public static string BuildTargetDirectory(
        string targetRoot, string? baseModelRaw, string? categoryFolderName, bool includeCategory)
    {
        var baseFolder = IsPlaceholderBaseModel(baseModelRaw)
            ? UnknownFolderName
            : SanitizeFolderName(baseModelRaw!);
        var path = Path.Combine(targetRoot, baseFolder);
        if (includeCategory && !IsUnresolvedCategory(categoryFolderName))
            path = Path.Combine(path, SanitizeFolderName(categoryFolderName!));
        return path;
    }

    /// <summary>Null, empty, or the Unknown bucket name — i.e. "no category segment".</summary>
    public static bool IsUnresolvedCategory(string? categoryFolderName)
        => string.IsNullOrWhiteSpace(categoryFolderName)
           || string.Equals(categoryFolderName.Trim(), UnknownFolderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The naming sequence a colliding file walks: the plain name, then <c>{stem}_{versionId}</c>
    /// when there is a version id, then <c>{stem}_2</c>, <c>_3</c>, … without end.
    /// </summary>
    /// <remarks>
    /// Name selection only — deliberately no "is it taken" callback and no disk access. Choosing a
    /// name needs a content comparison at every step (a taken name holding an identical file means
    /// "already sorted, skip", not "try the next name"), and the hashes and the plan-local claim
    /// map both live in <see cref="LoraSortPlanner"/>. Splitting that decision across two types is
    /// what let the numeric fallback grow <c>_2</c>, <c>_3</c>, <c>_4</c>… on every re-run of a copy:
    /// each run collided on the plain name, saw its own previous copy as merely "taken", and never
    /// compared content with it.
    /// </remarks>
    public static IEnumerable<string> EnumerateCandidateNames(string fileName, int? civitaiVersionId)
    {
        yield return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        if (civitaiVersionId is { } versionId)
            yield return $"{stem}_{versionId}{extension}";

        for (var i = 2; ; i++)
            yield return $"{stem}_{i}{extension}";
    }
}
