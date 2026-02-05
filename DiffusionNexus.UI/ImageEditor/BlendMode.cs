using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Defines the blend modes available for layer compositing.
/// </summary>
public enum BlendMode
{
    /// <summary>Normal blend - pixels replace underlying pixels based on alpha.</summary>
    Normal,

    /// <summary>Multiply - darkens by multiplying base and blend colors.</summary>
    Multiply,

    /// <summary>Screen - lightens by inverting, multiplying, and inverting again.</summary>
    Screen,

    /// <summary>Overlay - combines Multiply and Screen based on base color.</summary>
    Overlay,

    /// <summary>Darken - keeps the darker of the base and blend colors.</summary>
    Darken,

    /// <summary>Lighten - keeps the lighter of the base and blend colors.</summary>
    Lighten,

    /// <summary>Color Dodge - brightens base color to reflect blend color.</summary>
    ColorDodge,

    /// <summary>Color Burn - darkens base color to reflect blend color.</summary>
    ColorBurn,

    /// <summary>Soft Light - applies a soft version of Hard Light.</summary>
    SoftLight,

    /// <summary>Hard Light - combines Multiply and Screen based on blend color.</summary>
    HardLight,

    /// <summary>Difference - subtracts darker from lighter color.</summary>
    Difference,

    /// <summary>Exclusion - similar to Difference but with lower contrast.</summary>
    Exclusion
}

/// <summary>
/// Extension methods for BlendMode conversion to SkiaSharp types.
/// </summary>
public static class BlendModeExtensions
{
    /// <summary>
    /// Converts a BlendMode to the corresponding SKBlendMode.
    /// </summary>
    public static SKBlendMode ToSKBlendMode(this BlendMode mode) => mode switch
    {
        BlendMode.Normal => SKBlendMode.SrcOver,
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.Overlay => SKBlendMode.Overlay,
        BlendMode.Darken => SKBlendMode.Darken,
        BlendMode.Lighten => SKBlendMode.Lighten,
        BlendMode.ColorDodge => SKBlendMode.ColorDodge,
        BlendMode.ColorBurn => SKBlendMode.ColorBurn,
        BlendMode.SoftLight => SKBlendMode.SoftLight,
        BlendMode.HardLight => SKBlendMode.HardLight,
        BlendMode.Difference => SKBlendMode.Difference,
        BlendMode.Exclusion => SKBlendMode.Exclusion,
        _ => SKBlendMode.SrcOver
    };

    /// <summary>
    /// Gets a user-friendly display name for the blend mode.
    /// </summary>
    public static string GetDisplayName(this BlendMode mode) => mode switch
    {
        BlendMode.Normal => "Normal",
        BlendMode.Multiply => "Multiply",
        BlendMode.Screen => "Screen",
        BlendMode.Overlay => "Overlay",
        BlendMode.Darken => "Darken",
        BlendMode.Lighten => "Lighten",
        BlendMode.ColorDodge => "Color Dodge",
        BlendMode.ColorBurn => "Color Burn",
        BlendMode.SoftLight => "Soft Light",
        BlendMode.HardLight => "Hard Light",
        BlendMode.Difference => "Difference",
        BlendMode.Exclusion => "Exclusion",
        _ => mode.ToString()
    };
}
