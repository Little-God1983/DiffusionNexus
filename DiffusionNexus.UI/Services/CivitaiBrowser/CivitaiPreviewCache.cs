using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DiffusionNexus.UI.Services.CivitaiBrowser;

/// <summary>
/// On-disk cache for FFmpeg-extracted video preview thumbnails used by the Civitai
/// browser. Avoids paying the multi-MB video download + FFmpeg invocation cost every
/// time the same model resurfaces in a search.
/// </summary>
/// <remarks>
/// <para>
/// Image previews are <em>not</em> cached here — they're small (resized via the CDN's
/// <c>width=450</c> transform), decode in milliseconds, and benefit from the CDN's
/// own HTTP caching.
/// </para>
/// <para>
/// Eviction: simple count cap. Once per process the cache is scanned and any files
/// beyond <see cref="MaxFiles"/> (ordered by mtime, oldest first) are deleted. Reads
/// touch the file's mtime so frequently-viewed thumbnails survive eviction (LRU-ish).
/// </para>
/// </remarks>
public static class CivitaiPreviewCache
{
    private const int MaxFiles = 500;

    private static int _evictionRunning;

    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiffusionNexus", "Cache", "civitai-previews");

    /// <summary>
    /// Builds a stable cache key. Prefers Civitai's globally-unique image id; falls
    /// back to a short SHA-1 of the URL when an id isn't available.
    /// </summary>
    public static string ComputeKey(long? civitaiImageId, string? url)
    {
        if (civitaiImageId is long id && id > 0)
            return id.ToString(CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(url)) return "unknown";

        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Returns cached bytes for <paramref name="key"/> or <c>null</c> on a miss /
    /// I/O error. Touches the file's mtime so recently-read thumbnails are
    /// preserved by eviction.
    /// </summary>
    public static async Task<byte[]?> TryGetAsync(string key, CancellationToken ct)
    {
        try
        {
            var path = Path.Combine(CacheDir, $"{key}.webp");
            if (!File.Exists(path)) return null;
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { /* best-effort */ }
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Persists <paramref name="bytes"/> to the cache under <paramref name="key"/>.
    /// Uses temp + atomic rename so partial writes never become visible. Triggers
    /// a one-shot background eviction the first time the cap is exceeded.
    /// </summary>
    public static async Task PutAsync(string key, byte[] bytes, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var path = Path.Combine(CacheDir, $"{key}.webp");
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            try { File.Move(tmp, path, overwrite: true); }
            catch { try { File.Delete(tmp); } catch { /* best-effort */ } }

            _ = Task.Run(EvictIfNeeded);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort cache write */ }
    }

    private static void EvictIfNeeded()
    {
        if (Interlocked.CompareExchange(ref _evictionRunning, 1, 0) != 0) return;
        try
        {
            var dir = new DirectoryInfo(CacheDir);
            if (!dir.Exists) return;
            var files = dir.GetFiles("*.webp");
            if (files.Length <= MaxFiles) return;
            var ordered = files.OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            for (var i = MaxFiles; i < ordered.Count; i++)
            {
                try { ordered[i].Delete(); } catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }
        finally { Interlocked.Exchange(ref _evictionRunning, 0); }
    }
}
