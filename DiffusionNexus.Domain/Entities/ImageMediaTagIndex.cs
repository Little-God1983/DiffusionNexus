namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// One row per gallery file that has been run through the local image
/// tagger. Rebuilt incrementally: <see cref="FileSizeBytes"/> and
/// <see cref="FileLastWriteTimeUtc"/> let the indexer skip re-tagging a
/// file that hasn't changed since it was last indexed.
/// </summary>
public class ImageMediaTagIndex : BaseEntity
{
    /// <summary>Full, normalized (<see cref="Path.GetFullPath"/>) file path. Unique.</summary>
    public string FilePath { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    public DateTime FileLastWriteTimeUtc { get; set; }

    /// <summary>
    /// The argmax rating label from the tagger's own tag list (e.g. "general",
    /// "sensitive", "questionable", "explicit" — whatever that model defines).
    /// </summary>
    public string RatingLabel { get; set; } = string.Empty;

    public float RatingScore { get; set; }

    /// <summary>True when <see cref="RatingLabel"/> is not the model's safest ("general") label.</summary>
    public bool IsNsfw { get; set; }

    public DateTimeOffset IndexedAtUtc { get; set; }

    public ICollection<ImageMediaTagAssignment> TagAssignments { get; set; } = new List<ImageMediaTagAssignment>();
}
