namespace DiffusionNexus.Domain.Services;

/// <summary>
/// The single place that decides whether a tagger content rating counts as
/// NSFW. Both the write side (<c>ImageTaggingService</c> stamping
/// <c>ImageTagResult.IsNsfw</c>) and the read side (<c>TagIndexService</c>
/// queries deriving NSFW from the stored <c>RatingLabel</c>) go through this,
/// so a policy change is one edit here and takes effect on the next query —
/// no re-index of the whole gallery required.
/// </summary>
public static class ContentRatingPolicy
{
    /// <summary>
    /// The WD14/Danbooru rating labels that count as safe. "general" is the
    /// taxonomy's fully-safe bucket; "sensitive" is included deliberately:
    /// the tagger assigns it to a large share of completely ordinary
    /// character art (an early policy that treated anything non-"general" as
    /// NSFW badged obviously-safe images and hid most of a typical gallery
    /// behind Hide NSFW). NSFW is therefore "questionable" and "explicit" —
    /// and, failing closed, anything unknown.
    /// </summary>
    public static readonly string[] SfwRatingLabels = ["general", "sensitive"];

    /// <summary>
    /// True when <paramref name="ratingLabel"/> is not one of the safe
    /// ratings. Null/empty (an unrated or corrupt row) counts as NSFW —
    /// content filtering fails closed.
    /// </summary>
    public static bool IsNsfw(string? ratingLabel)
        => ratingLabel is null
           || !SfwRatingLabels.Contains(ratingLabel, StringComparer.OrdinalIgnoreCase);
}
