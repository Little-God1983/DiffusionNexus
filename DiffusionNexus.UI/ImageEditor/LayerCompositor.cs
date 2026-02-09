using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Handles compositing layers together for rendering and export.
/// </summary>
public static class LayerCompositor
{
    // Checkerboard tile size for inpaint mask display (in image pixels)
    private const int InpaintCheckSize = 8;
    private const byte InpaintMaskAlpha = 153; // ~60% opacity so image shows through
    private static readonly SKColor InpaintCheckLight = new(200, 200, 200);
    private static readonly SKColor InpaintCheckDark = new(150, 150, 150);

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

            if (layer.IsInpaintMask)
            {
                RenderInpaintMaskLayer(canvas, layer, layers.Width, layers.Height);
            }
            else
            {
                using var paint = new SKPaint
                {
                    Color = SKColors.White.WithAlpha((byte)(layer.Opacity * 255)),
                    BlendMode = layer.BlendMode.ToSKBlendMode(),
                    IsAntialias = true,
                    FilterQuality = SKFilterQuality.High
                };

                canvas.DrawBitmap(layer.Bitmap, 0, 0, paint);
            }
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

    /// <summary>
    /// Renders an inpaint mask layer as a checkerboard pattern where the mask has been painted.
    /// The mask bitmap stores white (opaque) where the user painted; those areas become checkerboard.
    /// </summary>
    private static void RenderInpaintMaskLayer(SKCanvas canvas, Layer layer, int imageWidth, int imageHeight)
    {
        if (layer.Bitmap is null) return;

        // Build a checkerboard tile that we'll use as the pattern source
        using var tileBitmap = new SKBitmap(InpaintCheckSize * 2, InpaintCheckSize * 2);
        tileBitmap.Erase(InpaintCheckLight);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                if ((x + y) % 2 == 1)
                {
                    for (int py = y * InpaintCheckSize; py < (y + 1) * InpaintCheckSize; py++)
                    {
                        for (int px = x * InpaintCheckSize; px < (x + 1) * InpaintCheckSize; px++)
                        {
                            tileBitmap.SetPixel(px, py, InpaintCheckDark);
                        }
                    }
                }
            }
        }

        using var shader = SKShader.CreateBitmap(tileBitmap, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);

        using var checkerPaint = new SKPaint
        {
            Shader = shader,
            IsAntialias = true
        };

        // Use the mask bitmap's alpha as a clip so checkerboard only shows where painted
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, imageWidth, imageHeight));

        // Draw checkerboard only where the mask has opaque pixels (painted areas)
        // We use DstIn blending: draw checkerboard first in a layer, then mask it
        var layerRect = new SKRect(0, 0, imageWidth, imageHeight);

        // Combine inherent mask semi-transparency with layer opacity
        var effectiveAlpha = (byte)(InpaintMaskAlpha * layer.Opacity);

        using var maskPaint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(effectiveAlpha)
        };

        // Use saveLayer for proper alpha compositing
        canvas.SaveLayer(maskPaint);

        // Draw the checkerboard everywhere
        canvas.DrawRect(layerRect, checkerPaint);

        // Cut it using the mask bitmap as alpha (DstIn keeps checkerboard only where mask is opaque)
        using var maskBlendPaint = new SKPaint
        {
            BlendMode = SKBlendMode.DstIn,
            FilterQuality = SKFilterQuality.High
        };
        canvas.DrawBitmap(layer.Bitmap, layerRect, maskBlendPaint);

        canvas.Restore(); // restore saveLayer
        canvas.Restore(); // restore clip
    }
}
