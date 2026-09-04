using System.Security.Cryptography;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <summary>
/// Remembers the last init image uploaded to the engine, so a batch that reuses one composited region
/// does not re-read and re-POST the same multi-megabyte PNG once per candidate.
/// </summary>
/// <remarks>
/// A hit requires the same path with the same content, plus the caller confirming the stored copy is
/// still on the server. Content is compared by hash rather than by last-write time: the canvas rewrites
/// the same path on every Generate, and a timestamp with coarse resolution could report two different
/// regions as one. Holds a single entry — the canvas writes one scratch file per Generate, and anything
/// wider would be caching for a caller that does not exist.
/// </remarks>
internal sealed class UploadedInitImageCache
{
    private string? _path;
    private byte[]? _contentHash;
    private string? _storedName;

    /// <summary>
    /// Returns the stored name for <paramref name="path"/> when its content matches the remembered upload
    /// and <paramref name="storedStillExists"/> confirms the server-side copy is still there.
    /// </summary>
    public bool TryGet(string path, Func<string, bool> storedStillExists, out string storedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(storedStillExists);

        storedName = string.Empty;
        if (_storedName is null || _contentHash is null || !string.Equals(_path, path, StringComparison.Ordinal))
            return false;

        var hash = TryHash(path);
        if (hash is null || !hash.AsSpan().SequenceEqual(_contentHash))
            return false;

        if (!storedStillExists(_storedName))
        {
            // The server no longer has it (a reinstall, a manual clean-up). Forget the entry so the next
            // call uploads again rather than naming a file the engine cannot open.
            Clear();
            return false;
        }

        storedName = _storedName;
        return true;
    }

    /// <summary>
    /// Records that <paramref name="path"/>, as its content is right now, is on the server as
    /// <paramref name="storedName"/>.
    /// </summary>
    public void Remember(string path, string storedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(storedName);

        var hash = TryHash(path);
        if (hash is null)
        {
            Clear();
            return;
        }

        _path = path;
        _contentHash = hash;
        _storedName = storedName;
    }

    public void Clear()
    {
        _path = null;
        _contentHash = null;
        _storedName = null;
    }

    private static byte[]? TryHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return SHA256.HashData(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
