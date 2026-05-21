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

    /// <summary>
    /// Cursor token for the next page. Civitai treats this as opaque — for the
    /// <c>/models</c> endpoint it's typically the id of the first item of the
    /// next page (numeric), but other endpoints return composite/string cursors.
    /// Always passed back verbatim in the <c>cursor=</c> query param.
    /// </summary>
    [JsonPropertyName("nextCursor")]
    [JsonConverter(typeof(NextCursorJsonConverter))]
    public string? NextCursor { get; init; }
}

/// <summary>
/// Accepts <c>nextCursor</c> as either a JSON number or string and normalizes
/// to <see cref="string"/>. Civitai's response varies by endpoint.
/// </summary>
internal sealed class NextCursorJsonConverter : System.Text.Json.Serialization.JsonConverter<string?>
{
    public override string? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            System.Text.Json.JsonTokenType.String => reader.GetString(),
            System.Text.Json.JsonTokenType.Number when reader.TryGetInt64(out var l) => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            System.Text.Json.JsonTokenType.Number => reader.GetDouble().ToString("G17", System.Globalization.CultureInfo.InvariantCulture),
            System.Text.Json.JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, string? value, System.Text.Json.JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
