using System.Security.Cryptography;

namespace DiffusionNexus.Service.Services.Sync;

/// <summary>
/// The one SHA256 implementation the library-sync pipeline uses (#521 decision D2).
/// </summary>
/// <remarks>
/// Digests are emitted as <b>uppercase</b> hex, which is what <c>ModelFile.HashSHA256</c>
/// stores and what Civitai returns from its hash lookup. Hash <i>comparisons</i> elsewhere
/// stay <see cref="StringComparison.OrdinalIgnoreCase"/> because rows written by older
/// builds can still hold lowercase digests.
/// </remarks>
public static class FileHasher
{
    // Matches the async FileStream buffer used by ModelFileSyncService — model files are large.
    private const int BufferSize = 81920;

    /// <summary>Uppercase hex SHA256 of the whole file. Blocking; prefer <see cref="Sha256UpperAsync"/>.</summary>
    public static string Sha256Upper(string path)
    {
        using var sha256 = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: false);
        return Convert.ToHexString(sha256.ComputeHash(stream));
    }

    /// <summary>Uppercase hex SHA256 of the whole file.</summary>
    public static async Task<string> Sha256UpperAsync(string path, CancellationToken ct = default)
    {
        using var sha256 = SHA256.Create();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var hash = await sha256.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// The stored spelling of a SHA256 digest that came from somewhere else — uppercase hex, or
    /// null. Every write to <c>ModelFile.HashSHA256</c> goes through this.
    /// </summary>
    /// <remarks>
    /// Civitai and the sidecars return lowercase digests, so an applier that stored them verbatim
    /// re-introduced exactly the mixed casing the recovery pass exists to clean up — and one
    /// lowercase row is enough to make that pass do real work on every launch. SHA256 only: the
    /// other hash columns carry no such invariant, and nothing compares them with SQL equality.
    /// </remarks>
    public static string? NormalizeSha256(string? hash) => hash?.ToUpperInvariant();

    /// <summary>True when <paramref name="hash"/> is a syntactically complete SHA256 digest (64 hex chars).</summary>
    public static bool IsSha256(string? hash)
    {
        if (hash is null || hash.Length != 64) return false;
        foreach (var c in hash)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex) return false;
        }
        return true;
    }
}
