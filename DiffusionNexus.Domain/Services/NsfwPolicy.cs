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
    /// never stored, which the next metadata sync fills in. A thumbnail the user uploaded is safe:
    /// they chose it. An unrated local preview file depends on its version: beside Civitai pictures
    /// it is a copy a downloader saved from that gallery (often the X-rated cover), so it is judged
    /// like them — not known to be safe; in a local-only version there is nothing to judge it by,
    /// and it stays shown.
    /// </summary>
    /// <param name="versionHasCivitaiImages">
    /// Whether the image's version holds Civitai (web) images — see
    /// <see cref="Entities.ModelVersion.IsImageNsfw"/>, which is the in-context answer.
    /// </param>
    public static bool IsNsfwImage(NsfwLevel level, string? url, bool versionHasCivitaiImages = false)
    {
        if (level != NsfwLevel.None) return !IsSafe(level);
        if (IsWebImage(url)) return true;
        if (url?.StartsWith(Entities.ModelImage.UserThumbnailScheme, StringComparison.OrdinalIgnoreCase) == true) return false;
        return versionHasCivitaiImages;
    }

    /// <summary>
    /// The LoRA's rating, and whether "Show NSFW" off hides it (#647) — the one answer for the
    /// grid, the unrated hint and its list. A LoRA is SFW or NSFW: the user's rating
    /// (<see cref="Entities.Model.UserNsfwRating"/>) when they gave one, otherwise Civitai's
    /// (<see cref="JudgeCivitai"/>). A LoRA neither rated is unrated, and hidden.
    /// </summary>
    public static NsfwVerdict Judge(Entities.Model model, Entities.ModelVersion? shownVersion) => model.UserNsfwRating switch
    {
        false => new NsfwVerdict(NsfwHideReason.None),
        true => new NsfwVerdict(NsfwHideReason.RatedNsfwByYou),
        null => JudgeCivitai(model, shownVersion),
    };

    /// <summary>
    /// What Civitai says, whatever the user rated: the creator's flag, then the picture the tile
    /// shows (<paramref name="shownVersion"/>'s <see cref="Entities.ModelVersion.PrimaryImage"/>).
    /// </summary>
    /// <remarks>
    /// An unrated picture is <see cref="NsfwHideReason.NotRatedYet"/> only when the bulk Download
    /// Metadata run would fetch it — the version has a Civitai id and the images step has not
    /// answered for the model yet. Otherwise Civitai was asked already (and had nothing, e.g. a
    /// deleted model) or cannot be asked: <see cref="NsfwHideReason.NoRating"/>. Promising a fix
    /// the button cannot deliver is what made the #646 hint wrong.
    /// </remarks>
    public static NsfwVerdict JudgeCivitai(Entities.Model model, Entities.ModelVersion? shownVersion)
    {
        if (model.IsNsfw) return new NsfwVerdict(NsfwHideReason.FlaggedByCreator);

        if (shownVersion?.PrimaryImage is not { } picture || !shownVersion.IsImageNsfw(picture))
            return new NsfwVerdict(NsfwHideReason.None);

        if (picture.NsfwLevel != NsfwLevel.None)
            return new NsfwVerdict(NsfwHideReason.RatedOnCivitai, picture.NsfwLevel);

        var hasUnratedWebImages = shownVersion.Images.Any(i => i.NsfwLevel == NsfwLevel.None && IsWebImage(i.Url));
        if (!hasUnratedWebImages)
        {
            // An unrated local preview beside a gallery Civitai rated NSFW throughout: the gallery's
            // mildest rating is the honest reason.
            var mildest = shownVersion.Images
                .Where(i => i.NsfwLevel != NsfwLevel.None)
                .Select(i => i.NsfwLevel)
                .DefaultIfEmpty(NsfwLevel.None)
                .Min();
            return new NsfwVerdict(NsfwHideReason.RatedOnCivitai, mildest);
        }

        var bulkRunWillFetch = shownVersion.CivitaiId is not null && model.SyncState?.ImagesCheckedAt is null;
        return new NsfwVerdict(bulkRunWillFetch ? NsfwHideReason.NotRatedYet : NsfwHideReason.NoRating);
    }

    /// <summary>
    /// Civitai's rating of the LoRA in words ("XXX", "PG-13", "NSFW (creator's flag)", "unrated"),
    /// ignoring the user's — what <see cref="Entities.Model.OriginalNsfwRating"/> keeps when the
    /// user changes it.
    /// </summary>
    public static string CivitaiRatingLabel(Entities.Model model, Entities.ModelVersion? shownVersion)
    {
        var civitai = JudgeCivitai(model, shownVersion);
        return civitai.Reason switch
        {
            NsfwHideReason.FlaggedByCreator => "NSFW (creator's flag)",
            NsfwHideReason.RatedOnCivitai => RatingLabel(civitai.Rating),
            NsfwHideReason.None when shownVersion?.PrimaryImage is { NsfwLevel: not NsfwLevel.None } picture
                => RatingLabel(picture.NsfwLevel),
            _ => "unrated",
        };
    }

    /// <summary>The strongest level in a rating, as Civitai names it.</summary>
    public static string RatingLabel(NsfwLevel rating) => rating switch
    {
        _ when rating.HasFlag(NsfwLevel.Blocked) => "Blocked",
        _ when rating.HasFlag(NsfwLevel.XXX) => "XXX",
        _ when rating.HasFlag(NsfwLevel.X) => "X",
        _ when rating.HasFlag(NsfwLevel.R) => "R",
        _ when rating.HasFlag(NsfwLevel.PG13) => "PG-13",
        _ when rating.HasFlag(NsfwLevel.PG) => "PG",
        _ => "unrated",
    };

    /// <summary>Whether the image lives on the web (a Civitai image) rather than on this machine.</summary>
    public static bool IsWebImage(string? url) =>
        url is not null
        && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

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
}

/// <summary>Why "Show NSFW" off hides a LoRA (#647); <see cref="None"/> when it does not.</summary>
public enum NsfwHideReason
{
    None = 0,
    RatedNsfwByYou,
    FlaggedByCreator,
    RatedOnCivitai,
    NotRatedYet,
    NoRating,
}

/// <summary><see cref="NsfwPolicy.Judge"/>'s answer.</summary>
/// <param name="Reason">Why the LoRA is hidden, or <see cref="NsfwHideReason.None"/>.</param>
/// <param name="Rating">The Civitai rating behind <see cref="NsfwHideReason.RatedOnCivitai"/>.</param>
public readonly record struct NsfwVerdict(NsfwHideReason Reason, NsfwLevel Rating = NsfwLevel.None)
{
    /// <summary>Whether "Show NSFW" off hides the LoRA.</summary>
    public bool IsHidden => Reason != NsfwHideReason.None;

    /// <summary>Whether the LoRA has no rating at all — neither the user's nor Civitai's.</summary>
    public bool IsUnrated => Reason is NsfwHideReason.NotRatedYet or NsfwHideReason.NoRating;
}
