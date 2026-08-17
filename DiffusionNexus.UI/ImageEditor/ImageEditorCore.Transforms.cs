using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

public partial class ImageEditorCore
{
    #region Transform Operations

    /// <summary>
    /// Rotates the image 90 degrees clockwise.
    /// When in layer mode, rotates all layers.
    /// </summary>
    public bool RotateRight() => ApplyTransform(RotateBitmapRight);

    /// <summary>
    /// Rotates the image 90 degrees counter-clockwise.
    /// When in layer mode, rotates all layers.
    /// </summary>
    public bool RotateLeft() => ApplyTransform(RotateBitmapLeft);

    /// <summary>
    /// Rotates the image 180 degrees.
    /// When in layer mode, rotates all layers.
    /// </summary>
    public bool Rotate180() => ApplyTransform(RotateBitmap180);

    /// <summary>
    /// Flips the image horizontally (mirror).
    /// When in layer mode, flips all layers.
    /// </summary>
    public bool FlipHorizontal() => ApplyTransform(FlipBitmapHorizontal);

    /// <summary>
    /// Flips the image vertically.
    /// When in layer mode, flips all layers.
    /// </summary>
    public bool FlipVertical() => ApplyTransform(FlipBitmapVertical);

    /// <summary>
    /// Applies a whole-image transform: every layer is transformed when in layer mode, otherwise
    /// the working bitmap is replaced.
    /// <para>
    /// Both paths free bitmaps the Avalonia render thread may be drawing, so the swap happens
    /// under the render lock and the replaced bitmap is released after it is dropped
    /// (see <c>ImageEditorCore._bitmapLock</c>).
    /// </para>
    /// </summary>
    /// <param name="transform">Produces the transformed copy of a source bitmap.</param>
    /// <returns>True if the transform was applied.</returns>
    private bool ApplyTransform(Func<SKBitmap?, SKBitmap?> transform)
    {
        if (GetOperationTargetBitmap() is null) return false;

        try
        {
            SKBitmap? replaced = null;

            lock (_bitmapLock)
            {
                if (_isLayerMode && _layers != null)
                {
                    _layers.TransformAll(layer => transform(layer.Bitmap));
                }
                else
                {
                    var transformed = transform(_workingBitmap);
                    if (transformed is null) return false;

                    replaced = _workingBitmap;
                    _workingBitmap = transformed;
                }
            }

            replaced?.Dispose();
            OnImageChanged();
            return true;
        }
        catch { return false; }
    }

    // Helper methods for bitmap transformations
    private static SKBitmap? RotateBitmapRight(SKBitmap? source)
    {
        if (source is null) return null;
        var rotated = new SKBitmap(source.Height, source.Width);
        using var canvas = new SKCanvas(rotated);
        canvas.Translate(rotated.Width, 0);
        canvas.RotateDegrees(90);
        canvas.DrawBitmap(source, 0, 0);
        return rotated;
    }

    private static SKBitmap? RotateBitmapLeft(SKBitmap? source)
    {
        if (source is null) return null;
        var rotated = new SKBitmap(source.Height, source.Width);
        using var canvas = new SKCanvas(rotated);
        canvas.Translate(0, rotated.Height);
        canvas.RotateDegrees(-90);
        canvas.DrawBitmap(source, 0, 0);
        return rotated;
    }

    private static SKBitmap? RotateBitmap180(SKBitmap? source)
    {
        if (source is null) return null;
        var rotated = new SKBitmap(source.Width, source.Height);
        using var canvas = new SKCanvas(rotated);
        canvas.Translate(rotated.Width, rotated.Height);
        canvas.RotateDegrees(180);
        canvas.DrawBitmap(source, 0, 0);
        return rotated;
    }

    private static SKBitmap? FlipBitmapHorizontal(SKBitmap? source)
    {
        if (source is null) return null;
        var flipped = new SKBitmap(source.Width, source.Height);
        using var canvas = new SKCanvas(flipped);
        canvas.Translate(flipped.Width, 0);
        canvas.Scale(-1, 1);
        canvas.DrawBitmap(source, 0, 0);
        return flipped;
    }

    private static SKBitmap? FlipBitmapVertical(SKBitmap? source)
    {
        if (source is null) return null;
        var flipped = new SKBitmap(source.Width, source.Height);
        using var canvas = new SKCanvas(flipped);
        canvas.Translate(0, flipped.Height);
        canvas.Scale(1, -1);
        canvas.DrawBitmap(source, 0, 0);
        return flipped;
    }

    #endregion Transform Operations
}
