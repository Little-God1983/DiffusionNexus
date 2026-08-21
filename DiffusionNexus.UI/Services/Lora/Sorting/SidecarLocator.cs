namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>
/// Locates the companion files that must travel with a LoRA when it is moved,
/// copied, or renamed. Convention: sidecars share the model file's stem in the
/// same directory ({stem}.civitai.info, {stem}.preview.png, ...). The existing
/// delete path (ModelTileViewModel.DeleteFilesFromDisk) misses sidecars — the
/// sorter must not repeat that mistake.
/// </summary>
public static class SidecarLocator
{
    /// <summary>
    /// Kept in step with <c>StaticFileTypes.GeneralExtensions</c>, which is what
    /// <c>ModelDiscoveryService</c> treats as "part of this model" — the two views of a
    /// model's companion files must not disagree. The video extensions and
    /// <c>.civitai</c>/<c>.thumb</c> were missing here, so a LoRA with a video preview had
    /// its weights moved and its <c>MyLora.mp4</c> left behind, permanently detached; with
    /// "delete empty source folders" on, the orphan even kept the folder alive.
    /// Model extensions (.safetensors/.ckpt/.pt/.pth) are deliberately absent: a model file
    /// must never be treated as another model's sidecar.
    /// </summary>
    public static readonly string[] SidecarExtensions =
    [
        ".civitai.info", ".civitai", ".json", ".metadata.json", ".cm-info.json",
        ".preview.png", ".preview.jpg", ".preview.jpeg", ".preview.webp",
        ".png", ".jpg", ".jpeg", ".webp", ".thumb.jpg", ".thumb", ".txt", ".info", ".yaml",
        ".mp4", ".webm", ".mov", ".avi", ".mkv", ".wmv", ".flv", ".m4v"
    ];

    /// <summary>Longest first, so ".preview.png" wins over ".png" (same rule as
    /// ModelMetadataUtils.ExtractBaseName).</summary>
    private static readonly string[] ExtensionsLongestFirst =
        [.. SidecarExtensions.OrderByDescending(e => e.Length)];

    public static IReadOnlyList<string> FindSidecars(string modelFilePath)
    {
        var directory = Path.GetDirectoryName(modelFilePath);
        var stem = Path.GetFileNameWithoutExtension(modelFilePath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(stem))
            return [];

        var results = new List<string>();
        foreach (var extension in SidecarExtensions)
        {
            var candidate = Path.Combine(directory, stem + extension);
            if (!string.Equals(candidate, modelFilePath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(candidate))
            {
                results.Add(candidate);
            }
        }
        return results;
    }

    /// <summary>
    /// Maps a sidecar onto the model's new (possibly renamed) stem.
    /// </summary>
    /// <remarks>
    /// <see cref="FindSidecars"/> only ever produces <c>{stem}{extension}</c> names, but
    /// the slice this relies on is unchecked, so any other caller could throw
    /// <see cref="ArgumentOutOfRangeException"/> out of the executor's transfer loop. When
    /// the name does not follow the convention we fall back to the longest known sidecar
    /// extension, then to the plain extension: an oddly named companion file should still
    /// travel with its model rather than cost the run.
    /// </remarks>
    public static string DeriveSidecarTargetPath(
        string sidecarPath, string modelFilePath, string targetModelFilePath)
    {
        var sourceStem = Path.GetFileNameWithoutExtension(modelFilePath);
        var sidecarName = Path.GetFileName(sidecarPath);

        var followsConvention = sourceStem.Length > 0
            && sidecarName.StartsWith(sourceStem, StringComparison.OrdinalIgnoreCase);
        // Everything after the source stem is the (possibly multi-dot) sidecar extension.
        var sidecarExtension = followsConvention
            ? sidecarName[sourceStem.Length..]
            : KnownExtensionOf(sidecarName) ?? Path.GetExtension(sidecarName);

        var targetDirectory = Path.GetDirectoryName(targetModelFilePath)
            ?? throw new ArgumentException(
                $"Sidecar target has no directory: '{targetModelFilePath}'.", nameof(targetModelFilePath));
        var targetStem = Path.GetFileNameWithoutExtension(targetModelFilePath);
        return Path.Combine(targetDirectory, targetStem + sidecarExtension);
    }

    private static string? KnownExtensionOf(string fileName)
        => Array.Find(ExtensionsLongestFirst,
            e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
