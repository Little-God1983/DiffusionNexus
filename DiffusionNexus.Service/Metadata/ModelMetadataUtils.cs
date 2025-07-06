using DiffusionNexus.Service.Helper;
using System.Text.Json;

namespace ModelMover.Core.Metadata;

/// <summary>
/// Shared helpers for parsing model metadata.
/// </summary>
internal static class ModelMetadataUtils
{
    /// <summary>
    /// Parses a comma separated list of tags.
    /// </summary>
    /// <param name="rawTagString">String containing tags.</param>
    /// <returns>Collection of tags without empty entries.</returns>
    public static IEnumerable<string> ParseTags(string rawTagString)
    {
        if (string.IsNullOrWhiteSpace(rawTagString))
            return Array.Empty<string>();

        return rawTagString
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
    }

    /// <summary>
    /// Parses a JSON array of tags.
    /// </summary>
    /// <param name="tags">JSON element representing an array.</param>
    /// <returns>List of tags.</returns>
    public static List<string> ParseTags(JsonElement tags)
    {
        var result = new List<string>();
        foreach (var t in tags.EnumerateArray())
        {
            if (t.ValueKind == JsonValueKind.String)
            {
                var s = t.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    result.Add(s);
            }
        }
        return result;
    }

    /// <summary>
    /// Converts a type token to <see cref="DiffusionTypes"/>.
    /// </summary>
    public static DiffusionTypes ParseModelType(string? typeToken)
    {
        if (string.IsNullOrWhiteSpace(typeToken))
            return DiffusionTypes.UNASSIGNED;
        if (Enum.TryParse(typeToken.Replace(" ", string.Empty), true, out DiffusionTypes dt))
            return dt;
        return DiffusionTypes.UNASSIGNED;
    }

    /// <summary>
    /// Extracts the base name from a file name removing known extensions.
    /// </summary>
    /// <param name="fileName">File name to process.</param>
    /// <returns>Base name without extension.</returns>
    public static string ExtractBaseName(string fileName)
    {
        var extension = StaticFileTypes.GeneralExtensions
            .OrderByDescending(e => e.Length)
            .FirstOrDefault(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        return extension != null ? fileName[..^extension.Length] : fileName;
    }
}
