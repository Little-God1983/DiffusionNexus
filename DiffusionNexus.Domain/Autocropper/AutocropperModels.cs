namespace DiffusionNexus.Domain.Autocropper;

/// <summary>
/// Configuration for the autocropper feature containing bucket and resolution definitions.
/// </summary>
public class AutocropperConfiguration
{
    public List<BucketDefinition> Buckets { get; set; } = [];
    public List<ResolutionDefinition> Resolutions { get; set; } = [];
}

/// <summary>
/// Defines an aspect ratio bucket for image cropping.
/// Used in LoRA training to standardize image dimensions.
/// </summary>
public class BucketDefinition
{
    /// <summary>
    /// Display name for the bucket (e.g., "16:9", "1:1").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Width component of the aspect ratio.
    /// </summary>
    public double Width { get; set; }

    /// <summary>
    /// Height component of the aspect ratio.
    /// </summary>
    public double Height { get; set; }

    /// <summary>
    /// Calculated aspect ratio (Width / Height).
    /// </summary>
    public double Ratio => Height != 0 ? Width / Height : 0;

    public override string ToString() => Name;
}

/// <summary>
/// Defines a target resolution option for image scaling.
/// </summary>
public class ResolutionDefinition
{
    /// <summary>
    /// Display name for the resolution (e.g., "512px", "1024px").
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Maximum size for the longest side in pixels.
    /// Null means no scaling.
    /// </summary>
    public int? MaxSize { get; set; }
}
