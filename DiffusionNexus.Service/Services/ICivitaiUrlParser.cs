namespace DiffusionNexus.Service.Services;

/// <summary>
/// Parses Civitai URLs and extracts model identifiers.
/// </summary>
public interface ICivitaiUrlParser
{
    /// <summary>
    /// Attempts to parse the supplied URL.
    /// </summary>
    /// <param name="url">The URL provided by the user.</param>
    /// <param name="info">When successful, contains the extracted identifiers.</param>
    /// <param name="normalizedUrl">The canonical HTTPS version of the URL.</param>
    /// <param name="error">An error message describing why parsing failed.</param>
    /// <returns><c>true</c> when the URL is valid.</returns>
    bool TryParse(string? url, out CivitaiLinkInfo info, out string? normalizedUrl, out string? error);
}
