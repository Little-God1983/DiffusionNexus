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

    /// <summary>
    /// Whether the raster is shown. A hidden raster is neither drawn nor fed to the model: what is under
    /// the box is what the model sees, so a layer the user hid must not leak into the input (#594).
    /// </summary>
    /// <remarks>A default member so a raster with no layer state (test stubs, snapshots) is simply shown.</remarks>
    bool IsVisible => true;

    /// <summary>Opacity from 0 to 1, applied on screen and in the composited region alike.</summary>
    double Opacity => 1.0;

    /// <summary>The raster's world rectangle.</summary>
    Rect WorldRect => new(CanvasX, CanvasY, Width, Height);
}
