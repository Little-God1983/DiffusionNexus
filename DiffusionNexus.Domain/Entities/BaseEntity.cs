namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Base class for all persisted entities with standard audit fields.
/// </summary>
public abstract class BaseEntity
{
    /// <summary>
    /// Local database primary key.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// When this record was first created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When this record was last updated.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}
