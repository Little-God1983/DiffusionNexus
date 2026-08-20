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

    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).TrimEnd('.', ' ');
        return sanitized.Length == 0 ? "_" : sanitized;
    }

    public static string BuildTargetDirectory(
        string targetRoot, string? baseModelRaw, string categoryFolderName, bool includeCategory)
    {
        var baseFolder = IsPlaceholderBaseModel(baseModelRaw)
            ? UnknownFolderName
            : SanitizeFolderName(baseModelRaw!);
        var path = Path.Combine(targetRoot, baseFolder);
        if (includeCategory)
            path = Path.Combine(path, SanitizeFolderName(categoryFolderName));
        return path;
    }

    public static string BuildCollisionFreeFileName(
        string fileName, int? civitaiVersionId, Func<string, bool> nameIsTaken)
    {
        if (!nameIsTaken(fileName)) return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        if (civitaiVersionId is { } versionId)
        {
            var suffixed = $"{stem}_{versionId}{extension}";
            if (!nameIsTaken(suffixed)) return suffixed;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}_{i}{extension}";
            if (!nameIsTaken(candidate)) return candidate;
        }
    }
}
