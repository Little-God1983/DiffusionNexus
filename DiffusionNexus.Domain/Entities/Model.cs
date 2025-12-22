using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Represents a model from Civitai (e.g., a LoRA, Checkpoint).
/// A model can have multiple versions (ModelVersions).
/// This is the primary aggregate root for model data.
/// </summary>
/// <remarks>
/// Designed for EF Core:
/// - Uses int Id as primary key
/// - Has navigation properties for related entities
/// - Supports both API and local file discovery
/// </remarks>
public class Model
{
    /// <summary>Local database ID.</summary>
    public int Id { get; set; }

    /// <summary>Civitai model ID. Null if discovered locally without API match.</summary>
    public int? CivitaiId { get; set; }

    /// <summary>The name of the model.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The description (HTML content from Civitai).</summary>
    public string? Description { get; set; }

    /// <summary>The type of model (LORA, Checkpoint, etc.).</summary>
    public ModelType Type { get; set; } = ModelType.Unknown;

    /// <summary>Whether the model contains NSFW content.</summary>
    public bool IsNsfw { get; set; }

    /// <summary>Whether this is a person of interest model.</summary>
    public bool IsPoi { get; set; }

    /// <summary>Current availability mode.</summary>
    public ModelMode Mode { get; set; } = ModelMode.Available;

    /// <summary>Where the model data originated.</summary>
    public DataSource Source { get; set; } = DataSource.Unknown;

    /// <summary>When this record was first created locally.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this record was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When data was last synced from Civitai API.</summary>
    public DateTimeOffset? LastSyncedAt { get; set; }

    #region License Permissions

    public bool AllowNoCredit { get; set; }
    public CommercialUse AllowCommercialUse { get; set; } = CommercialUse.None;
    public bool AllowDerivatives { get; set; }
    public bool AllowDifferentLicense { get; set; }

    #endregion

    #region Navigation Properties

    /// <summary>The creator of the model.</summary>
    public int? CreatorId { get; set; }
    public Creator? Creator { get; set; }

    /// <summary>All versions of this model.</summary>
    public ICollection<ModelVersion> Versions { get; set; } = new List<ModelVersion>();

    /// <summary>Tags associated with this model.</summary>
    public ICollection<ModelTag> Tags { get; set; } = new List<ModelTag>();

    #endregion

    #region Computed Properties

    /// <summary>Gets the latest version of this model.</summary>
    public ModelVersion? LatestVersion =>
        Versions.OrderByDescending(v => v.CreatedAt).FirstOrDefault();

    /// <summary>Gets the total download count across all versions.</summary>
    public int TotalDownloads =>
        Versions.Sum(v => v.DownloadCount);

    /// <summary>Gets the primary preview image from the latest version.</summary>
    public ModelImage? PrimaryImage =>
        LatestVersion?.Images.FirstOrDefault(i => !i.IsNsfw) ?? LatestVersion?.Images.FirstOrDefault();

    #endregion
}
