using System.Collections.Concurrent;
using DiffusionNexus.Domain.Services;
using Serilog;
using SkiaSharp;

namespace DiffusionNexus.Infrastructure.Services;

/// <summary>
/// Implementation of IImageCacheService using SkiaSharp.
/// Provides hybrid storage: thumbnails in memory/DB, full images on disk.
/// </summary>
public sealed class ImageCacheService : IImageCacheService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly bool _disposeHttpClient;
    private readonly SemaphoreSlim _downloadSemaphore;

    /// <summary>
    /// Creates a new ImageCacheService.
    /// </summary>
    /// <param name="cacheDirectory">Root directory for cached images.</param>
    /// <param name="httpClient">Optional HttpClient (creates new if null).</param>
    /// <param name="maxConcurrentDownloads">Maximum concurrent downloads.</param>
    public ImageCacheService(
        string? cacheDirectory = null,
        HttpClient? httpClient = null,
        int maxConcurrentDownloads = 4)
    {
        CacheDirectory = cacheDirectory ?? GetDefaultCacheDirectory();
        _httpClient = httpClient ?? new HttpClient();
        _disposeHttpClient = httpClient is null;
        _logger = Log.ForContext<ImageCacheService>();
        _downloadSemaphore = new SemaphoreSlim(maxConcurrentDownloads, maxConcurrentDownloads);

        // Ensure cache directory exists
        Directory.CreateDirectory(CacheDirectory);
    }

    /// <inheritdoc />
    public string CacheDirectory { get; }

    /// <inheritdoc />
    public async Task<ImageCacheResult> DownloadAndCacheAsync(
        string imageUrl,
        ImageCacheOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= ImageCacheOptions.Default;

        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return ImageCacheResult.Failed("Image URL is required");
        }

        await _downloadSemaphore.WaitAsync(cancellationToken);
        try
        {
            // Download image
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(options.DownloadTimeout);

            using var response = await _httpClient.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cts.Token);
            memoryStream.Position = 0;

            return await ProcessImageAsync(memoryStream, imageUrl, options, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ImageCacheResult.Failed("Download was cancelled or timed out");
        }
        catch (HttpRequestException ex)
        {
            _logger.Warning(ex, "Failed to download image from {Url}", imageUrl);
            return ImageCacheResult.Failed($"Download failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error downloading image from {Url}", imageUrl);
            return ImageCacheResult.Failed($"Unexpected error: {ex.Message}");
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ImageCacheResult> CreateThumbnailFromFileAsync(
        string localFilePath,
        ImageCacheOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= ImageCacheOptions.Default;

        if (!File.Exists(localFilePath))
        {
            return ImageCacheResult.Failed($"File not found: {localFilePath}");
        }

        try
        {
            await using var stream = File.OpenRead(localFilePath);
            return await ProcessImageAsync(stream, localFilePath, options with { CacheFullImage = false }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to create thumbnail from {Path}", localFilePath);
            return ImageCacheResult.Failed($"Failed to process image: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public string GetFullCachePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Relative path is required", nameof(relativePath));
        }

        return Path.Combine(CacheDirectory, relativePath);
    }

    /// <inheritdoc />
    public bool ValidateCacheFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var fullPath = GetFullCachePath(relativePath);
        return File.Exists(fullPath);
    }

    /// <inheritdoc />
    public async Task<CacheCleanupResult> CleanupAsync(
        TimeSpan maxAge,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var deleted = 0;
        long bytesFreed = 0;

        await Task.Run(() =>
        {
            foreach (var file in Directory.EnumerateFiles(CacheDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var info = new FileInfo(file);
                    if (info.LastAccessTimeUtc < cutoff.UtcDateTime)
                    {
                        bytesFreed += info.Length;
                        info.Delete();
                        deleted++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to delete cache file {File}", file);
                }
            }
        }, cancellationToken);

        _logger.Information("Cache cleanup: deleted {Count} files, freed {MB:F2} MB", deleted, bytesFreed / (1024.0 * 1024.0));

        return new CacheCleanupResult
        {
            FilesDeleted = deleted,
            BytesFreed = bytesFreed
        };
    }

    /// <inheritdoc />
    public Task<CacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var files = new List<FileInfo>();

            foreach (var file in Directory.EnumerateFiles(CacheDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(new FileInfo(file));
            }

            return new CacheStatistics
            {
                TotalFiles = files.Count,
                TotalBytes = files.Sum(f => f.Length),
                OldestFile = files.Count > 0 ? files.Min(f => new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)) : null,
                NewestFile = files.Count > 0 ? files.Max(f => new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)) : null
            };
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
        _downloadSemaphore.Dispose();
    }

    #region Private Methods

    private async Task<ImageCacheResult> ProcessImageAsync(
        Stream imageStream,
        string sourceIdentifier,
        ImageCacheOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var image = SKBitmap.Decode(imageStream)
            ?? throw new InvalidDataException("The data is not a decodable image.");

        var originalWidth = image.Width;
        var originalHeight = image.Height;

        // Create thumbnail
        var (thumbnailData, thumbnailMime, thumbWidth, thumbHeight) = CreateThumbnail(image, options);

        // Cache full image if requested
        string? relativeCachePath = null;
        long cachedFileSize = 0;

        if (options.CacheFullImage)
        {
            imageStream.Position = 0;
            (relativeCachePath, cachedFileSize) = await CacheFullImageAsync(imageStream, sourceIdentifier, cancellationToken);
        }

        return new ImageCacheResult
        {
            Success = true,
            ThumbnailData = thumbnailData,
            ThumbnailMimeType = thumbnailMime,
            ThumbnailWidth = thumbWidth,
            ThumbnailHeight = thumbHeight,
            LocalCachePath = relativeCachePath,
            CachedFileSize = cachedFileSize,
            OriginalWidth = originalWidth,
            OriginalHeight = originalHeight
        };
    }

    private (byte[] Data, string MimeType, int Width, int Height) CreateThumbnail(SKBitmap image, ImageCacheOptions options)
    {
        // Calculate thumbnail dimensions maintaining aspect ratio (fit within, downscale only)
        var (thumbWidth, thumbHeight) = CalculateThumbnailSize(image.Width, image.Height, options.ThumbnailMaxSize);

        // Resize. Skia has no Lanczos; Catmull-Rom is the closest cubic.
        SKBitmap? resized = null;
        if (thumbWidth != image.Width || thumbHeight != image.Height)
        {
            resized = image.Resize(new SKImageInfo(thumbWidth, thumbHeight, image.ColorType, image.AlphaType),
                new SKSamplingOptions(SKCubicResampler.CatmullRom))
                ?? throw new InvalidOperationException($"Thumbnail resize to {thumbWidth}x{thumbHeight} failed.");
        }

        try
        {
            var thumbnail = resized ?? image;
            using var thumbnailImage = SKImage.FromBitmap(thumbnail);

            // Try WebP first if enabled (lossy, like the previous encoder settings)
            if (options.UseWebP)
            {
                using var webp = thumbnailImage.Encode(SKEncodedImageFormat.Webp, options.ThumbnailQuality);
                if (webp is not null)
                {
                    return (webp.ToArray(), "image/webp", thumbnail.Width, thumbnail.Height);
                }
                // Fall back to JPEG
            }

            // JPEG fallback
            using var jpeg = thumbnailImage.Encode(SKEncodedImageFormat.Jpeg, options.ThumbnailQuality)
                ?? throw new InvalidOperationException("JPEG thumbnail encoding failed.");

            return (jpeg.ToArray(), "image/jpeg", thumbnail.Width, thumbnail.Height);
        }
        finally
        {
            resized?.Dispose();
        }
    }

    private async Task<(string RelativePath, long FileSize)> CacheFullImageAsync(
        Stream imageStream,
        string sourceIdentifier,
        CancellationToken cancellationToken)
    {
        // Generate a unique filename based on source
        var hash = GenerateHash(sourceIdentifier);
        var subDir = hash[..2]; // First 2 chars for subdirectory
        var fileName = $"{hash}.cache";
        var relativePath = Path.Combine(subDir, fileName);
        var fullPath = GetFullCachePath(relativePath);

        // Ensure subdirectory exists
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // Write file
        await using var fileStream = File.Create(fullPath);
        await imageStream.CopyToAsync(fileStream, cancellationToken);

        return (relativePath, fileStream.Length);
    }

    private static (int Width, int Height) CalculateThumbnailSize(int originalWidth, int originalHeight, int maxSize)
    {
        if (originalWidth <= maxSize && originalHeight <= maxSize)
        {
            return (originalWidth, originalHeight);
        }

        var ratio = (double)originalWidth / originalHeight;

        if (originalWidth > originalHeight)
        {
            return (maxSize, (int)(maxSize / ratio));
        }
        else
        {
            return ((int)(maxSize * ratio), maxSize);
        }
    }

    private static string GenerateHash(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetDefaultCacheDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "DiffusionNexus", "ImageCache");
    }

    #endregion
}
