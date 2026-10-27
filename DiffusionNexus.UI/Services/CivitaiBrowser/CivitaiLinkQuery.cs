using System.Text.RegularExpressions;

namespace DiffusionNexus.UI.Services.CivitaiBrowser;

/// <summary>
/// What the Browse Civitai search box holds when it is not a name search: a Civitai
/// link or a bare number (issue #630). A link names a model and/or a version, or an image
/// (<c>/images/N</c>, issue #628 — the recipe view); a bare number is ambiguous — model ids and
/// version ids are separate number ranges — so it is looked up as both, and never as an image.
/// </summary>
public sealed record CivitaiLinkQuery
{
    private CivitaiLinkQuery(int? modelId, int? versionId, bool isBareId, long? imageId = null)
    {
        ModelId = modelId;
        VersionId = versionId;
        IsBareId = isBareId;
        ImageId = imageId;
    }

    /// <summary>Model id from <c>/models/N</c>, or the bare number.</summary>
    public int? ModelId { get; }

    /// <summary>Version id from <c>?modelVersionId=N</c>, or the bare number.</summary>
    public int? VersionId { get; }

    /// <summary>True for a plain number: <see cref="ModelId"/> and <see cref="VersionId"/> are the same candidate.</summary>
    public bool IsBareId { get; }

    /// <summary>Image id from an <c>/images/N</c> link; never set for a bare number.</summary>
    public long? ImageId { get; }

    /// <summary>A link to one Civitai image: the browser shows its recipe instead of cards.</summary>
    public bool IsImageLink => ImageId is not null;

    /// <summary>A Civitai link that names no model, no version and no image (a user page, a post).</summary>
    public bool IsLinkWithoutIds => !IsBareId && ModelId is null && VersionId is null && ImageId is null;

    /// <summary>
    /// Recognises a bare number or a link to any Civitai host (civitai.com, civitai.red, …),
    /// with or without the scheme. Everything else is a name search and returns false.
    /// </summary>
    public static bool TryParse(string? text, out CivitaiLinkQuery? query)
    {
        query = null;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0) return false;

        if (Regex.IsMatch(trimmed, @"^\d+$"))
        {
            if (!int.TryParse(trimmed, out var id) || id <= 0) return false;
            query = new CivitaiLinkQuery(id, id, isBareId: true);
            return true;
        }

        if (trimmed.Any(char.IsWhiteSpace)) return false;

        var candidate = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
        if (!IsCivitaiHost(uri.Host)) return false;

        var image = Regex.Match(uri.AbsolutePath, @"^/images/(\d+)/?$", RegexOptions.IgnoreCase);
        if (image.Success && long.TryParse(image.Groups[1].Value, out var imageId) && imageId > 0)
        {
            query = new CivitaiLinkQuery(null, null, isBareId: false, imageId);
            return true;
        }

        CivitaiUrlParser.TryResolveIds(trimmed, out var modelId, out var versionId, out _);
        query = new CivitaiLinkQuery(modelId, versionId, isBareId: false);
        return true;
    }

    /// <summary>
    /// "civitai" must be a domain label, not the top-level one: a bare word "civitai" typed as a
    /// name search parses as host "civitai" and must stay a name search.
    /// </summary>
    private static bool IsCivitaiHost(string host)
    {
        var labels = host.Split('.');
        return labels.Length >= 2
               && labels[..^1].Any(label => label.Equals("civitai", StringComparison.OrdinalIgnoreCase));
    }

    public override string ToString() =>
        IsBareId ? $"ID {ModelId}"
        : IsImageLink ? $"image link (image {ImageId})"
        : $"link (model {ModelId?.ToString() ?? "-"}, version {VersionId?.ToString() ?? "-"})";
}
