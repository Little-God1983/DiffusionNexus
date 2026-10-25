namespace DiffusionNexus.UI.Services.Lora.Sorting;

public interface ILocalPathUpdater
{
    /// <summary>Repoints every DB ModelFile row at oldPath to newPath
    /// (LocalPath + LocalFileVerifiedAt = now, IsLocalFileValid = true).</summary>
    Task UpdateLocalPathsAsync(IReadOnlyList<(string OldPath, string NewPath)> changes,
        CancellationToken ct = default);

    /// <summary>
    /// Attaches each copy to its original's version as one more file (#653, the way #380 attaches
    /// a re-download), so the next library scan does not give it a bare "???" record. A copy whose
    /// original has no record is left to the scan. Any other record on the copy's path is stale
    /// (the copy is written with overwrite off) and lets go of the path.
    /// </summary>
    Task RegisterCopiesAsync(IReadOnlyList<(string SourcePath, string CopyPath)> copies,
        CancellationToken ct = default);
}
