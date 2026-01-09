namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Represents the tonal range to adjust in color balance operations.
/// </summary>
public enum ColorBalanceRange
{
    /// <summary>Dark tones in the image.</summary>
    Shadows,
    /// <summary>Middle tones in the image.</summary>
    Midtones,
    /// <summary>Bright tones in the image.</summary>
    Highlights
}

/// <summary>
/// Settings for color balance adjustments, similar to Blender's Color Balance node.
/// Each range (Shadows, Midtones, Highlights) has three adjustment axes:
/// Cyan-Red, Magenta-Green, Yellow-Blue.
/// Values range from -100 to +100, where 0 is neutral.
/// </summary>
public class ColorBalanceSettings
{
    /// <summary>Shadows: Cyan (-100) to Red (+100)</summary>
    public float ShadowsCyanRed { get; set; }

    /// <summary>Shadows: Magenta (-100) to Green (+100)</summary>
    public float ShadowsMagentaGreen { get; set; }

    /// <summary>Shadows: Yellow (-100) to Blue (+100)</summary>
    public float ShadowsYellowBlue { get; set; }

    /// <summary>Midtones: Cyan (-100) to Red (+100)</summary>
    public float MidtonesCyanRed { get; set; }

    /// <summary>Midtones: Magenta (-100) to Green (+100)</summary>
    public float MidtonesMagentaGreen { get; set; }

    /// <summary>Midtones: Yellow (-100) to Blue (+100)</summary>
    public float MidtonesYellowBlue { get; set; }

    /// <summary>Highlights: Cyan (-100) to Red (+100)</summary>
    public float HighlightsCyanRed { get; set; }

    /// <summary>Highlights: Magenta (-100) to Green (+100)</summary>
    public float HighlightsMagentaGreen { get; set; }

    /// <summary>Highlights: Yellow (-100) to Blue (+100)</summary>
    public float HighlightsYellowBlue { get; set; }

    /// <summary>Whether to preserve luminosity when adjusting colors.</summary>
    public bool PreserveLuminosity { get; set; } = true;

    /// <summary>
    /// Gets whether any adjustments have been made (any value is non-zero).
    /// </summary>
    public bool HasAdjustments =>
        ShadowsCyanRed != 0 || ShadowsMagentaGreen != 0 || ShadowsYellowBlue != 0 ||
        MidtonesCyanRed != 0 || MidtonesMagentaGreen != 0 || MidtonesYellowBlue != 0 ||
        HighlightsCyanRed != 0 || HighlightsMagentaGreen != 0 || HighlightsYellowBlue != 0;

    /// <summary>
    /// Resets all adjustments for a specific range to neutral (0).
    /// </summary>
    public void ResetRange(ColorBalanceRange range)
    {
        switch (range)
        {
            case ColorBalanceRange.Shadows:
                ShadowsCyanRed = 0;
                ShadowsMagentaGreen = 0;
                ShadowsYellowBlue = 0;
                break;
            case ColorBalanceRange.Midtones:
                MidtonesCyanRed = 0;
                MidtonesMagentaGreen = 0;
                MidtonesYellowBlue = 0;
                break;
            case ColorBalanceRange.Highlights:
                HighlightsCyanRed = 0;
                HighlightsMagentaGreen = 0;
                HighlightsYellowBlue = 0;
                break;
        }
    }

    /// <summary>
    /// Resets all adjustments to neutral (0).
    /// </summary>
    public void ResetAll()
    {
        ShadowsCyanRed = 0;
        ShadowsMagentaGreen = 0;
        ShadowsYellowBlue = 0;
        MidtonesCyanRed = 0;
        MidtonesMagentaGreen = 0;
        MidtonesYellowBlue = 0;
        HighlightsCyanRed = 0;
        HighlightsMagentaGreen = 0;
        HighlightsYellowBlue = 0;
    }

    /// <summary>
    /// Creates a copy of the current settings.
    /// </summary>
    public ColorBalanceSettings Clone()
    {
        return new ColorBalanceSettings
        {
            ShadowsCyanRed = ShadowsCyanRed,
            ShadowsMagentaGreen = ShadowsMagentaGreen,
            ShadowsYellowBlue = ShadowsYellowBlue,
            MidtonesCyanRed = MidtonesCyanRed,
            MidtonesMagentaGreen = MidtonesMagentaGreen,
            MidtonesYellowBlue = MidtonesYellowBlue,
            HighlightsCyanRed = HighlightsCyanRed,
            HighlightsMagentaGreen = HighlightsMagentaGreen,
            HighlightsYellowBlue = HighlightsYellowBlue,
            PreserveLuminosity = PreserveLuminosity
        };
    }
}
