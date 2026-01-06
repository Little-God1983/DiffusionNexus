namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Represents a category for organizing LoRA training datasets.
/// Default categories: Character, Style, Concept.
/// Users can add custom categories via Settings.
/// </summary>
public class DatasetCategory
{
    /// <summary>
    /// Unique identifier for the category.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Category name (e.g., "Character", "Style", "Concept").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description for the category.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Display order for sorting categories.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Whether this is a system default category (cannot be deleted).
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Foreign key to AppSettings (always 1).
    /// </summary>
    public int AppSettingsId { get; set; } = 1;

    /// <summary>
    /// Navigation property to AppSettings.
    /// </summary>
    public AppSettings? AppSettings { get; set; }

    /// <summary>
    /// When the category was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
