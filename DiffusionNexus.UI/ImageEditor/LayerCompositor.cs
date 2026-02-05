using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Handles compositing layers together for rendering and export.
/// </summary>
public static class LayerCompositor
{
    /// <summary>
    /// Composites all visible layers onto a canvas.
    /// </summary>
    /// <param name="canvas">Target canvas to draw on.</param>
    /// <param name="layers">Layer stack to composite.</param>
    /// <param name="destRect">Destination rectangle for drawing.</param>
    public static void CompositeToCanvas(SKCanvas canvas, LayerStack layers, SKRect destRect)
    {
        if (layers.Count == 0) return;

        var scaleX = destRect.Width / layers.Width;
        var scaleY = destRect.Height / layers.Height;

        canvas.Save();
        canvas.Translate(destRect.Left, destRect.Top);
        canvas.Scale(scaleX, scaleY);

        // Draw layers from bottom to top
        foreach (var layer in layers.Layers)
        {
            if (!layer.IsVisible || layer.Bitmap == null) continue;

            using var paint = new SKPaint
            {
                Color = SKColors.White.WithAlpha((byte)(layer.Opacity * 255)),
                BlendMode = layer.BlendMode.ToSKBlendMode(),
                IsAntialias = true,
                FilterQuality = SKFilterQuality.High
            };

            canvas.DrawBitmap(layer.Bitmap, 0, 0, paint);
        }

        canvas.Restore();
    }

    /// <summary>
    /// Composites layers with a zoom transform applied.
    /// </summary>
    /// <param name="canvas">Target canvas.</param>
    /// <param name="layers">Layer stack to composite.</param>
    /// <param name="zoomLevel">Zoom level (1.0 = 100%).</param>
    /// <param name="panX">Horizontal pan offset.</param>
    /// <param name="panY">Vertical pan offset.</param>
    /// <param name="canvasBounds">Bounds of the drawing canvas.</param>
    /// <returns>The actual image rectangle on screen.</returns>
    public static SKRect CompositeWithZoom(
        SKCanvas canvas,
        LayerStack layers,
        float zoomLevel,
        float panX,
        float panY,
        SKRect canvasBounds)
    {
        if (layers.Count == 0) return SKRect.Empty;

        // Calculate scaled dimensions
        var scaledWidth = layers.Width * zoomLevel;
        var scaledHeight = layers.Height * zoomLevel;

        // Center the image
        var x = (canvasBounds.Width - scaledWidth) / 2 + panX;
        var y = (canvasBounds.Height - scaledHeight) / 2 + panY;

        var destRect = new SKRect(x, y, x + scaledWidth, y + scaledHeight);

        CompositeToCanvas(canvas, layers, destRect);

        return destRect;
    }

    /// <summary>
    /// Renders a single layer to a canvas at the specified location.
    /// </summary>
    /// <param name="canvas">Target canvas.</param>
    /// <param name="layer">Layer to render.</param>
    /// <param name="destRect">Destination rectangle.</param>
    /// <param name="applyLayerProperties">Whether to apply opacity and blend mode.</param>
    public static void RenderLayer(SKCanvas canvas, Layer layer, SKRect destRect, bool applyLayerProperties = true)
    {
        if (layer.Bitmap == null) return;

        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.High
        };

        if (applyLayerProperties)
        {
            paint.Color = SKColors.White.WithAlpha((byte)(layer.Opacity * 255));
            paint.BlendMode = layer.BlendMode.ToSKBlendMode();
        }

        canvas.DrawBitmap(layer.Bitmap, destRect, paint);
    }

    /// <summary>
    /// Creates a flattened bitmap from the layer stack with optional background.
    /// </summary>
    /// <param name="layers">Layer stack to flatten.</param>
    /// <param name="backgroundColor">Optional background color (null for transparent).</param>
    /// <returns>A new bitmap with all layers composited.</returns>
    public static SKBitmap CreateFlattenedBitmap(LayerStack layers, SKColor? backgroundColor = null)
    {
        var result = new SKBitmap(layers.Width, layers.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);

        if (backgroundColor.HasValue)
        {
            canvas.Clear(backgroundColor.Value);
        }
        else
        {
            canvas.Clear(SKColors.Transparent);
        }

        var destRect = new SKRect(0, 0, layers.Width, layers.Height);
        CompositeToCanvas(canvas, layers, destRect);

        return result;
    }

    /// <summary>
    /// Exports layers to individual bitmaps (for layered file formats).
    /// </summary>
    /// <param name="layers">Layer stack to export.</param>
    /// <returns>List of tuples containing layer name and bitmap.</returns>
    public static List<(string Name, SKBitmap Bitmap, float Opacity, BlendMode BlendMode)> ExportLayersAsBitmaps(LayerStack layers)
    {
        var result = new List<(string, SKBitmap, float, BlendMode)>();

        foreach (var layer in layers.Layers)
        {
            if (layer.Bitmap == null) continue;

            var copy = layer.Bitmap.Copy();
            result.Add((layer.Name, copy, layer.Opacity, layer.BlendMode));
        }

        return result;
    }
}
