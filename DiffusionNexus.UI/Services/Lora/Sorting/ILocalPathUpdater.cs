namespace DiffusionNexus.UI.Services.Lora.Sorting;

public interface ILocalPathUpdater
{
    /// <summary>Repoints every DB ModelFile row at oldPath to newPath
    /// (LocalPath + LocalFileVerifiedAt = now, IsLocalFileValid = true).</summary>
    Task UpdateLocalPathsAsync(IReadOnlyList<(string OldPath, string NewPath)> changes,
        CancellationToken ct = default);
}
