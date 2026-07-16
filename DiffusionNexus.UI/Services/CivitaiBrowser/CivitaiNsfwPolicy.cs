using DiffusionNexus.Civitai.Models;

namespace DiffusionNexus.UI.Services.CivitaiBrowser;

/// <summary>
/// Client-side NSFW gating for the Civitai browser. The browser always requests
/// <c>nsfw=true</c> (so the toggle flips without a refetch), which also makes the
/// API return every gallery image unfiltered — so both card visibility and preview
/// choice must be gated here, using the numeric nsfwLevel bitmask
/// (1=PG, 2=PG13, 4=R, 8=X, 16=XXX), not just the narrow model-level nsfw boolean.
/// </summary>
public static class CivitaiNsfwPolicy
{
    /// <summary>PG (1) and PG13 (2) — everything Civitai shows without an NSFW opt-in.</summary>
    private const int SafeLevelMask = 0b11;

    /// <summary>
    /// True when the image is rated PG/PG13. Unrated images (null or 0) count as
    /// unsafe: with nsfw=true passthrough an unrated image could be anything, and a
    /// wrongly-shown adult thumbnail is worse than a wrongly-hidden safe one.
    /// </summary>
    public static bool IsImageSafe(CivitaiModelImage image)
        => image.NsfwLevel is int level && level != 0 && (level & ~SafeLevelMask) == 0;

    /// <summary>
    /// True when the card must be hidden while the NSFW toggle is off: the model is
    /// designated mature, or it has nothing safe to show — no PG/PG13 gallery image
    /// (falling back to the model-level bitmask when the gallery is empty).
    /// </summary>
    public static bool IsCardNsfw(CivitaiModel model)
    {
        if (model.Nsfw) return true;

        var images = AllImages(model).ToList();
        if (images.Count > 0) return !images.Any(IsImageSafe);

        // No gallery at all — trust the model-level bitmask; an unrated (0) model
        // that isn't flagged stays visible.
        return model.NsfwLevel != 0 && (model.NsfwLevel & SafeLevelMask) == 0;
    }

    /// <summary>
    /// Picks the preview image for a card. Candidates are every gallery image with a
    /// URL — restricted to safe ones when <paramref name="showNsfw"/> is off. Still
    /// images are preferred over videos (same preference the browser always had, so
    /// no CDN poster extraction is needed). Null when nothing qualifies.
    /// </summary>
    public static CivitaiModelImage? SelectPreview(CivitaiModel model, bool showNsfw)
    {
        var candidates = AllImages(model)
            .Where(i => !string.IsNullOrWhiteSpace(i.Url))
            .Where(i => showNsfw || IsImageSafe(i))
            .ToList();

        return candidates.FirstOrDefault(i => !IsVideoAsset(i)) ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// Whether a preview asset is animated. Uses the API's own <c>type</c> field first
    /// (authoritative), falling back to the URL extension for records that didn't report it.
    /// </summary>
    public static bool IsVideoAsset(CivitaiModelImage? image)
    {
        if (image is null) return false;
        if (string.Equals(image.Type, "video", StringComparison.OrdinalIgnoreCase)) return true;

        var url = image.Url;
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<CivitaiModelImage> AllImages(CivitaiModel model)
        => model.ModelVersions.SelectMany(v => v.Images);
}
