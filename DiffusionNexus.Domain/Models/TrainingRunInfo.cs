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
}
