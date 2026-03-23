namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// LoRA training type that determines which quality checks are applicable.
/// Maps to dataset categories (Character, Concept, Style) but is a separate
/// enum to decouple the quality-checker pipeline from the persistence model.
/// </summary>
public enum LoraType
{
    /// <summary>
    /// Character-focused LoRA (specific person, character, or entity).
    /// Requires consistent trigger words and identity-preserving captions.
    /// </summary>
    Character = 0,

    /// <summary>
    /// Concept-focused LoRA (action, object, scene, composition).
    /// Focuses on describing the concept clearly in every caption.
    /// </summary>
    Concept,

    /// <summary>
    /// Style-focused LoRA (art style, color palette, rendering technique).
    /// Requires consistent style descriptors and avoids subject leakage.
    /// </summary>
    Style
}

/// <summary>
/// Severity level for issues found during dataset quality analysis.
/// </summary>
public enum IssueSeverity
{
    /// <summary>
    /// Informational observation that may not require action.
    /// </summary>
    Info = 0,

    /// <summary>
    /// Potential problem that could degrade training quality.
    /// </summary>
    Warning,

    /// <summary>
    /// Serious problem that is likely to cause training failure or very poor results.
    /// </summary>
    Critical
}

/// <summary>
/// Domain of the quality check (which aspect of the dataset is being analyzed).
/// </summary>
public enum CheckDomain
{
    /// <summary>
    /// Text-based checks on caption / tag files.
    /// </summary>
    Caption = 0,

    /// <summary>
    /// Image-based checks (resolution, quality, duplicates, etc.).
    /// </summary>
    Image
}

/// <summary>
/// Detected caption style — natural language prose vs. booru/danbooru tag lists.
/// </summary>
public enum CaptionStyle
{
    /// <summary>
    /// Style could not be determined.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Natural-language caption (full sentences).
    /// </summary>
    NaturalLanguage,

    /// <summary>
    /// Comma-separated booru-style tags.
    /// </summary>
    BooruTags,

    /// <summary>
    /// Mix of both styles detected in the same file.
    /// </summary>
    Mixed
}
