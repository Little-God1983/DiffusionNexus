using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Models;

/// <summary>
/// Metadata for a single training run within a dataset version.
/// Serialized as part of the dataset's config.json.
/// </summary>
public class TrainingRunInfo
{
    /// <summary>
    /// Display name (also used as folder name under TrainingRuns/).
    /// Example: "SDXL_MyCharacter", "Flux_MyCharacter"
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional base model identifier (e.g., "SDXL", "Flux", "SD 1.5", "Pony").
    /// </summary>
    public string? BaseModel { get; set; }

    /// <summary>
    /// When this training run was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// Optional short description or training parameters summary.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Legacy single trigger word. Use <see cref="TriggerWords"/> instead.
    /// Kept for backward compatibility during deserialization; migrated on load.
    /// </summary>
    [Obsolete("Use TriggerWords list instead. Will be removed in a future release.")]
    public string? TriggerWord { get; set; }

    // ── Civitai Upload Profile ──────────────────────────────────────

    /// <summary>
    /// Display name for the model on Civitai (e.g., "My Character LoRA").
    /// Distinct from the folder name.
    /// </summary>
    public string? ModelDisplayName { get; set; }

    /// <summary>
    /// Civitai category for the model (Character, Style, Concept, etc.).
    /// </summary>
    public CivitaiCategory Category { get; set; } = CivitaiCategory.Unknown;

    /// <summary>
    /// Version label for the upload (defaults to "V1").
    /// </summary>
    public string VersionName { get; set; } = "V1";

    /// <summary>
    /// Comma-separated trigger words used to activate this LoRA/model.
    /// Maps to Civitai's trainedWords field.
    /// </summary>
    public List<string> TriggerWords { get; set; } = [];

    /// <summary>
    /// User-defined tags for discoverability on Civitai.
    /// </summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Number of training epochs (for Civitai training parameters).
    /// </summary>
    public int? TrainingEpochs { get; set; }

    /// <summary>
    /// Number of training steps (for Civitai training parameters).
    /// </summary>
    public int? TrainingSteps { get; set; }
}
