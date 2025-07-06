using System.Text.Json;
using DiffusionNexus.Service.Helper;

namespace ModelMover.Core.Metadata;

/// <summary>
/// Utility helpers for parsing metadata common to multiple providers.
/// </summary>
internal static class ModelMetadataUtils
{
    /// <summary>
    /// Splits a raw tag string into individual tags.
    /// </summary>
    /// <param name="rawTagString">Input tag list using ',', ';' or new lines as delimiters.</param>
    /// <returns>Collection of normalized tag strings.</returns>
    internal static IEnumerable<string> ParseTags(string rawTagString)
    {
        if (string.IsNullOrWhiteSpace(rawTagString))
            return Array.Empty<string>();

        char[] separators = [',', ';', '\n', '\r'];
        return rawTagString
            .Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t));
    }

    /// <summary>
    /// Parses a model type token into the <see cref="DiffusionTypes"/> enumeration.
    /// </summary>
    /// <param name="typeToken">Model type string token.</param>
    /// <returns>Parsed <see cref="DiffusionTypes"/> value or <see cref="DiffusionTypes.UNASSIGNED"/> if unknown.</returns>
    internal static DiffusionTypes ParseModelType(string? typeToken)
    {
        if (string.IsNullOrWhiteSpace(typeToken))
            return DiffusionTypes.UNASSIGNED;
        return Enum.TryParse(typeToken.Replace(" ", string.Empty), true, out DiffusionTypes dt)
            ? dt
            : DiffusionTypes.UNASSIGNED;
    }

    /// <summary>
    /// Extracts the base name from a file removing known extensions.
    /// </summary>
    /// <param name="fileName">File name with extension.</param>
    /// <returns>The base name without the recognized extension.</returns>
    internal static string ExtractBaseName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return fileName;

        var known = StaticFileTypes.GeneralExtensions
            .OrderByDescending(e => e.Length)
            .FirstOrDefault(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        if (known != null)
            return fileName[..^known.Length];
        return Path.GetFileNameWithoutExtension(fileName);
    }

    /// <summary>
    /// Parses tags from a <see cref="JsonElement"/> array.
    /// </summary>
    /// <param name="tags">JSON array containing tag strings.</param>
    /// <returns>List of tags.</returns>
    internal static List<string> ParseTags(JsonElement tags)
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
}
