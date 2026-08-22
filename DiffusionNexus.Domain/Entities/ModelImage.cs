using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Represents a preview image for a model version.
/// Uses hybrid storage: thumbnails in DB (BLOB), full images on disk.
/// </summary>
public class ModelImage
{
    /// <summary>Local database ID.</summary>
    public int Id { get; set; }

    /// <summary>Civitai image ID.</summary>
    public long? CivitaiId { get; set; }

    /// <summary>Parent model version ID.</summary>
    public int ModelVersionId { get; set; }

    /// <summary>
    /// The <see cref="Url"/> prefix written for a thumbnail the user uploaded themselves. Such a
    /// row is the bytes: there is nothing behind the URL to fetch, and nothing may overwrite it.
    /// Declared here, on the entity whose column carries it, because both the Service-side
    /// pipeline (<c>LocalPreviewFiles.UserThumbnailScheme</c>, which forwards to this) and the
    /// DataAccess-side candidate selection have to recognise it, and those two share only Domain.
    /// </summary>
    public const string UserThumbnailScheme = "user-thumbnail://";

    /// <summary>Image URL on Civitai (source of truth for re-download).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Media type from Civitai: "image" or "video". Null for legacy entries.</summary>
    public string? MediaType { get; set; }

    /// <summary>Whether the image is NSFW.</summary>
    public bool IsNsfw { get; set; }

    /// <summary>NSFW level classification.</summary>
    public NsfwLevel NsfwLevel { get; set; } = NsfwLevel.None;

    /// <summary>Original image width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Original image height in pixels.</summary>
    public int Height { get; set; }

    /// <summary>BlurHash for instant placeholder display.</summary>
    public string? BlurHash { get; set; }

    /// <summary>Sort order for display (0 = primary image).</summary>
    public int SortOrder { get; set; }

    /// <summary>When the image was created on Civitai.</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>Post ID the image belongs to.</summary>
    public int? PostId { get; set; }

    /// <summary>Username of the image creator.</summary>
    public string? Username { get; set; }

    #region Thumbnail Storage (BLOB in DB)

    /// <summary>
    /// Thumbnail image data stored as BLOB.
    /// Resized to fit within ThumbnailMaxSize, encoded as WebP/JPEG.
    /// Typically 20-80 KB per image for instant tile rendering.
    /// </summary>
    public byte[]? ThumbnailData { get; set; }

    /// <summary>
    /// MIME type of the thumbnail (e.g., "image/webp", "image/jpeg").
    /// </summary>
    public string? ThumbnailMimeType { get; set; }

    /// <summary>
    /// Width of the stored thumbnail in pixels.
    /// </summary>
    public int? ThumbnailWidth { get; set; }

    /// <summary>
    /// Height of the stored thumbnail in pixels.
    /// </summary>
    public int? ThumbnailHeight { get; set; }

    /// <summary>When the thumbnail pipeline last tried to produce <see cref="ThumbnailData"/> for this image.</summary>
    public DateTimeOffset? ThumbnailAttemptedAt { get; set; }

    /// <summary>Why the last attempt failed — one of <see cref="ThumbnailFailureReason"/>; null after success.</summary>
    public string? ThumbnailFailure { get; set; }

    #endregion

    #region Full Image Cache (File on Disk)

    /// <summary>
    /// Local file path for the full-resolution cached image.
    /// Stored relative to the cache root directory.
    /// </summary>
    public string? LocalCachePath { get; set; }

    /// <summary>
    /// Whether the local cache file exists and is valid.
    /// </summary>
    public bool IsLocalCacheValid { get; set; }

    /// <summary>
    /// When the image was last downloaded/cached.
    /// </summary>
    public DateTimeOffset? CachedAt { get; set; }

    /// <summary>
    /// File size of the cached full image in bytes.
    /// </summary>
    public long? CachedFileSize { get; set; }

    #endregion

    #region Generation Metadata

    /// <summary>The prompt used to generate the image.</summary>
    public string? Prompt { get; set; }

    /// <summary>The negative prompt.</summary>
    public string? NegativePrompt { get; set; }

    /// <summary>The seed used.</summary>
    public long? Seed { get; set; }

    /// <summary>Number of steps.</summary>
    public int? Steps { get; set; }

    /// <summary>Sampler used.</summary>
    public string? Sampler { get; set; }

    /// <summary>CFG scale.</summary>
    public double? CfgScale { get; set; }

    /// <summary>Model used for generation.</summary>
    public string? GenerationModel { get; set; }

    /// <summary>Denoising strength if img2img.</summary>
    public double? DenoisingStrength { get; set; }

    #endregion

    #region Statistics

    public int LikeCount { get; set; }
    public int HeartCount { get; set; }
    public int CommentCount { get; set; }

    #endregion

    #region Navigation Properties

    public ModelVersion? ModelVersion { get; set; }

    #endregion

    #region Lightweight Loading Support

    /// <summary>
    /// Sentinel byte array assigned by lightweight queries to signal "a thumbnail exists in
    /// the database but the BLOB was not loaded to save memory". The tile can detect this and
    /// lazy-load the real data on demand via <c>GetImageThumbnailDataAsync</c>.
    /// </summary>
    public static readonly byte[] ThumbnailNotLoadedSentinel = [0xFF];

    /// <summary>
    /// Returns <c>true</c> when ThumbnailData is the sentinel marker, meaning a real
    /// thumbnail exists in the DB but was deliberately not loaded to save memory.
    /// </summary>
    public bool IsThumbnailDeferred =>
        ReferenceEquals(ThumbnailData, ThumbnailNotLoadedSentinel);

    #endregion

    #region Computed Properties

    /// <summary>Aspect ratio of the original image.</summary>
    public double AspectRatio => Height > 0 ? (double)Width / Height : 1.0;

    /// <summary>Whether this is a portrait image.</summary>
    public bool IsPortrait => Height > Width;

    /// <summary>Whether this is a landscape image.</summary>
    public bool IsLandscape => Width > Height;

    /// <summary>Whether a thumbnail is available for instant display.</summary>
    public bool HasThumbnail => ThumbnailData is { Length: > 0 } && !IsThumbnailDeferred;

    /// <summary>Whether the preview is a video (MP4, WebM, etc.).</summary>
    public bool IsVideo => IsVideoLike(MediaType, Url);

    /// <summary>
    /// Whether a preview described by <paramref name="mediaType"/>/<paramref name="url"/> is a
    /// video. The single source for that question: <see cref="IsVideo"/>, the tile's own video test,
    /// and the SQL-side candidate ranking are all this call.
    /// </summary>
    /// <remarks>
    /// A recorded <paramref name="mediaType"/> is an answer and the URL does not get to argue with
    /// it; the extension fallback applies only to rows that carry no answer at all. Those exist in
    /// quantity — anything imported from a <c>.civitai.info</c> sidecar without a <c>type</c> field
    /// has a null media type and, often enough, an <c>.mp4</c> URL.
    /// <para>
    /// The fallback is not cosmetic. This predicate gates rung 3 of the thumbnail ladder (the CDN
    /// poster transform, a few KB); a video row that misses it falls to rung 4, which GETs the URL
    /// and buffers the response whole — up to the client's 64 MB cap — before the byte sniffer
    /// recognises a container and hands it back to rung 3 anyway. Without this the bulk sync pulled
    /// and discarded one full preview clip per such row, every run, on the one path that is
    /// otherwise careful never to download a video nobody asked for.
    /// </para>
    /// <para>
    /// <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/>, never the throwing constructor: the
    /// database holds URLs nothing guarantees are parseable — a truncated download, a legacy
    /// relative path, an unclosed bracket. One that cannot be parsed is simply not known to be a
    /// video, which is the answer it already had.
    /// </para>
    /// </remarks>
    public static bool IsVideoLike(string? mediaType, string? url)
    {
        if (string.Equals(mediaType, "video", StringComparison.OrdinalIgnoreCase)) return true;

        if (mediaType is not null || string.IsNullOrEmpty(url)) return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        return extension is ".mp4" or ".webm" or ".mov" or ".avi" or ".mkv";
    }

    /// <summary>Whether a full-resolution cached image is available.</summary>
    public bool HasLocalCache => IsLocalCacheValid && !string.IsNullOrEmpty(LocalCachePath);

    /// <summary>Whether this is the primary image (first in sort order).</summary>
    public bool IsPrimary => SortOrder == 0;

    /// <summary>Thumbnail size in KB for display.</summary>
    public double ThumbnailSizeKB => ThumbnailData?.Length / 1024.0 ?? 0;

    #endregion
}
