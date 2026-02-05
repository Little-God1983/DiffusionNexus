using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Settings for the drawing tool.
/// </summary>
public record DrawingSettings
{
    /// <summary>
    /// The brush color.
    /// </summary>
    public SKColor Color { get; init; } = SKColors.White;

    /// <summary>
    /// The brush size in pixels (relative to the displayed image).
    /// </summary>
    public float Size { get; init; } = 10f;

    /// <summary>
    /// The brush shape.
    /// </summary>
    public BrushShape Shape { get; init; } = BrushShape.Round;
}
