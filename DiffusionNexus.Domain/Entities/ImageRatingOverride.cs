namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// A user's manual SFW/NSFW decision for one gallery file, taking precedence
/// over the tagger's automatic rating. Deliberately a separate table from
/// <see cref="ImageMediaTagIndex"/>: index rows are machine-owned — builds
/// rewrite them and Rebuild wipes them wholesale — while these rows are
/// user-owned and must survive all of that. An override whose file has no
/// index row (or no file) is a harmless orphan that re-applies if the file
/// is ever indexed again.
/// </summary>
public class ImageRatingOverride : BaseEntity
{
    /// <summary>Full, normalized (<see cref="Path.GetFullPath"/>) file path. Unique.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>The user's verdict — replaces the policy-derived rating entirely.</summary>
    public bool IsNsfw { get; set; }
}
