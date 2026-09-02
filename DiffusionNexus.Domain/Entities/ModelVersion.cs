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

    /// <summary>
    /// Dead column, kept on purpose. Until #553 this stored the name of a hardcoded
    /// <c>BaseModelType</c> enum that nothing ever read; the enum is gone and
    /// <see cref="BaseModelRaw"/> is the only base-model spelling the app uses. The column
    /// itself stays (every new row gets "Unknown") because dropping it would make the schema
    /// forward-only: every build before #553 still maps this column, and its startup recovery
    /// un-stamps migrations it does not know about, so a user rolling back to an older installer
    /// would hit "no such column: BaseModel" on the first LoRA query. "Unknown" was a member of
    /// that enum, so old builds parse it. Nothing may read or write this; the EF configuration
    /// refers to it by name so no code has to reference it.
    /// </summary>
    [Obsolete("Dead column kept only for downgrade safety (#553) — use BaseModelRaw.", error: true)]
    public string BaseModel { get; set; } = "Unknown";

    /// <summary>Original base model string from Civitai (for display).</summary>
    public string? BaseModelRaw { get; set; }

    /// <summary>When this version was published.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Download URL for the primary file.</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>Early access period in days (0 = no early access).</summary>
    public int EarlyAccessDays { get; set; }

    /// <summary>
    /// True when the user has manually edited this version (e.g., trigger words).
    /// Civitai sync flows must not overwrite user-edited versions. Defaults to false.
    /// </summary>
    public bool IsUserEdited { get; set; }

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

    /// <summary>
    /// Gets the primary preview image, preferring static images over video previews.
    /// Falls back to video only when no static image is available.
    /// </summary>
    public ModelImage? PrimaryImage =>
        Images.FirstOrDefault(i => !i.IsNsfw && !i.IsVideo)
        ?? Images.FirstOrDefault(i => !i.IsVideo)
        ?? Images.FirstOrDefault(i => !i.IsNsfw)
        ?? Images.FirstOrDefault();

    /// <summary>Gets all trigger words as a single string.</summary>
    public string TriggerWordsText =>
        string.Join(", ", TriggerWords.Select(t => t.Word));

    #endregion
}
