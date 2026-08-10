namespace DiffusionNexus.Domain.Services;

/// <summary>
/// The single place that decides whether a tagger content rating counts as
/// NSFW. Both the write side (<c>ImageTaggingService</c> stamping
/// <c>ImageTagResult.IsNsfw</c>) and the read side (<c>TagIndexService</c>
/// queries deriving NSFW from the stored <c>RatingLabel</c>) go through this,
/// so a future policy change (e.g. treating "sensitive" as SFW, or a
/// user-configurable threshold) is one edit here and takes effect on the next
/// query — no re-index of the whole gallery required.
/// </summary>
public static class ContentRatingPolicy
{
    /// <summary>
    /// The one hardcoded rating-name assumption: WD14/Danbooru's rating
    /// taxonomy has used "general" as the safest bucket since the schema's
    /// introduction ("sensitive", "questionable" and "explicit" are the
    /// others). Every other tag/rating name is read from the model's CSV.
    /// </summary>
    public const string SfwRatingLabel = "general";

    /// <summary>
    /// True when <paramref name="ratingLabel"/> is not the safest rating.
    /// Null/empty (an unrated or corrupt row) counts as NSFW — content
    /// filtering fails closed.
    /// </summary>
    public static bool IsNsfw(string? ratingLabel)
        => !string.Equals(ratingLabel, SfwRatingLabel, StringComparison.OrdinalIgnoreCase);
}
