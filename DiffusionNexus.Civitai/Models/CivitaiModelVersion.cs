using System.Text.Json.Serialization;

namespace DiffusionNexus.Civitai.Models;

/// <summary>
/// Represents a specific version of a model.
/// Each version can have multiple files and images.
/// </summary>
public sealed record CivitaiModelVersion
{
    /// <summary>The unique identifier for the model version.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>The parent model ID.</summary>
    [JsonPropertyName("modelId")]
    public int ModelId { get; init; }

    /// <summary>The name of this version.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The description/changelog for this version.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>When this version was created.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When this version was last updated.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>When this version was published.</summary>
    [JsonPropertyName("publishedAt")]
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>The base model this version is trained for.</summary>
    [JsonPropertyName("baseModel")]
    public string BaseModel { get; init; } = string.Empty;

    /// <summary>The base model type.</summary>
    [JsonPropertyName("baseModelType")]
    public string? BaseModelType { get; init; }

    /// <summary>The words used to trigger the model.</summary>
    [JsonPropertyName("trainedWords")]
    public IReadOnlyList<string> TrainedWords { get; init; } = [];

    /// <summary>Early access timeframe in days.</summary>
    [JsonPropertyName("earlyAccessTimeFrame")]
    public int EarlyAccessTimeFrame { get; init; }

    /// <summary>The download URL for this version's primary file.</summary>
    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; init; }

    /// <summary>Files available for this version.</summary>
    [JsonPropertyName("files")]
    public IReadOnlyList<CivitaiModelFile> Files { get; init; } = [];

    /// <summary>Preview images for this version.</summary>
    [JsonPropertyName("images")]
    public IReadOnlyList<CivitaiModelImage> Images { get; init; } = [];

    /// <summary>Statistics for this version.</summary>
    [JsonPropertyName("stats")]
    public CivitaiVersionStats? Stats { get; init; }

    /// <summary>Model info when fetching version directly.</summary>
    [JsonPropertyName("model")]
    public CivitaiVersionModelInfo? Model { get; init; }
}

/// <summary>
/// Minimal model info included in version responses.
/// </summary>
public sealed record CivitaiVersionModelInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public CivitaiModelType Type { get; init; }

    [JsonPropertyName("nsfw")]
    public bool Nsfw { get; init; }

    [JsonPropertyName("poi")]
    public bool Poi { get; init; }

    [JsonPropertyName("mode")]
    public CivitaiModelMode? Mode { get; init; }
}

/// <summary>
/// Version-specific statistics.
/// </summary>
public sealed record CivitaiVersionStats
{
    [JsonPropertyName("downloadCount")]
    public int DownloadCount { get; init; }

    [JsonPropertyName("ratingCount")]
    public int RatingCount { get; init; }

    [JsonPropertyName("rating")]
    public double Rating { get; init; }

    [JsonPropertyName("thumbsUpCount")]
    public int ThumbsUpCount { get; init; }

    [JsonPropertyName("thumbsDownCount")]
    public int ThumbsDownCount { get; init; }
}
