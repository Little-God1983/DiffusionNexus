using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

public partial class ImageEditorCore
{
    #region Transform Operations

    /// <summary>
    /// Rotates the image 90 degrees clockwise.
    /// When in layer mode, rotates all layers.
    /// </summary>
    public bool RotateRight()
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (targetBitmap is null) return false;
        try
        {
            var rotated = new SKBitmap(targetBitmap.Height, targetBitmap.Width);
            using (var canvas = new SKCanvas(rotated))
            {
                canvas.Translate(rotated.Width, 0);
                canvas.RotateDegrees(90);
                canvas.DrawBitmap(targetBitmap, 0, 0);
            }
            
            if (_isLayerMode && _layers != null)
            {
                // Rotate all layers
                _layers.TransformAll(layer => RotateBitmapRight(layer.Bitmap));
            }
            else
            {
                _workingBitmap?.Dispose();
                _workingBitmap = rotated;
            }
            
            OnImageChanged();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Rotates the image 90 degrees counter-clockwise.
    /// When in layer mode, rotates all layers.
    /// </summary>
    public bool RotateLeft()
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (targetBitmap is null) return false;
        try
        {
            var rotated = new SKBitmap(targetBitmap.Height, targetBitmap.Width);
            using (var canvas = new SKCanvas(rotated))
            {
                canvas.Translate(0, rotated.Height);
                canvas.RotateDegrees(-90);
                canvas.DrawBitmap(targetBitmap, 0, 0);
            }
            
            if (_isLayerMode && _layers != null)
            {
                // Rotate all layers
                _layers.TransformAll(layer => RotateBitmapLeft(layer.Bitmap));
            }
            else
            {
                _workingBitmap?.Dispose();
                _workingBitmap = rotated;
            }
            
            OnImageChanged();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Rotates the image 180 degrees.
    /// When in layer mode, rotates all layers.
    /// </summary>
    public bool Rotate180()
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (targetBitmap is null) return false;
        try
        {
            var rotated = new SKBitmap(targetBitmap.Width, targetBitmap.Height);
            using (var canvas = new SKCanvas(rotated))
            {
                canvas.Translate(rotated.Width, rotated.Height);
                canvas.RotateDegrees(180);
                canvas.DrawBitmap(targetBitmap, 0, 0);
            }
            
            if (_isLayerMode && _layers != null)
            {
                // Rotate all layers
                _layers.TransformAll(layer => RotateBitmap180(layer.Bitmap));
            }
            else
            {
                _workingBitmap?.Dispose();
                _workingBitmap = rotated;
            }
            
            OnImageChanged();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Flips the image horizontally (mirror).
    /// When in layer mode, flips all layers.
    /// </summary>
    public bool FlipHorizontal()
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (targetBitmap is null) return false;
        try
        {
            var flipped = new SKBitmap(targetBitmap.Width, targetBitmap.Height);
            using (var canvas = new SKCanvas(flipped))
            {
                canvas.Translate(flipped.Width, 0);
                canvas.Scale(-1, 1);
                canvas.DrawBitmap(targetBitmap, 0, 0);
            }
            
            if (_isLayerMode && _layers != null)
            {
                // Flip all layers
                _layers.TransformAll(layer => FlipBitmapHorizontal(layer.Bitmap));
            }
            else
            {
                _workingBitmap?.Dispose();
                _workingBitmap = flipped;
            }
            
            OnImageChanged();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Flips the image vertically.
    /// When in layer mode, flips all layers.
    /// </summary>
    public bool FlipVertical()
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (targetBitmap is null) return false;
        try
        {
            var flipped = new SKBitmap(targetBitmap.Width, targetBitmap.Height);
            using (var canvas = new SKCanvas(flipped))
            {
                canvas.Translate(0, flipped.Height);
                canvas.Scale(1, -1);
                canvas.DrawBitmap(targetBitmap, 0, 0);
            }
            
            if (_isLayerMode && _layers != null)
            {
                // Flip all layers
                _layers.TransformAll(layer => FlipBitmapVertical(layer.Bitmap));
            }
            else
            {
                _workingBitmap?.Dispose();
                _workingBitmap = flipped;
            }
            
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
