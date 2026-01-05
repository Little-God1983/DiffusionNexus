using System.Text.Json.Serialization;

namespace DiffusionNexus.Civitai.Models;

/// <summary>
/// Represents a preview image for a model version.
/// </summary>
public sealed record CivitaiModelImage
{
    /// <summary>The image ID.</summary>
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    /// <summary>The image URL.</summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    /// <summary>Whether the image is NSFW.</summary>
    [JsonPropertyName("nsfw")]
    public bool Nsfw { get; init; }

    /// <summary>The NSFW level of the image.</summary>
    [JsonPropertyName("nsfwLevel")]
    public CivitaiNsfwLevel? NsfwLevel { get; init; }

    /// <summary>Image width in pixels.</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>Image height in pixels.</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary>BlurHash of the image for placeholders.</summary>
    [JsonPropertyName("hash")]
    public string? Hash { get; init; }

    /// <summary>Generation parameters for the image.</summary>
    [JsonPropertyName("meta")]
    public CivitaiImageMeta? Meta { get; init; }

    /// <summary>When the image was created.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>The post ID this image belongs to.</summary>
    [JsonPropertyName("postId")]
    public int? PostId { get; init; }

    /// <summary>The username of the image creator.</summary>
    [JsonPropertyName("username")]
    public string? Username { get; init; }

    /// <summary>Image statistics.</summary>
    [JsonPropertyName("stats")]
    public CivitaiImageStats? Stats { get; init; }
}

/// <summary>
/// Generation metadata for an image.
/// </summary>
public sealed record CivitaiImageMeta
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; }

    [JsonPropertyName("negativePrompt")]
    public string? NegativePrompt { get; init; }

    [JsonPropertyName("seed")]
    public long? Seed { get; init; }

    [JsonPropertyName("steps")]
    public int? Steps { get; init; }

    [JsonPropertyName("sampler")]
    public string? Sampler { get; init; }

    [JsonPropertyName("cfgScale")]
    public double? CfgScale { get; init; }

    [JsonPropertyName("Model")]
    public string? Model { get; init; }

    [JsonPropertyName("Model hash")]
    public string? ModelHash { get; init; }

    [JsonPropertyName("Size")]
    public string? Size { get; init; }

    [JsonPropertyName("Clip skip")]
    public string? ClipSkip { get; init; }

    [JsonPropertyName("Hires upscale")]
    public string? HiresUpscale { get; init; }

    [JsonPropertyName("Hires upscaler")]
    public string? HiresUpscaler { get; init; }

    [JsonPropertyName("Denoising strength")]
    public string? DenoisingStrength { get; init; }

    /// <summary>Resources used in generation.</summary>
    [JsonPropertyName("resources")]
    public IReadOnlyList<CivitaiImageResource>? Resources { get; init; }
}

/// <summary>
/// A resource used in image generation.
/// </summary>
public sealed record CivitaiImageResource
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("weight")]
    public double? Weight { get; init; }

    [JsonPropertyName("hash")]
    public string? Hash { get; init; }
}

/// <summary>
/// Image reaction statistics.
/// </summary>
public sealed record CivitaiImageStats
{
    [JsonPropertyName("cryCount")]
    public int CryCount { get; init; }

    [JsonPropertyName("laughCount")]
    public int LaughCount { get; init; }

    [JsonPropertyName("likeCount")]
    public int LikeCount { get; init; }

    [JsonPropertyName("dislikeCount")]
    public int DislikeCount { get; init; }

    [JsonPropertyName("heartCount")]
    public int HeartCount { get; init; }

    [JsonPropertyName("commentCount")]
    public int CommentCount { get; init; }
}
