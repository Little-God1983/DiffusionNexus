using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Where a layer's top-left lands after a whole-image rotate/flip. <c>canvasWidth/Height</c> are
/// the canvas size BEFORE the operation; <c>bounds</c> the layer's bounds before it.
/// </summary>
public static class LayerOffsetRemap
{
    public static SKPointI RotateRight(SKRectI b, int canvasWidth, int canvasHeight) => new(canvasHeight - b.Bottom, b.Left);
    public static SKPointI RotateLeft(SKRectI b, int canvasWidth, int canvasHeight) => new(b.Top, canvasWidth - b.Right);
    public static SKPointI Rotate180(SKRectI b, int canvasWidth, int canvasHeight) => new(canvasWidth - b.Right, canvasHeight - b.Bottom);
    public static SKPointI FlipHorizontal(SKRectI b, int canvasWidth, int canvasHeight) => new(canvasWidth - b.Right, b.Top);
    public static SKPointI FlipVertical(SKRectI b, int canvasWidth, int canvasHeight) => new(b.Left, canvasHeight - b.Bottom);
}
