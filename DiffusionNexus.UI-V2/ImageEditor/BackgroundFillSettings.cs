namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Settings for the background fill operation.
/// Used to composite a solid color behind transparent areas of an image.
/// </summary>
public sealed class BackgroundFillSettings
{
    /// <summary>
    /// Red component of the fill color (0-255).
    /// </summary>
    public byte Red { get; init; }

    /// <summary>
    /// Green component of the fill color (0-255).
    /// </summary>
    public byte Green { get; init; }

    /// <summary>
    /// Blue component of the fill color (0-255).
    /// </summary>
    public byte Blue { get; init; }

    /// <summary>
    /// Creates a new BackgroundFillSettings with default white color.
    /// </summary>
    public BackgroundFillSettings() : this(255, 255, 255) { }

    /// <summary>
    /// Creates a new BackgroundFillSettings with specified RGB values.
    /// </summary>
    public BackgroundFillSettings(byte red, byte green, byte blue)
    {
        Red = red;
        Green = green;
        Blue = blue;
    }

    /// <summary>
    /// Creates settings from an Avalonia Color.
    /// </summary>
    public static BackgroundFillSettings FromColor(Avalonia.Media.Color color) =>
        new(color.R, color.G, color.B);

    /// <summary>
    /// Converts to SkiaSharp color for rendering.
    /// </summary>
    public SkiaSharp.SKColor ToSKColor() => new(Red, Green, Blue, 255);

    /// <summary>
    /// Gets the hex color string representation.
    /// </summary>
    public string ToHexString() => $"#{Red:X2}{Green:X2}{Blue:X2}";

    /// <summary>
    /// Common preset colors for quick selection.
    /// </summary>
    public static class Presets
    {
        public static BackgroundFillSettings White => new(255, 255, 255);
        public static BackgroundFillSettings Black => new(0, 0, 0);
        public static BackgroundFillSettings Gray => new(128, 128, 128);
        public static BackgroundFillSettings Green => new(0, 177, 64);
        public static BackgroundFillSettings Blue => new(0, 120, 215);
    }
}
