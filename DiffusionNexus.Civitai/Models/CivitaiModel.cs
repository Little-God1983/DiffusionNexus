using System.Text.Json.Serialization;

namespace DiffusionNexus.Civitai.Models;

/// <summary>
/// Represents a model from the Civitai API.
/// A model can have multiple versions (ModelVersions).
/// </summary>
public sealed record CivitaiModel
{
    /// <summary>The unique identifier for the model.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>The name of the model.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The description of the model (HTML).</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>The model type (LORA, Checkpoint, etc.).</summary>
    [JsonPropertyName("type")]
    public CivitaiModelType Type { get; init; }

    /// <summary>Whether the model is NSFW.</summary>
    [JsonPropertyName("nsfw")]
    public bool Nsfw { get; init; }

    /// <summary>Whether the model is of a person of interest.</summary>
    [JsonPropertyName("poi")]
    public bool Poi { get; init; }

    /// <summary>The tags associated with the model.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The mode the model is in (null, Archived, or TakenDown).</summary>
    [JsonPropertyName("mode")]
    public CivitaiModelMode? Mode { get; init; }

    /// <summary>The creator of the model.</summary>
    [JsonPropertyName("creator")]
    public CivitaiCreator? Creator { get; init; }

    /// <summary>Statistics about the model.</summary>
    [JsonPropertyName("stats")]
    public CivitaiModelStats? Stats { get; init; }

    /// <summary>All versions of this model.</summary>
    [JsonPropertyName("modelVersions")]
    public IReadOnlyList<CivitaiModelVersion> ModelVersions { get; init; } = [];

    // License permissions
    [JsonPropertyName("allowNoCredit")]
    public bool AllowNoCredit { get; init; }

    [JsonPropertyName("allowCommercialUse")]
    public CivitaiCommercialUse AllowCommercialUse { get; init; }

    [JsonPropertyName("allowDerivatives")]
    public bool AllowDerivatives { get; init; }

    [JsonPropertyName("allowDifferentLicense")]
    public bool AllowDifferentLicense { get; init; }
}

/// <summary>
/// Creator information.
/// </summary>
public sealed record CivitaiCreator
{
    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("image")]
    public string? Image { get; init; }
}

/// <summary>
/// Model statistics.
/// </summary>
public sealed record CivitaiModelStats
{
    [JsonPropertyName("downloadCount")]
    public int DownloadCount { get; init; }

    [JsonPropertyName("favoriteCount")]
    public int FavoriteCount { get; init; }

    [JsonPropertyName("commentCount")]
    public int CommentCount { get; init; }

    [JsonPropertyName("ratingCount")]
    public int RatingCount { get; init; }

    [JsonPropertyName("rating")]
    public double Rating { get; init; }

    [JsonPropertyName("thumbsUpCount")]
    public int ThumbsUpCount { get; init; }

    [JsonPropertyName("thumbsDownCount")]
    public int ThumbsDownCount { get; init; }
}
