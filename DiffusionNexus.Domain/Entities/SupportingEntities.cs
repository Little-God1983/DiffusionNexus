namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Represents a model creator/author.
/// </summary>
public class Creator
{
    /// <summary>Local database ID.</summary>
    public int Id { get; set; }

    /// <summary>The username on Civitai.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Avatar image URL.</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>When this creator was first seen.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    #region Navigation Properties

    /// <summary>All models by this creator.</summary>
    public ICollection<Model> Models { get; set; } = new List<Model>();

    #endregion

    #region Computed Properties

    /// <summary>Total number of models by this creator.</summary>
    public int ModelCount => Models.Count;

    #endregion
}

/// <summary>
/// Represents a tag that can be associated with models.
/// </summary>
public class Tag
{
    /// <summary>Local database ID.</summary>
    public int Id { get; set; }

    /// <summary>The tag name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Normalized name for searching (lowercase, trimmed).</summary>
    public string NormalizedName { get; set; } = string.Empty;

    #region Navigation Properties

    /// <summary>Models with this tag.</summary>
    public ICollection<ModelTag> Models { get; set; } = new List<ModelTag>();

    #endregion
}

/// <summary>
/// Join table for Model-Tag many-to-many relationship.
/// </summary>
public class ModelTag
{
    public int ModelId { get; set; }
    public Model? Model { get; set; }

    public int TagId { get; set; }
    public Tag? Tag { get; set; }
}

/// <summary>
/// Represents a trigger word for a model version.
/// </summary>
public class TriggerWord
{
    /// <summary>Local database ID.</summary>
    public int Id { get; set; }

    /// <summary>Parent model version ID.</summary>
    public int ModelVersionId { get; set; }

    /// <summary>The trigger word.</summary>
    public string Word { get; set; } = string.Empty;

    /// <summary>Order in the list.</summary>
    public int Order { get; set; }

    #region Navigation Properties

    public ModelVersion? ModelVersion { get; set; }

    #endregion
}
