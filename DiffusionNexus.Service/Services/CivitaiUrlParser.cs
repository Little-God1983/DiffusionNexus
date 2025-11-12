using System.Text.RegularExpressions;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Regex-based implementation for parsing Civitai model URLs.
/// </summary>
public sealed partial class CivitaiUrlParser : ICivitaiUrlParser
{
    private static readonly string[] AllowedHosts = ["civitai.com", "www.civitai.com"];

    /// <inheritdoc />
    public bool TryParse(string? url, out CivitaiLinkInfo info, out string? normalizedUrl, out string? error)
    {
        info = default;
        normalizedUrl = null;
        error = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "URL is required.";
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            error = "Enter a valid URL.";
            return false;
        }

        if (!AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            error = "Only civitai.com links are supported.";
            return false;
        }

        var scheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "https" : "https";
        var builder = new UriBuilder(uri)
        {
            Scheme = scheme,
            Port = scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? -1 : uri.Port
        };

        var match = ModelPathRegex().Match(uri.AbsolutePath);
        if (!match.Success)
        {
            error = "URL must be in the form https://civitai.com/models/<id>.";
            return false;
        }

        if (!int.TryParse(match.Groups["modelId"].Value, out var modelId))
        {
            error = "Model id must be numeric.";
            return false;
        }

        int? versionId = null;
        var versionMatch = VersionRegex().Match(uri.Query);
        if (versionMatch.Success)
        {
            if (!int.TryParse(versionMatch.Groups["versionId"].Value, out var parsedVersion))
            {
                error = "Model version id must be numeric.";
                return false;
            }

            versionId = parsedVersion;
        }

        info = new CivitaiLinkInfo(modelId, versionId);
        normalizedUrl = builder.Uri.ToString();
        return true;
    }

    [GeneratedRegex("^/models/(?<modelId>\\d+)(/[^?]+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ModelPathRegex();

    [GeneratedRegex("(?:[?&])modelVersionId=(?<versionId>\\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VersionRegex();
}
