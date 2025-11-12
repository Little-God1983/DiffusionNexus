using System.Text.RegularExpressions;
using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.Service.Services;

public static class CivitaiLinkParser
{
    private static readonly Regex DigitRegex = new("\\d+", RegexOptions.Compiled);

    public static bool TryParse(string? link, out CivitaiLinkInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(link))
        {
            return false;
        }

        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Host.Contains("civitai.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || !segments[0].Equals("models", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? modelId = null;
        string? versionId = null;

        if (segments.Length >= 2)
        {
            modelId = ExtractFirstNumber(segments[1]);
        }

        if (segments.Length >= 4 && segments[2].StartsWith("model", StringComparison.OrdinalIgnoreCase))
        {
            versionId = ExtractFirstNumber(segments[3]);
        }

        versionId ??= TryGetQueryValue(uri, "modelVersionId");

        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        info = new CivitaiLinkInfo(modelId, versionId);
        return true;
    }

    private static string? TryGetQueryValue(Uri uri, string key)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        var trimmed = query.TrimStart('?');
        var pairs = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pair in pairs)
        {
            var kvp = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kvp.Length == 0)
            {
                continue;
            }

            if (!kvp[0].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (kvp.Length == 1)
            {
                return string.Empty;
            }

            var value = Uri.UnescapeDataString(kvp[1]);
            return ExtractFirstNumber(value);
        }

        return null;
    }

    private static string? ExtractFirstNumber(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var match = DigitRegex.Match(input);
        return match.Success ? match.Value : null;
    }
}
