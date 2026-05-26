using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Loads avares:// bitmap assets with full-buffer copy, retry, and cache.
/// All avares:// loads in the app must go through this helper — see issue #351.
/// </summary>
public static class SafeAssetBitmap
{
    private static readonly ConcurrentDictionary<string, Bitmap?> _cache = new();

    public static Bitmap? Load(string avaresUri)
    {
        if (string.IsNullOrEmpty(avaresUri))
            return null;

        if (_cache.TryGetValue(avaresUri, out var cached))
            return cached;

        Bitmap? result = null;
        Exception? lastEx = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var src = AssetLoader.Open(new Uri(avaresUri));
                if (src is null)
                {
                    Serilog.Log.Warning("SafeAssetBitmap: asset not found {Uri}", avaresUri);
                    break;
                }

                using var ms = new MemoryStream();
                src.CopyTo(ms);
                if (ms.Length == 0)
                {
                    lastEx = new InvalidDataException($"Empty stream for {avaresUri}");
                    if (attempt < 2) Thread.Sleep(10);
                    continue;
                }

                ms.Position = 0;
                result = new Bitmap(ms);
                break;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                if (attempt < 2) Thread.Sleep(50);
            }
        }

        if (result is null && lastEx is not null)
        {
            Serilog.Log.Error(lastEx,
                "SafeAssetBitmap: failed after retries for {Uri}", avaresUri);
        }

        _cache[avaresUri] = result;
        return result;
    }

    public static WindowIcon? LoadWindowIcon(string avaresUri)
    {
        var bmp = Load(avaresUri);
        if (bmp is null)
            return null;

        Exception? lastEx = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return new WindowIcon(bmp);
            }
            catch (Exception ex)
            {
                lastEx = ex;
                if (attempt < 2) Thread.Sleep(50);
            }
        }

        Serilog.Log.Error(lastEx,
            "SafeAssetBitmap: failed to create WindowIcon from {Uri}", avaresUri);
        return null;
    }
}
