using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Represents a preview image for a model version.
/// </summary>
public class ModelImage
{
    /// <summary>Local database ID.</summary>
    public int Id { get; set; }

    /// <summary>Civitai image ID.</summary>
    public long? CivitaiId { get; set; }

    /// <summary>Parent model version ID.</summary>
    public int ModelVersionId { get; set; }

    /// <summary>Image URL on Civitai.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Whether the image is NSFW.</summary>
    public bool IsNsfw { get; set; }

    /// <summary>NSFW level classification.</summary>
    public NsfwLevel NsfwLevel { get; set; } = NsfwLevel.None;

    /// <summary>Image width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Image height in pixels.</summary>
    public int Height { get; set; }

    /// <summary>BlurHash for placeholder display.</summary>
    public string? BlurHash { get; set; }

    /// <summary>When the image was created.</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>Post ID the image belongs to.</summary>
    public int? PostId { get; set; }

    /// <summary>Username of the image creator.</summary>
    public string? Username { get; set; }

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

    #region Local Cache

    /// <summary>Local cached file path.</summary>
    public string? LocalCachePath { get; set; }

    /// <summary>Whether the local cache is valid.</summary>
    public bool IsLocalCacheValid { get; set; }

    #endregion

    #region Navigation Properties

    public ModelVersion? ModelVersion { get; set; }

    #endregion

    #region Computed Properties

    /// <summary>Aspect ratio of the image.</summary>
    public double AspectRatio => Height > 0 ? (double)Width / Height : 1.0;

    /// <summary>Whether this is a portrait image.</summary>
    public bool IsPortrait => Height > Width;

    /// <summary>Whether this is a landscape image.</summary>
    public bool IsLandscape => Width > Height;

    #endregion
}
