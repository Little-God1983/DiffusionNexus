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

    /// <summary>The file whose SHA256 is <paramref name="sha256"/> (case-insensitive), else null.</summary>
    public static CivitaiModelFile? FindBySha256(CivitaiModelVersion? version, string? sha256)
        => string.IsNullOrWhiteSpace(sha256)
            ? null
            : version?.Files.FirstOrDefault(f =>
                string.Equals(f.Hashes?.SHA256, sha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The file a queued job was created for, by its expected SHA256, while the version still
    /// carries it; else the primary. A waitlist entry followed by hash (#617) can sit on a version
    /// whose primary is a different file.
    /// </summary>
    public static CivitaiModelFile? PickBySha256OrPrimary(CivitaiModelVersion? version, string? sha256)
        => FindBySha256(version, sha256) ?? PickPrimary(version);
}
