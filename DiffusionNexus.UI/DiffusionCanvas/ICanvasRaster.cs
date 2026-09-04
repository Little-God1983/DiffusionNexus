using Avalonia;
using Avalonia.Media.Imaging;

namespace DiffusionNexus.UI.DiffusionCanvas;

/// <summary>
/// An accepted result sitting on the canvas: pixels at a world position and size.
///
/// The surface renders these, and <see cref="CanvasRegionCompositor"/> reads them back to build the
/// image under the bounding box. Kept as an interface so neither the control nor the compositor needs
/// to know about <c>GenerationFrameViewModel</c>.
/// </summary>
public interface ICanvasRaster
{
    /// <summary>World X of the raster's left edge.</summary>
    double CanvasX { get; }

    /// <summary>World Y of the raster's top edge.</summary>
    double CanvasY { get; }

    /// <summary>Raster width in world units (the generated pixel width).</summary>
    int Width { get; }

    /// <summary>Raster height in world units (the generated pixel height).</summary>
    int Height { get; }

    /// <summary>The decoded image the surface draws, or null while the raster has no pixels yet.</summary>
    Bitmap? FrameImage { get; }

    /// <summary>
    /// Absolute path of the saved PNG. The compositor decodes from here rather than from
    /// <see cref="FrameImage"/>, so compositing needs no Avalonia platform and stays unit-testable.
    /// </summary>
    string? ImagePath { get; }

    /// <summary>The raster's world rectangle.</summary>
    Rect WorldRect => new(CanvasX, CanvasY, Width, Height);
}
