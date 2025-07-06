using DiffusionNexus.Service.Helper;
using System.Text.Json;

namespace ModelMover.Core.Metadata;

/// <summary>
/// Helper utilities for parsing and deriving model metadata.
/// </summary>
internal static class ModelMetadataUtils
{
    /// <summary>
    /// Parses a comma separated list of tags.
    /// </summary>
    /// <param name="rawTagString">Raw tag string (comma separated).</param>
    /// <returns>Normalized collection of tags.</returns>
    internal static IEnumerable<string> ParseTags(string? rawTagString)
    {
        if (string.IsNullOrWhiteSpace(rawTagString))
            return Enumerable.Empty<string>();

        return rawTagString
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses tags from a <see cref="JsonElement"/> array.
    /// </summary>
    /// <param name="tags">JSON array element.</param>
    /// <returns>Normalized collection of tags.</returns>
    internal static IEnumerable<string> ParseTags(JsonElement tags)
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
    /// Attempts to parse the model type token into <see cref="DiffusionTypes"/>.
    /// </summary>
    /// <param name="typeToken">Type token string.</param>
    /// <returns>The parsed <see cref="DiffusionTypes"/> value or <see cref="DiffusionTypes.UNASSIGNED"/>.</returns>
    internal static DiffusionTypes ParseModelType(string? typeToken)
    {
        if (string.IsNullOrWhiteSpace(typeToken))
            return DiffusionTypes.UNASSIGNED;

        if (Enum.TryParse(typeToken.Replace(" ", string.Empty), true, out DiffusionTypes dt))
            return dt;

        return DiffusionTypes.UNASSIGNED;
    }

    /// <summary>
    /// Removes known metadata related extensions from a file name.
    /// </summary>
    /// <param name="fileName">The file name to normalize.</param>
    /// <returns>Base file name without metadata extensions.</returns>
    internal static string ExtractBaseName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return string.Empty;

        var known = StaticFileTypes.GeneralExtensions
            .OrderByDescending(e => e.Length)
            .FirstOrDefault(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        return known != null ? fileName[..^known.Length] : fileName;
    }
}
