using DiffusionNexus.Civitai.Models;

namespace DiffusionNexus.UI.Services.CivitaiBrowser;

/// <summary>
/// Snapshot of what the local library already holds, as the two lookups every
/// browse result is tested against: installed Civitai version ids, plus installed
/// file SHA256 hashes as a fallback.
/// <para>
/// The hash fallback covers orphan DB rows whose <c>ModelVersion.CivitaiId</c> is
/// missing (legacy indexing bugs created duplicate Models without one — see the
/// "Cyberpunk Edgerunners" report) but whose file hash still matches the API
/// response.
/// </para>
/// <para>
/// The same rule answers both "does this card own anything I already have?" (the
/// card badge) and "do I already have this exact version?" (the version-picker
/// badge and the re-download prompt), so it lives in one place.
/// </para>
/// </summary>
public sealed class CivitaiInstalledIndex
{
    /// <summary>Nothing installed — the state before the first library refresh completes.</summary>
    public static CivitaiInstalledIndex Empty { get; } = new([], []);

    private readonly HashSet<int> _versionIds;
    private readonly HashSet<string> _fileHashes;

    public CivitaiInstalledIndex(IEnumerable<int> installedVersionIds, IEnumerable<string> installedFileHashes)
    {
        _versionIds = [.. installedVersionIds];
        // Hashes arrive lower-cased from the repository, but Civitai returns them
        // upper-cased — compare case-insensitively or the fallback never fires.
        _fileHashes = new HashSet<string>(installedFileHashes, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when this exact version is already in the local library.</summary>
    public bool IsInstalled(CivitaiModelVersion? version)
    {
        if (version is null) return false;
        if (_versionIds.Contains(version.Id)) return true;
        foreach (var file in version.Files)
        {
            var sha = file.Hashes?.SHA256;
            if (!string.IsNullOrEmpty(sha) && _fileHashes.Contains(sha)) return true;
        }
        return false;
    }

    /// <summary>True when ANY version of the model is already in the local library.</summary>
    public bool IsInstalled(CivitaiModel? model)
    {
        if (model is null) return false;
        foreach (var version in model.ModelVersions)
        {
            if (IsInstalled(version)) return true;
        }
        return false;
    }
}
