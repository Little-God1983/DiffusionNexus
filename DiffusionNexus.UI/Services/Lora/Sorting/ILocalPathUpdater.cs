namespace DiffusionNexus.UI.Services.Lora.Sorting;

public interface ILocalPathUpdater
{
    /// <summary>Repoints every DB ModelFile row at oldPath to newPath
    /// (LocalPath + LocalFileVerifiedAt = now, IsLocalFileValid = true).</summary>
    Task UpdateLocalPathsAsync(IReadOnlyList<(string OldPath, string NewPath)> changes,
        CancellationToken ct = default);

    /// <summary>
    /// Gives each copy a record chain of its own carrying the original's metadata (#653), so the
    /// next library scan does not give it a bare "???" one. A copy whose path already has a
    /// record, or whose original has none, is left alone.
    /// </summary>
    Task RegisterCopiesAsync(IReadOnlyList<(string SourcePath, string CopyPath)> copies,
        CancellationToken ct = default);
}
