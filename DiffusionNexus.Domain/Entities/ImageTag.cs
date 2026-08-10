namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// A distinct booru-style content tag (e.g. "1girl", "outdoor", "dog") in the
/// gallery's tag vocabulary. Deliberately separate from <see cref="Tag"/>,
/// which is the unrelated LoRA/model-catalog tag entity.
/// </summary>
public class ImageTag : BaseEntity
{
    /// <summary>Tag name exactly as produced by the tagger (already lowercase/underscored). Unique.</summary>
    public string Name { get; set; } = string.Empty;

    public ICollection<ImageMediaTagAssignment> Assignments { get; set; } = new List<ImageMediaTagAssignment>();
}
