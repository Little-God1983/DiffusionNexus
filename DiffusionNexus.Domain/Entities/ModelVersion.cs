using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Represents a specific version of a model.
/// Each version can have multiple files (different formats/sizes) and preview images.
/// </summary>
public class ModelVersion : BaseEntity
{
    /// <summary>Civitai model version ID.</summary>
    public int? CivitaiId { get; set; }

    /// <summary>Parent model ID.</summary>
    public int ModelId { get; set; }

    /// <summary>The name of this version (e.g., "v1.0", "High Noise").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Version description or changelog.</summary>
    public string? Description { get; set; }

    /// <summary>The base model this version is trained for.</summary>
    public BaseModelType BaseModel { get; set; } = BaseModelType.Unknown;

    /// <summary>Original base model string from Civitai (for display).</summary>
    public string? BaseModelRaw { get; set; }

    /// <summary>When this version was published.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Download URL for the primary file.</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>Early access period in days (0 = no early access).</summary>
    public int EarlyAccessDays { get; set; }

    #region Statistics

    public int DownloadCount { get; set; }
    public int RatingCount { get; set; }
    public double Rating { get; set; }
    public int ThumbsUpCount { get; set; }
    public int ThumbsDownCount { get; set; }

    #endregion

    #region Navigation Properties

    /// <summary>The parent model.</summary>
    public Model? Model { get; set; }

    /// <summary>Files available for download.</summary>
    public ICollection<ModelFile> Files { get; set; } = new List<ModelFile>();

    /// <summary>Preview images for this version.</summary>
    public ICollection<ModelImage> Images { get; set; } = new List<ModelImage>();

    /// <summary>Trigger words for this version.</summary>
    public ICollection<TriggerWord> TriggerWords { get; set; } = new List<TriggerWord>();

    #endregion

    #region Computed Properties

    /// <summary>Gets the primary downloadable file.</summary>
    public ModelFile? PrimaryFile =>
        Files.FirstOrDefault(f => f.IsPrimary) ?? Files.FirstOrDefault();

    /// <summary>Gets the primary preview image.</summary>
    public ModelImage? PrimaryImage =>
        Images.FirstOrDefault(i => !i.IsNsfw) ?? Images.FirstOrDefault();

    /// <summary>Gets all trigger words as a single string.</summary>
    public string TriggerWordsText =>
        string.Join(", ", TriggerWords.Select(t => t.Word));

    #endregion
}
