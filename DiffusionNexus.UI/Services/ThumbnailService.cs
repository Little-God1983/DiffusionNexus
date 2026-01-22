using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Service for asynchronously loading and caching image thumbnails.
/// Uses an LRU cache to limit memory usage while providing fast access to recently viewed images.
/// </summary>
public interface IThumbnailService
{
    /// <summary>
    /// Loads a thumbnail asynchronously, returning from cache if available.
    /// </summary>
    /// <param name="imagePath">Path to the image file.</param>
    /// <param name="targetWidth">Target width for the thumbnail (default 340px to match card width).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The loaded bitmap, or null if loading failed.</returns>
    Task<Bitmap?> LoadThumbnailAsync(string imagePath, int targetWidth = 340, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tries to get a cached thumbnail synchronously.
    /// </summary>
    /// <param name="imagePath">Path to the image file.</param>
    /// <param name="bitmap">The cached bitmap if found.</param>
    /// <returns>True if the thumbnail was in cache.</returns>
    bool TryGetCached(string imagePath, out Bitmap? bitmap);

    /// <summary>
    /// Invalidates a specific cache entry (e.g., when image is modified).
    /// </summary>
    /// <param name="imagePath">Path to the image to invalidate.</param>
    void Invalidate(string imagePath);

    /// <summary>
    /// Clears all cached thumbnails.
    /// </summary>
    void ClearCache();

    /// <summary>
    /// Gets the current cache statistics.
    /// </summary>
    ThumbnailCacheStats GetStats();
}

/// <summary>
/// Statistics about the thumbnail cache.
/// </summary>
public record ThumbnailCacheStats(int CachedCount, int MaxSize, long EstimatedMemoryBytes);

/// <summary>
/// Implementation of IThumbnailService with LRU eviction and async loading.
/// 
/// <para>
/// <b>Important:</b> This service does NOT dispose evicted bitmaps because they may still be
/// bound to UI elements. Bitmaps are garbage collected when no longer referenced.
/// Only ClearCache() and Dispose() attempt to dispose bitmaps (when the application is shutting down).
/// </para>
/// </summary>
public sealed class ThumbnailService : IThumbnailService, IDisposable
{
    private readonly int _maxCacheSize;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _accessOrder = new();
    private readonly object _evictionLock = new();
    private readonly SemaphoreSlim _loadSemaphore;
    private bool _disposed;

    /// <summary>
    /// Creates a new ThumbnailService.
    /// </summary>
    /// <param name="maxCacheSize">Maximum number of thumbnails to cache (default 200).</param>
    /// <param name="maxConcurrentLoads">Maximum concurrent image load operations (default 4).</param>
    public ThumbnailService(int maxCacheSize = 200, int maxConcurrentLoads = 4)
    {
        _maxCacheSize = maxCacheSize;
        _loadSemaphore = new SemaphoreSlim(maxConcurrentLoads, maxConcurrentLoads);
    }

    /// <inheritdoc />
    public async Task<Bitmap?> LoadThumbnailAsync(string imagePath, int targetWidth = 340, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(imagePath))
            return null;

        // Check cache first
        if (TryGetCached(imagePath, out var cached))
            return cached;

        // Load asynchronously with throttling
        await _loadSemaphore.WaitAsync(cancellationToken);
        try
        {
            // Double-check cache after acquiring semaphore
            if (TryGetCached(imagePath, out cached))
                return cached;

            var bitmap = await Task.Run(() => LoadAndDecode(imagePath, targetWidth), cancellationToken);
            
            if (bitmap is not null)
            {
                AddToCache(imagePath, bitmap);
            }

            return bitmap;
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    /// <inheritdoc />
    public bool TryGetCached(string imagePath, out Bitmap? bitmap)
    {
        bitmap = null;
        
        if (string.IsNullOrEmpty(imagePath))
            return false;

        if (_cache.TryGetValue(imagePath, out var entry))
        {
            // Update access order for LRU
            UpdateAccessOrder(imagePath);
            bitmap = entry.Bitmap;
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public void Invalidate(string imagePath)
    {
        if (_cache.TryRemove(imagePath, out _))
        {
            lock (_evictionLock)
            {
                _accessOrder.Remove(imagePath);
            }
            // NOTE: Don't dispose the bitmap - it may still be bound to a UI element.
            // The bitmap will be garbage collected when no longer referenced.
        }
    }

    /// <inheritdoc />
    public void ClearCache()
    {
        lock (_evictionLock)
        {
            // Only dispose bitmaps during explicit cache clear (shutdown scenario)
            // Even then, be cautious - only dispose if we're disposed (app shutting down)
            if (_disposed)
            {
                foreach (var entry in _cache.Values)
                {
                    try
                    {
                        entry.Bitmap?.Dispose();
                    }
                    catch
                    {
                        // Ignore disposal errors during shutdown
                    }
                }
            }
            _cache.Clear();
            _accessOrder.Clear();
        }
    }

    /// <inheritdoc />
    public ThumbnailCacheStats GetStats()
    {
        var count = _cache.Count;
        var estimatedMemory = _cache.Values.Sum(e => EstimateBitmapMemory(e.Bitmap));
        return new ThumbnailCacheStats(count, _maxCacheSize, estimatedMemory);
    }

    /// <summary>
    /// Loads and decodes an image from disk, resizing to target width.
    /// </summary>
    private static Bitmap? LoadAndDecode(string imagePath, int targetWidth)
    {
        // Guard clauses
        if (string.IsNullOrEmpty(imagePath)) return null;

        // Create black placeholder for MP4 files to avoid decoding errors
        if (imagePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return CreateVideoPlaceholder(targetWidth);
        }
        
        try
        {
            // Verify file exists
            if (!File.Exists(imagePath)) return null;

            // Ensure valid width
            var width = targetWidth > 0 ? targetWidth : 340;

            // Use FileStream with shared read access to avoid locking issues
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            
            // Empty stream check
            if (stream.Length == 0 || !stream.CanRead) return null;
            
            // Decode with error handling
            try 
            {
                var bitmap = Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
                
                // Validate bitmap
                if (bitmap != null && bitmap.PixelSize.Width > 0)
                {
                    return bitmap;
                }
                
                bitmap?.Dispose();
                return null;
            }
            catch
            {
                // Internal decoding error
                return null;
            }
        }
        catch
        {
            // File access or generic error
            return null;
        }
    }

    /// <summary>
    /// Creates a black placeholder bitmap for video files.
    /// </summary>
    private static Bitmap CreateVideoPlaceholder(int width)
    {
        // 16:9 aspect ratio
        int height = Math.Max(1, width * 9 / 16);
        
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using var frameBuffer = bitmap.Lock();
        
        // Calculate total bytes
        int totalBytes = frameBuffer.RowBytes * height;
        var data = new byte[totalBytes];
        
        // Fill with opaque black (B=0, G=0, R=0, A=255)
        // Bgra8888 -> [B, G, R, A]
        for (int i = 3; i < data.Length; i += 4)
        {
            data[i] = 255;
        }

        Marshal.Copy(data, 0, frameBuffer.Address, totalBytes);
        
        return bitmap;
    }

    /// <summary>
    /// Adds a bitmap to the cache with LRU eviction.
    /// </summary>
    private void AddToCache(string imagePath, Bitmap bitmap)
    {
        var entry = new CacheEntry(bitmap, DateTime.UtcNow);

        lock (_evictionLock)
        {
            // Add to cache
            _cache[imagePath] = entry;
            
            // Add to access order (most recent at end)
            _accessOrder.Remove(imagePath); // Remove if exists
            _accessOrder.AddLast(imagePath);

            // Evict oldest entries if over capacity
            while (_cache.Count > _maxCacheSize && _accessOrder.First is not null)
            {
                var oldestKey = _accessOrder.First.Value;
                _accessOrder.RemoveFirst();
                
                if (_cache.TryRemove(oldestKey, out _))
                {
                    // NOTE: Don't dispose evicted bitmaps - they may still be bound to UI elements.
                    // The bitmaps will be garbage collected when no longer referenced by ViewModels.
                    // This prevents NullReferenceException in Avalonia.Controls.Image.MeasureOverride
                    // when a disposed bitmap is still bound to an Image control.
                }
            }
        }
    }

    /// <summary>
    /// Updates the access order for LRU tracking.
    /// </summary>
    private void UpdateAccessOrder(string imagePath)
    {
        lock (_evictionLock)
        {
            _accessOrder.Remove(imagePath);
            _accessOrder.AddLast(imagePath);
        }
    }

    /// <summary>
    /// Estimates memory usage of a bitmap (width * height * 4 bytes per pixel).
    /// </summary>
    private static long EstimateBitmapMemory(Bitmap? bitmap)
    {
        if (bitmap is null)
            return 0;
        
        return (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ClearCache();
        _loadSemaphore.Dispose();
    }

    private sealed record CacheEntry(Bitmap? Bitmap, DateTime LoadedAt);
}
