using DiffusionNexus.Civitai.Models;

namespace DiffusionNexus.Civitai;

/// <summary>
/// Query parameters for fetching models from Civitai.
/// </summary>
public sealed record CivitaiModelsQuery
{
    /// <summary>Number of results per page (1-100).</summary>
    public int? Limit { get; init; }

    /// <summary>Page number to fetch.</summary>
    public int? Page { get; init; }

    /// <summary>Search query to filter by name.</summary>
    public string? Query { get; init; }

    /// <summary>Filter by tag.</summary>
    public string? Tag { get; init; }

    /// <summary>Filter by username.</summary>
    public string? Username { get; init; }

    /// <summary>Filter by model types.</summary>
    public IReadOnlyList<CivitaiModelType>? Types { get; init; }

    /// <summary>Sort order.</summary>
    public string? Sort { get; init; }

    /// <summary>Time period for sorting.</summary>
    public CivitaiPeriod? Period { get; init; }

    /// <summary>NSFW filter. Civitai's API expects a STRING here, not a bool — typically
    /// the literal "true" to include NSFW results. We always send "true" and filter
    /// NSFW client-side instead of relying on the API to filter, so the toggle can
    /// be flipped without a re-fetch.</summary>
    public string? Nsfw { get; init; }

    /// <summary>Only include primary file.</summary>
    public bool? PrimaryFileOnly { get; init; }

    /// <summary>Filter by base model (single value, legacy).</summary>
    public string? BaseModel { get; init; }

    /// <summary>Filter by multiple base models. Each value is sent as <c>baseModels=...</c>.</summary>
    public IReadOnlyList<string>? BaseModels { get; init; }

    /// <summary>Cursor for cursor-based pagination (preferred over Page for deep result sets).
    /// Opaque string — pass back exactly what <c>CivitaiPaginationMetadata.NextCursor</c> returned.</summary>
    public string? Cursor { get; init; }

    /// <summary>Builds the query string.</summary>
    internal string ToQueryString()
    {
        var parts = new List<string>();

        if (Limit.HasValue) parts.Add($"limit={Limit.Value}");
        if (Page.HasValue) parts.Add($"page={Page.Value}");
        if (!string.IsNullOrWhiteSpace(Cursor)) parts.Add($"cursor={Uri.EscapeDataString(Cursor)}");
        if (!string.IsNullOrWhiteSpace(Query)) parts.Add($"query={Uri.EscapeDataString(Query)}");
        if (!string.IsNullOrWhiteSpace(Tag)) parts.Add($"tag={Uri.EscapeDataString(Tag)}");
        if (!string.IsNullOrWhiteSpace(Username)) parts.Add($"username={Uri.EscapeDataString(Username)}");
        // Civitai's Zod schema for `types` requires an actual array — repeated params
        // (`types=LORA&types=LoCon`), not a single comma-separated string. Sending
        // `types=LORA,LoCon` fails with HTTP 400 "expected array, received string".
        if (Types is { Count: > 0 })
        {
            foreach (var type in Types)
            {
                parts.Add($"types={type}");
            }
        }
        if (!string.IsNullOrWhiteSpace(Sort)) parts.Add($"sort={Uri.EscapeDataString(Sort)}");
        if (Period.HasValue) parts.Add($"period={Period.Value}");
        if (!string.IsNullOrWhiteSpace(Nsfw)) parts.Add($"nsfw={Uri.EscapeDataString(Nsfw)}");
        if (PrimaryFileOnly.HasValue) parts.Add($"primaryFileOnly={PrimaryFileOnly.Value.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(BaseModel)) parts.Add($"baseModel={Uri.EscapeDataString(BaseModel)}");
        // Multi-value base model filter. The REST API accepts repeated `baseModels=`
        // params (note the trailing s). The civitai.com web URL uses singular `baseModel=`
        // because that route goes through Algolia, not the public REST endpoint —
        // copying the singular form here breaks the filter.
        if (BaseModels is { Count: > 0 })
        {
            foreach (var bm in BaseModels)
            {
                if (!string.IsNullOrWhiteSpace(bm)) parts.Add($"baseModels={Uri.EscapeDataString(bm)}");
            }
        }

        return parts.Count > 0 ? string.Join("&", parts) : string.Empty;
    }
}

/// <summary>
/// Query parameters for fetching images from Civitai.
/// </summary>
public sealed record CivitaiImagesQuery
{
    /// <summary>Number of results per page (1-200).</summary>
    public int? Limit { get; init; }

    /// <summary>Page number.</summary>
    public int? Page { get; init; }

    /// <summary>Filter by post ID.</summary>
    public int? PostId { get; init; }

    /// <summary>Filter by model ID.</summary>
    public int? ModelId { get; init; }

    /// <summary>Filter by model version ID.</summary>
    public int? ModelVersionId { get; init; }

    /// <summary>Filter by username.</summary>
    public string? Username { get; init; }

    /// <summary>NSFW filter.</summary>
    public bool? Nsfw { get; init; }

    /// <summary>Sort order.</summary>
    public string? Sort { get; init; }

    /// <summary>Time period.</summary>
    public CivitaiPeriod? Period { get; init; }

    internal string ToQueryString()
    {
        var parts = new List<string>();

        if (Limit.HasValue) parts.Add($"limit={Limit.Value}");
        if (Page.HasValue) parts.Add($"page={Page.Value}");
        if (PostId.HasValue) parts.Add($"postId={PostId.Value}");
        if (ModelId.HasValue) parts.Add($"modelId={ModelId.Value}");
        if (ModelVersionId.HasValue) parts.Add($"modelVersionId={ModelVersionId.Value}");
        if (!string.IsNullOrWhiteSpace(Username)) parts.Add($"username={Uri.EscapeDataString(Username)}");
        if (Nsfw.HasValue) parts.Add($"nsfw={Nsfw.Value.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(Sort)) parts.Add($"sort={Uri.EscapeDataString(Sort)}");
        if (Period.HasValue) parts.Add($"period={Period.Value}");

        return parts.Count > 0 ? string.Join("&", parts) : string.Empty;
    }
}
