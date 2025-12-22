using System.Text.Json.Serialization;

namespace DiffusionNexus.Civitai.Models;

/// <summary>
/// Represents a paginated response from the Civitai API.
/// </summary>
/// <typeparam name="T">The type of items in the response.</typeparam>
public sealed record CivitaiPagedResponse<T>
{
    [JsonPropertyName("items")]
    public IReadOnlyList<T> Items { get; init; } = [];

    [JsonPropertyName("metadata")]
    public CivitaiPaginationMetadata? Metadata { get; init; }
}

/// <summary>
/// Pagination metadata from Civitai API responses.
/// </summary>
public sealed record CivitaiPaginationMetadata
{
    [JsonPropertyName("totalItems")]
    public int TotalItems { get; init; }

    [JsonPropertyName("currentPage")]
    public int CurrentPage { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; init; }

    [JsonPropertyName("nextPage")]
    public string? NextPage { get; init; }

    [JsonPropertyName("prevPage")]
    public string? PrevPage { get; init; }

    [JsonPropertyName("nextCursor")]
    public long? NextCursor { get; init; }
}
