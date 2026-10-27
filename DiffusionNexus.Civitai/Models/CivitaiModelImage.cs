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

    // No "nsfw" boolean: the API stopped sending one for images, and reading it is what stored
    // every synced picture as SFW (#631). NsfwLevel below is the rating; NsfwPolicy judges it.

    /// <summary>
    /// Browsing-level bitmask of this image: 1=PG, 2=PG13, 4=R, 8=X, 16=XXX.
    /// The API used to send the legacy string levels (None/Soft/Mature/X) but has
    /// returned this numeric scheme for a long time — the old
    /// <c>CivitaiNsfwLevel</c> enum typing silently swallowed every value.
    /// Null when the API omitted the field — or sent a word: the <c>/images</c> endpoint still sends
    /// <c>"None"</c>/<c>"X"</c> here and carries the bitmask in <see cref="BrowsingLevel"/> instead,
    /// and a word must not fail the whole page (#628).
    /// </summary>
    [JsonPropertyName("nsfwLevel")]
    [JsonConverter(typeof(TolerantNullableInt32JsonConverter))]
    public int? NsfwLevel { get; init; }

    /// <summary>
    /// Browsing-level bitmask (same scheme as <see cref="NsfwLevel"/>). Sent by the <c>/images</c>
    /// endpoint, where it is the image's rating. Null elsewhere.
    /// </summary>
    [JsonPropertyName("browsingLevel")]
    [JsonConverter(typeof(TolerantNullableInt32JsonConverter))]
    public int? BrowsingLevel { get; init; }

    /// <summary>
    /// Image width in pixels. Nullable because Civitai sends <c>null</c> for some
    /// preview media (e.g. videos whose dimensions haven't been probed yet) rather
    /// than omitting the field or defaulting to 0.
    /// </summary>
    [JsonPropertyName("width")]
    public int? Width { get; init; }

    /// <summary>Image height in pixels. See <see cref="Width"/> for why this is nullable.</summary>
    [JsonPropertyName("height")]
    public int? Height { get; init; }

    /// <summary>BlurHash of the image for placeholders.</summary>
    [JsonPropertyName("hash")]
    public string? Hash { get; init; }

    /// <summary>
    /// Generation parameters for the image. Null when absent — or unreadable: see
    /// <see cref="TolerantImageMetaJsonConverter"/>.
    /// </summary>
    [JsonPropertyName("meta")]
    [JsonConverter(typeof(TolerantImageMetaJsonConverter))]
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

    /// <summary>Media type: "image" or "video".</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Base model of the image (<c>/images</c> endpoint only).</summary>
    [JsonPropertyName("baseModel")]
    public string? BaseModel { get; init; }

    /// <summary>
    /// The model versions the image was made with — Civitai's "Resources used" (<c>/images</c>
    /// endpoint only). Present even when <see cref="Meta"/> is null.
    /// </summary>
    [JsonPropertyName("modelVersionIds")]
    public IReadOnlyList<int>? ModelVersionIds { get; init; }
}

/// <summary>
/// Generation metadata for an image. The shapes come from whatever tool posted the image, and
/// one number where a string was expected used to fail the whole model version (#627). So every
/// scalar field here and on <see cref="CivitaiImageResource"/> reads through a tolerant converter
/// (<see cref="TolerantMetaStringJsonConverter"/>, <see cref="TolerantNullableInt32JsonConverter"/>,
/// <see cref="TolerantNullableDoubleJsonConverter"/>), so a bad value costs that field only, and
/// anything still unreadable costs only this image its meta (<see cref="TolerantImageMetaJsonConverter"/>).
/// </summary>
public sealed record CivitaiImageMeta
{
    [JsonPropertyName("prompt")]
    [JsonConverter(typeof(TolerantMetaStringJsonConverter))]
    public string? Prompt { get; init; }

    [JsonPropertyName("negativePrompt")]
    [JsonConverter(typeof(TolerantMetaStringJsonConverter))]
    public string? NegativePrompt { get; init; }

    /// <summary>
    /// Generation seed. Tolerant converter: live payloads carry seeds beyond
    /// <c>Int64.MaxValue</c> (JS float64 artifacts) — those read as null
    /// instead of failing the whole model version.
    /// </summary>
    [JsonPropertyName("seed")]
    [JsonConverter(typeof(TolerantNullableInt64JsonConverter))]
    public long? Seed { get; init; }

    [JsonPropertyName("steps")]
    [JsonConverter(typeof(TolerantNullableInt32JsonConverter))]
    public int? Steps { get; init; }

    [JsonPropertyName("sampler")]
    [JsonConverter(typeof(TolerantMetaStringJsonConverter))]
    public string? Sampler { get; init; }

    [JsonPropertyName("cfgScale")]
    [JsonConverter(typeof(TolerantNullableDoubleJsonConverter))]
    public double? CfgScale { get; init; }

    [JsonPropertyName("Model")]
    [JsonConverter(typeof(TolerantMetaStringJsonConverter))]
    public string? Model { get; init; }

    [JsonPropertyName("Model hash")]
    [JsonConverter(typeof(TolerantMetaStringJsonConverter))]
    public string? ModelHash { get; init; }

    [JsonPropertyName("Size")]
    [JsonConverter(typeof(TolerantMetaStringJsonConverter))]
    public string? Size { get; init; }

    [JsonPropertyName("Clip skip")]
    [JsonConverter(typeof(TolerantMetaStringJsonConverter))]
    public string? ClipSkip { get; init; }

    [JsonPropertyName("Hires upscale")]
    [JsonConverter(typeof(TolerantMetaStringJsonConverter))]
    public string? HiresUpscale { get; init; }

    [JsonPropertyName("Hires upscaler")]
    [JsonConverter(typeof(TolerantMetaStringJsonConverter))]
    public string? HiresUpscaler { get; init; }

    [JsonPropertyName("Denoising strength")]
    [JsonConverter(typeof(TolerantMetaStringJsonConverter))]
    public string? DenoisingStrength { get; init; }

    /// <summary>Resources used in generation.</summary>
    [JsonPropertyName("resources")]
    public IReadOnlyList<CivitaiImageResource>? Resources { get; init; }
}

/// <summary>
/// A resource used in image generation. Part of the meta, so read as tolerantly — see
/// <see cref="CivitaiImageMeta"/>.
/// </summary>
public sealed record CivitaiImageResource
{
    [JsonPropertyName("name")]
    [JsonConverter(typeof(TolerantMetaStringJsonConverter))]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    [JsonConverter(typeof(TolerantMetaStringJsonConverter))]
    public string? Type { get; init; }

    [JsonPropertyName("weight")]
    [JsonConverter(typeof(TolerantNullableDoubleJsonConverter))]
    public double? Weight { get; init; }

    [JsonPropertyName("hash")]
    [JsonConverter(typeof(TolerantMetaStringJsonConverter))]
    public string? Hash { get; init; }
}

/// <summary>
/// Image reaction statistics. Counters are null on freshly posted images
/// (stats not yet computed) — the tolerant converter reads those as 0.
/// </summary>
public sealed record CivitaiImageStats
{
    [JsonPropertyName("cryCount")]
    [JsonConverter(typeof(TolerantInt32JsonConverter))]
    public int CryCount { get; init; }

    [JsonPropertyName("laughCount")]
    [JsonConverter(typeof(TolerantInt32JsonConverter))]
    public int LaughCount { get; init; }

    [JsonPropertyName("likeCount")]
    [JsonConverter(typeof(TolerantInt32JsonConverter))]
    public int LikeCount { get; init; }

    [JsonPropertyName("dislikeCount")]
    [JsonConverter(typeof(TolerantInt32JsonConverter))]
    public int DislikeCount { get; init; }

    [JsonPropertyName("heartCount")]
    [JsonConverter(typeof(TolerantInt32JsonConverter))]
    public int HeartCount { get; init; }

    [JsonPropertyName("commentCount")]
    [JsonConverter(typeof(TolerantInt32JsonConverter))]
    public int CommentCount { get; init; }
}
