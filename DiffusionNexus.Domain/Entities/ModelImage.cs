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

    /// <summary>Image URL on Civitai (source of truth for re-download).</summary>
    public string Url { get; set; } = string.Empty;

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

    #region Computed Properties

    /// <summary>Aspect ratio of the original image.</summary>
    public double AspectRatio => Height > 0 ? (double)Width / Height : 1.0;

    /// <summary>Whether this is a portrait image.</summary>
    public bool IsPortrait => Height > Width;

    /// <summary>Whether this is a landscape image.</summary>
    public bool IsLandscape => Width > Height;

    /// <summary>Whether a thumbnail is available for instant display.</summary>
    public bool HasThumbnail => ThumbnailData is { Length: > 0 };

    /// <summary>Whether a full-resolution cached image is available.</summary>
    public bool HasLocalCache => IsLocalCacheValid && !string.IsNullOrEmpty(LocalCachePath);

    /// <summary>Whether this is the primary image (first in sort order).</summary>
    public bool IsPrimary => SortOrder == 0;

    /// <summary>Thumbnail size in KB for display.</summary>
    public double ThumbnailSizeKB => ThumbnailData?.Length / 1024.0 ?? 0;

    #endregion
}
