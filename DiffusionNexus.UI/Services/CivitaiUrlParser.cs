using System.Text.RegularExpressions;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Parses model and version identifiers from Civitai URLs.
/// </summary>
public static class CivitaiUrlParser
{
    public static bool TryResolveIds(string? urlText, out int? modelId, out int? versionId, out string error)
    {
        modelId = null;
        versionId = null;
        error = string.Empty;

        var url = (urlText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            error = "Enter a Civitai URL.";
            return false;
        }

        var modelMatch = Regex.Match(url, @"/models/(\d+)", RegexOptions.IgnoreCase);
        if (modelMatch.Success && int.TryParse(modelMatch.Groups[1].Value, out var mId))
            modelId = mId;

        var versionMatch = Regex.Match(url, @"[?&]modelVersionId=(\d+)", RegexOptions.IgnoreCase);
        if (versionMatch.Success && int.TryParse(versionMatch.Groups[1].Value, out var vId))
            versionId = vId;

        if (modelId is null && versionId is null)
        {
            error = "Could not parse a Model ID or Model Version ID from the URL.";
            return false;
        }

        return true;
    }
}
