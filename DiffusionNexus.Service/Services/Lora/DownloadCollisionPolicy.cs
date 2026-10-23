using DiffusionNexus.Service.Services.Sync;

namespace DiffusionNexus.Service.Services.Lora;

/// <summary>Where a download may land: the resolved path, and whether a file already
/// there is byte-identical to what would be downloaded (caller may skip the transfer).</summary>
public sealed record CollisionResolution(string TargetPath, bool ExistingContentMatches);

/// <summary>
/// The one collision policy for every Civitai download path (spec §4.4, S4). Moved from
/// CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync: Civitai file names are frequently
/// generic ("V1.safetensors"), so two unrelated models routed to the same folder collide — the
/// second download used to replace the first model's weights. When an existing file's SHA256
/// matches the expected hash it IS this download and is reused; otherwise the Civitai version id
/// is appended ({stem}_{versionId}, LoraPathBuilder.EnumerateCandidateNames convention) — unique
/// per version and stable across retries, so a suffixed target that already exists can only be
/// this same version's earlier bytes.
/// <para>
/// That last guarantee needs a REAL version id. Local-only versions map <c>CivitaiId ?? 0</c>, and
/// "{stem}_0" is shared by every one of them — two unrelated local models routed to the same folder
/// both claimed it, and the caller, seeing no content match, overwrote whatever was there. So with
/// <paramref name="versionId"/> at or below zero the version-id candidate is skipped entirely and
/// the numeric sequence (_2, _3, …) is walked instead: each existing candidate is reused only on
/// hash proof, and the first FREE name wins otherwise. An existing file we cannot prove is ours is
/// never returned — which is what makes S4 ("never overwrite someone else's bytes") total rather
/// than conditional on Civitai having given us an id.
/// </para>
/// </summary>
public static class DownloadCollisionPolicy
{
    public static async Task<CollisionResolution> ResolveAsync(
        string targetDir, string fileName, int versionId, string? expectedSha256, CancellationToken ct)
    {
        var plain = Path.Combine(targetDir, fileName);
        if (!File.Exists(plain)) return new CollisionResolution(plain, false);

        if (await MatchesAsync(plain, expectedSha256, ct).ConfigureAwait(false))
            return new CollisionResolution(plain, true);

        if (versionId > 0)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            var suffixed = Path.Combine(targetDir, $"{stem}_{versionId}{extension}");
            var suffixedMatches = File.Exists(suffixed)
                && await MatchesAsync(suffixed, expectedSha256, ct).ConfigureAwait(false);
            return new CollisionResolution(suffixed, suffixedMatches);
        }

        // No usable version id. Skip(1) because the plain name is the sequence's first candidate
        // and was just probed above — re-hashing a multi-GB file to learn the same answer twice
        // is the one thing this walk cannot afford.
        foreach (var candidate in LoraPathBuilder.EnumerateCandidateNames(fileName, null).Skip(1))
        {
            var path = Path.Combine(targetDir, candidate);
            if (!File.Exists(path)) return new CollisionResolution(path, false);
            if (await MatchesAsync(path, expectedSha256, ct).ConfigureAwait(false))
                return new CollisionResolution(path, true);
        }

        // EnumerateCandidateNames is an infinite sequence, so the loop above always returns.
        throw new InvalidOperationException("The collision-name sequence ended, which cannot happen.");
    }

    private static async Task<bool> MatchesAsync(string path, string? expectedSha256, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return false;
        try
        {
            var actual = await FileHasher.Sha256UpperAsync(path, ct).ConfigureAwait(false);
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // unreadable/locked OR access-denied — either way we can't prove it's ours, so don't overwrite it
        }
    }
}
