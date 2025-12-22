namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Service for downloading, caching, and managing model preview images.
/// Implements hybrid storage: thumbnails in DB, full images on disk.
/// </summary>
public interface IImageCacheService
{
    /// <summary>
    /// Downloads an image from URL and creates a thumbnail for DB storage.
    /// Optionally caches the full image to disk.
    /// </summary>
    /// <param name="imageUrl">Source URL to download from.</param>
    /// <param name="options">Caching options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing thumbnail data and cache info.</returns>
    Task<ImageCacheResult> DownloadAndCacheAsync(
        string imageUrl,
        ImageCacheOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a thumbnail from an existing local image file.
    /// </summary>
    /// <param name="localFilePath">Path to the source image.</param>
    /// <param name="options">Thumbnail options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing thumbnail data.</returns>
    Task<ImageCacheResult> CreateThumbnailFromFileAsync(
        string localFilePath,
        ImageCacheOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the root directory for image cache files.
    /// </summary>
    string CacheDirectory { get; }

    /// <summary>
    /// Gets the full path for a cached image.
    /// </summary>
    /// <param name="relativePath">Relative path stored in ModelImage.LocalCachePath.</param>
    /// <returns>Full file system path.</returns>
    string GetFullCachePath(string relativePath);

    /// <summary>
    /// Validates that a cached file exists and is readable.
    /// </summary>
    /// <param name="relativePath">Relative path to validate.</param>
    /// <returns>True if the file exists and is valid.</returns>
    bool ValidateCacheFile(string relativePath);

    /// <summary>
    /// Deletes cached files older than the specified age.
    /// </summary>
    /// <param name="maxAge">Maximum age of files to keep.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of files deleted and bytes freed.</returns>
    Task<CacheCleanupResult> CleanupAsync(
        TimeSpan maxAge,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets statistics about the image cache.
    /// </summary>
    Task<CacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for image caching operations.
/// </summary>
public sealed record ImageCacheOptions
{
    /// <summary>
    /// Maximum dimension (width or height) for thumbnails.
    /// Default is 256 pixels.
    /// </summary>
    public int ThumbnailMaxSize { get; init; } = 256;

    /// <summary>
    /// JPEG quality for thumbnail encoding (1-100).
    /// Default is 85 for good quality/size balance.
    /// </summary>
    public int ThumbnailQuality { get; init; } = 85;

    /// <summary>
    /// Whether to use WebP format for thumbnails (smaller files).
    /// Falls back to JPEG if WebP encoding fails.
    /// </summary>
    public bool UseWebP { get; init; } = true;

    /// <summary>
    /// Whether to also cache the full-resolution image to disk.
    /// </summary>
    public bool CacheFullImage { get; init; } = true;

    /// <summary>
    /// Timeout for HTTP download operations.
    /// </summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default options instance.
    /// </summary>
    public static ImageCacheOptions Default { get; } = new();
}

/// <summary>
/// Result of an image caching operation.
/// </summary>
public sealed record ImageCacheResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>Error message if the operation failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Thumbnail image data (BLOB for DB storage).</summary>
    public byte[]? ThumbnailData { get; init; }

    /// <summary>MIME type of the thumbnail.</summary>
    public string? ThumbnailMimeType { get; init; }

    /// <summary>Thumbnail width in pixels.</summary>
    public int ThumbnailWidth { get; init; }

    /// <summary>Thumbnail height in pixels.</summary>
    public int ThumbnailHeight { get; init; }

    /// <summary>Relative path to cached full image (for LocalCachePath).</summary>
    public string? LocalCachePath { get; init; }

    /// <summary>Size of the cached full image in bytes.</summary>
    public long CachedFileSize { get; init; }

    /// <summary>Original image width.</summary>
    public int OriginalWidth { get; init; }

    /// <summary>Original image height.</summary>
    public int OriginalHeight { get; init; }

    /// <summary>Creates a failed result.</summary>
    public static ImageCacheResult Failed(string error) => new()
    {
        Success = false,
        ErrorMessage = error
    };
}

/// <summary>
/// Result of a cache cleanup operation.
/// </summary>
public sealed record CacheCleanupResult
{
    /// <summary>Number of files deleted.</summary>
    public int FilesDeleted { get; init; }

    /// <summary>Total bytes freed.</summary>
    public long BytesFreed { get; init; }

    /// <summary>Bytes freed in megabytes.</summary>
    public double MegabytesFreed => BytesFreed / (1024.0 * 1024.0);
}

/// <summary>
/// Statistics about the image cache.
/// </summary>
public sealed record CacheStatistics
{
    /// <summary>Total number of cached files.</summary>
    public int TotalFiles { get; init; }

    /// <summary>Total size in bytes.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Total size in megabytes.</summary>
    public double TotalMegabytes => TotalBytes / (1024.0 * 1024.0);

    /// <summary>Total size in gigabytes.</summary>
    public double TotalGigabytes => TotalMegabytes / 1024.0;

    /// <summary>Oldest file in the cache.</summary>
    public DateTimeOffset? OldestFile { get; init; }

    /// <summary>Newest file in the cache.</summary>
    public DateTimeOffset? NewestFile { get; init; }
}
