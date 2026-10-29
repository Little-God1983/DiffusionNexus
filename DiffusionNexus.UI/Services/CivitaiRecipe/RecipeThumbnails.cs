using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace DiffusionNexus.UI.Services.CivitaiRecipe;

/// <summary>
/// The Recipes tab's card thumbnails (#667): decoded at card size off the UI thread and kept in the app's
/// bounded thumbnail LRU (<see cref="IThumbnailService"/>), which owns the bitmaps and lets evicted ones be
/// collected. That cache knows paths only; a re-paste can replace an original under the same name, so an
/// entry whose file was written since it was decoded is dropped first.
/// </summary>
internal sealed class RecipeThumbnails(IThumbnailService cache)
{
    /// <summary>The card is 168 px wide; an original can be a 50 MB PNG.</summary>
    internal const int DecodeWidth = 256;

    private readonly ConcurrentDictionary<string, DateTime> _decodedWritten = new(StringComparer.OrdinalIgnoreCase);

    public async Task<Bitmap?> LoadAsync(string path, CancellationToken ct)
    {
        var written = await Task.Run(() => File.GetLastWriteTimeUtc(path), ct);
        if (_decodedWritten.TryGetValue(path, out var seen) && seen != written) cache.Invalidate(path);
        _decodedWritten[path] = written;
        return await cache.LoadThumbnailAsync(path, DecodeWidth, ct);
    }
}
