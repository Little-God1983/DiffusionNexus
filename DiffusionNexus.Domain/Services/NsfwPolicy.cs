using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// The single definition of "safe" for Civitai-rated content (#631): a rating is safe when it
/// carries only PG and PG-13. The Installed tab, the Browse tab and every writer of
/// <see cref="Entities.ModelImage.NsfwLevel"/> ask this class — before it existed, three places
/// answered with three different thresholds.
/// </summary>
/// <remarks>
/// This is not the gallery's <see cref="ContentRatingPolicy"/>: that one judges tagger labels on
/// local images, a different scale from a different source.
/// </remarks>
public static class NsfwPolicy
{
    /// <summary>The levels shown with "Show NSFW" off: PG and PG-13 (owner decision, 2026-10-23).</summary>
    public const NsfwLevel SafeLevels = NsfwLevel.PG | NsfwLevel.PG13;

    /// <summary>
    /// True when the rating carries only safe levels. Unrated (<see cref="NsfwLevel.None"/>) is not
    /// safe: a wrongly hidden safe picture is better than a wrongly shown adult one.
    /// </summary>
    public static bool IsSafe(NsfwLevel level) => level != NsfwLevel.None && (level & ~SafeLevels) == 0;

    /// <summary>The same rule for a raw API value; null and 0 are unrated.</summary>
    public static bool IsSafe(int? level) => IsSafe((NsfwLevel)(level ?? 0));

    /// <summary>
    /// Whether an image must stay out of sight with "Show NSFW" off. A rated image follows its
    /// rating. An unrated image is NSFW when it came from the web — a Civitai image whose rating was
    /// never stored, which the next metadata sync fills in — and safe when it is a local file or a
    /// thumbnail the user uploaded, which nothing can rate and the user chose themselves.
    /// </summary>
    public static bool IsNsfwImage(NsfwLevel level, string? url)
    {
        if (level != NsfwLevel.None) return !IsSafe(level);
        return IsRemote(url);
    }

    /// <summary>
    /// Maps the rating strings older Civitai payloads (and sidecars written from them) carried
    /// before the bitmask: None → PG, Soft → PG-13, Mature → R, X → X. Anything else is unrated.
    /// </summary>
    public static NsfwLevel FromLegacyRating(string? legacy) => legacy?.Trim().ToLowerInvariant() switch
    {
        "none" => NsfwLevel.PG,
        "soft" => NsfwLevel.PG13,
        "mature" => NsfwLevel.R,
        "x" => NsfwLevel.X,
        _ => NsfwLevel.None,
    };

    private static bool IsRemote(string? url) =>
        url is not null
        && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
}
