using System;

namespace DiffusionNexus.UI.Models.Pipelines;

/// <summary>
/// Helpers for working with HuggingFace <c>/resolve/</c> download URLs.
/// </summary>
public static class HuggingFaceUrl
{
    /// <summary>
    /// Extracts the destination filename from a HuggingFace file URL — the last path segment,
    /// with any query string (e.g. <c>?download=true</c>) stripped. Returns an empty string if
    /// the URL cannot be parsed.
    /// </summary>
    /// <example>
    /// <c>.../resolve/main/flux-2-klein-9b-Q4_K_M.gguf?download=true</c> → <c>flux-2-klein-9b-Q4_K_M.gguf</c>
    /// </example>
    public static string GetFileName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        // Strip query/fragment first so '?download=true' never leaks into the filename.
        var withoutQuery = url.Split('?', '#')[0].TrimEnd('/');
        var lastSlash = withoutQuery.LastIndexOf('/');
        var segment = lastSlash >= 0 ? withoutQuery[(lastSlash + 1)..] : withoutQuery;

        return Uri.UnescapeDataString(segment);
    }

    /// <summary>
    /// Normalizes a HuggingFace URL into a direct-download form: rewrites <c>/blob/</c> and
    /// <c>/raw/</c> to <c>/resolve/</c> and ensures a single <c>download=true</c> query.
    /// Idempotent; non-HuggingFace URLs are returned trimmed and unchanged.
    /// </summary>
    public static string NormalizeResolveUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        var u = url.Trim();
        var isHuggingFace = u.Contains("huggingface.co", StringComparison.OrdinalIgnoreCase)
            || u.Contains("hf.co", StringComparison.OrdinalIgnoreCase);
        if (!isHuggingFace)
            return u;

        u = u.Replace("/blob/", "/resolve/", StringComparison.OrdinalIgnoreCase)
             .Replace("/raw/", "/resolve/", StringComparison.OrdinalIgnoreCase);

        if (u.Contains("/resolve/", StringComparison.OrdinalIgnoreCase)
            && !u.Contains("download=true", StringComparison.OrdinalIgnoreCase))
        {
            u += (u.Contains('?') ? "&" : "?") + "download=true";
        }

        return u;
    }
}
