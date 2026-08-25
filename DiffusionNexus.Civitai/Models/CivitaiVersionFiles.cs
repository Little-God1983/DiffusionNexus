namespace DiffusionNexus.Civitai.Models;

/// <summary>
/// The one "pick the primary file" rule. Eight call sites carried private copies
/// (spec §1 RC5); they all route here so a future change to the preference cannot
/// diverge per path.
/// </summary>
public static class CivitaiVersionFiles
{
    /// <summary>Primary-flagged file, else the first file, else null.</summary>
    public static CivitaiModelFile? PickPrimary(CivitaiModelVersion? version)
        => version?.Files.FirstOrDefault(f => f.Primary == true) ?? version?.Files.FirstOrDefault();

    /// <summary>
    /// LoraDownloadService's 4-level chain: the richer version's primary/first file,
    /// falling back to the originally supplied version's primary/first file.
    /// </summary>
    public static CivitaiModelFile? PickPrimary(CivitaiModelVersion best, CivitaiModelVersion fallback)
        => PickPrimary(best) ?? PickPrimary(fallback);
}
