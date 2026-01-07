namespace DiffusionNexus.Domain.Autocropper;

/// <summary>
/// Specifies how images should be adjusted to fit aspect ratio buckets.
/// </summary>
public enum FitMode
{
    /// <summary>
    /// Crop the image to fit the target aspect ratio (removes pixels).
    /// </summary>
    Crop,

    /// <summary>
    /// Pad the image to fit the target aspect ratio (adds canvas).
    /// </summary>
    Pad
}

/// <summary>
/// Specifies how to fill the padding area when using Pad mode.
/// </summary>
public enum PaddingFillMode
{
    /// <summary>
    /// Fill with a solid color (default: black).
    /// </summary>
    SolidColor,

    /// <summary>
    /// Fill with white.
    /// </summary>
    White,

    /// <summary>
    /// Fill with a blurred/stretched version of the image edges.
    /// </summary>
    BlurFill,

    /// <summary>
    /// Mirror/reflect the image edges.
    /// </summary>
    Mirror
}

/// <summary>
/// Options for padding when using Pad fit mode.
/// </summary>
public class PaddingOptions
{
    /// <summary>
    /// How to fill the padding area.
    /// </summary>
    public PaddingFillMode FillMode { get; set; } = PaddingFillMode.SolidColor;

    /// <summary>
    /// The color to use for SolidColor fill mode (ARGB hex string, e.g., "#FF000000" for black).
    /// </summary>
    public string FillColor { get; set; } = "#FF000000";

    /// <summary>
    /// Blur radius for BlurFill mode (higher = more blur).
    /// </summary>
    public float BlurRadius { get; set; } = 50f;
}

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
