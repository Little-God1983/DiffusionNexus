using System;
using System.Text.RegularExpressions;

namespace DiffusionNexus.UI.Classes;

public class CivitaiUrlParser : ICivitaiUrlParser
{
    private static readonly Regex PathRegex = new(
        "^https?://(?:www\\.)?civitai\\.com/models/(?<modelId>\\d+)(?:/[^?]+)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VersionRegex = new(
        "(?:[?&])modelVersionId=(?<versionId>\\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool TryParse(string? url, out CivitaiLinkInfo? info, out string? errorMessage)
    {
        info = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            errorMessage = "Please enter a Civitai URL.";
            return false;
        }

        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url.Substring("http://".Length);
        }

        var match = PathRegex.Match(url);
        if (!match.Success)
        {
            errorMessage = "Enter a valid civitai.com/models link.";
            return false;
        }

        if (!int.TryParse(match.Groups["modelId"].Value, out var modelId))
        {
            errorMessage = "Model ID must be numeric.";
            return false;
        }

        int? versionId = null;
        var versionMatch = VersionRegex.Match(url);
        if (versionMatch.Success &&
            int.TryParse(versionMatch.Groups["versionId"].Value, out var parsedVersion))
        {
            versionId = parsedVersion;
        }

        info = new CivitaiLinkInfo(modelId, versionId);
        return true;
    }
}
