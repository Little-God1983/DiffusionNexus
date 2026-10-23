namespace DiffusionNexus.Service.Services.Sync.Thumbnails;

/// <summary>
/// Shared Civitai CDN URL transform rewriting. Generalises
/// <c>CivitaiResultViewModel.RewriteToResizedImageUrl</c> (the browser UI's private copy) so
/// the sync pipeline's thumbnail step and the browser never disagree on how a transform
/// segment is inserted or replaced. No-op for non-Civitai hosts.
/// </summary>
public static class CivitaiImageUrls
{
    /// <summary>Server-side resize transform for still-image thumbnails.</summary>
    public const string ThumbnailTransform = "width=450";

    /// <summary>
    /// Server-side transform for a still-frame poster extracted from a video asset.
    /// <c>transcode=true</c> is required — without it the CDN returns the original video
    /// bytes regardless of the requested file extension.
    /// </summary>
    public const string VideoPosterTransform = "width=450,anim=false,transcode=true";

    /// <summary>
    /// Replaces (or inserts) the transform segment of an <c>image.civitai.com</c> URL with
    /// <paramref name="transform"/>. Null-safe; returns non-CDN URLs unchanged.
    /// </summary>
    public static string? WithTransform(string? url, string transform)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (!url.Contains("image.civitai.com", StringComparison.OrdinalIgnoreCase)) return url;

        var queryIndex = url.IndexOf('?');
        var bare = queryIndex >= 0 ? url[..queryIndex] : url;
        var trailing = queryIndex >= 0 ? url[queryIndex..] : string.Empty;

        var lastSlash = bare.LastIndexOf('/');
        if (lastSlash <= 0) return url;

        var dirPart = bare[..lastSlash];
        var filePart = bare[(lastSlash + 1)..];

        var prevSlash = dirPart.LastIndexOf('/');
        var lastSegment = prevSlash >= 0 ? dirPart[(prevSlash + 1)..] : dirPart;
        var dirWithoutOldTransform = lastSegment.Contains('=') && prevSlash >= 0
            ? dirPart[..prevSlash]
            : dirPart;

        return $"{dirWithoutOldTransform}/{transform}/{filePart}{trailing}";
    }

    /// <summary>Rewrites a CDN URL to the standard 450px-wide still-image thumbnail transform.</summary>
    public static string? ToThumbnailUrl(string? url) => WithTransform(url, ThumbnailTransform);

    /// <summary>
    /// Rewrites a CDN video URL to a still-frame JPEG poster: swaps in
    /// <see cref="VideoPosterTransform"/> and replaces the final segment's extension with
    /// <c>.jpeg</c>. Returns <c>null</c> for non-CDN URLs — there is no poster to derive.
    /// </summary>
    public static string? ToVideoPosterUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!url.Contains("image.civitai.com", StringComparison.OrdinalIgnoreCase)) return null;

        var rewritten = WithTransform(url, VideoPosterTransform);
        if (string.IsNullOrEmpty(rewritten)) return rewritten;

        var queryIndex = rewritten.IndexOf('?');
        var bare = queryIndex >= 0 ? rewritten[..queryIndex] : rewritten;
        var trailing = queryIndex >= 0 ? rewritten[queryIndex..] : string.Empty;

        var lastSlash = bare.LastIndexOf('/');
        var dirPart = lastSlash >= 0 ? bare[..(lastSlash + 1)] : string.Empty;
        var filePart = lastSlash >= 0 ? bare[(lastSlash + 1)..] : bare;

        var dot = filePart.LastIndexOf('.');
        var fileWithoutExtension = dot >= 0 ? filePart[..dot] : filePart;

        return $"{dirPart}{fileWithoutExtension}.jpeg{trailing}";
    }
}
