namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Settings for brightness and contrast adjustments.
/// Brightness: -100 to +100 (0 = no change)
/// Contrast: -100 to +100 (0 = no change)
/// </summary>
public class BrightnessContrastSettings
{
    /// <summary>
    /// Brightness adjustment from -100 (darken) to +100 (brighten).
    /// Default is 0 (no change).
    /// </summary>
    public float Brightness { get; set; }

    /// <summary>
    /// Contrast adjustment from -100 (reduce contrast) to +100 (increase contrast).
    /// Default is 0 (no change).
    /// </summary>
    public float Contrast { get; set; }

    /// <summary>
    /// Gets whether any adjustments have been made.
    /// </summary>
    public bool HasAdjustments => Brightness != 0 || Contrast != 0;

    /// <summary>
    /// Creates a new instance with default values (no adjustment).
    /// </summary>
    public BrightnessContrastSettings()
    {
        Brightness = 0;
        Contrast = 0;
    }

    /// <summary>
    /// Creates a new instance with the specified values.
    /// </summary>
    public BrightnessContrastSettings(float brightness, float contrast)
    {
        Brightness = brightness;
        Contrast = contrast;
    }

    /// <summary>
    /// Creates a copy of this settings object.
    /// </summary>
    public BrightnessContrastSettings Clone() => new(Brightness, Contrast);
}
