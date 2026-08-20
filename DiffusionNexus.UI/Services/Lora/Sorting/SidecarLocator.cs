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
    public static readonly string[] SidecarExtensions =
    [
        ".civitai.info", ".json", ".metadata.json", ".cm-info.json",
        ".preview.png", ".preview.jpg", ".preview.jpeg", ".preview.webp",
        ".png", ".jpg", ".jpeg", ".webp", ".thumb.jpg", ".txt", ".info", ".yaml"
    ];

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

    public static string DeriveSidecarTargetPath(
        string sidecarPath, string modelFilePath, string targetModelFilePath)
    {
        var sourceStem = Path.GetFileNameWithoutExtension(modelFilePath);
        var sidecarName = Path.GetFileName(sidecarPath);
        // Everything after the source stem is the (possibly multi-dot) sidecar extension.
        var sidecarExtension = sidecarName[sourceStem.Length..];

        var targetDirectory = Path.GetDirectoryName(targetModelFilePath)!;
        var targetStem = Path.GetFileNameWithoutExtension(targetModelFilePath);
        return Path.Combine(targetDirectory, targetStem + sidecarExtension);
    }
}
