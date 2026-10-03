using System.Runtime.InteropServices;
using Avalonia;
using DiffusionNexus.UI.ImageEditor;
using SkiaSharp;

namespace DiffusionNexus.UI.DiffusionCanvas;

/// <summary>
/// An inpaint mask rasterised for one generation: an opaque grey image, white = repaint, black = keep.
/// </summary>
public sealed class CanvasMaskRaster : IDisposable
{
    /// <summary>
    /// The strength a pixel needs for the mask to count as repainting anything: half. Erasing with an
    /// antialiased eraser leaves faint alpha along the old edges, and the feather spreads it; that residue
    /// is not something the user painted, and running on it is a wasted GPU run with no visible change.
    /// </summary>
    public const byte MeaningfulValue = 128;

    internal CanvasMaskRaster(SKBitmap bitmap, double repaintFraction, byte maxValue)
    {
        Bitmap = bitmap;
        RepaintFraction = repaintFraction;
        MaxValue = maxValue;
    }

    /// <summary>The mask, owned by this object.</summary>
    public SKBitmap Bitmap { get; }

    /// <summary>
    /// How much of the region is marked for repaint, 0 to 1. Feathered grey counts in proportion, so a
    /// soft edge is half of an edge.
    /// </summary>
    public double RepaintFraction { get; }

    /// <summary>The strongest repaint value anywhere in the mask, 0 to 255.</summary>
    public byte MaxValue { get; }

    /// <summary>
    /// True when no pixel is marked at least <see cref="MeaningfulValue"/> for repaint: nothing, or only
    /// antialiasing residue left by the eraser.
    /// </summary>
    public bool IsEmpty => MaxValue < MeaningfulValue;

    /// <summary>Encodes the mask as PNG, the form both backends load.</summary>
    public byte[] EncodePng()
    {
        using var image = SKImage.FromBitmap(Bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// Turns the mask layer's world-space strokes into the request's <c>MaskImage</c> for exactly the box's
/// region (#595). Pure: strokes and numbers in, a bitmap out, so every rule here is unit-testable.
/// </summary>
public static class CanvasMaskRasterizer
{
    /// <summary>
    /// How far the shared feather reaches beyond a painted edge, in multiples of the feather: the dilate
    /// adds half, and a Gaussian blur is negligible past three sigma.
    /// </summary>
    private const double FeatherReach = 3.5;

    /// <summary>
    /// How far, in world pixels, a stroke can reach past its own bounds once feathered: a stroke this close
    /// to the box still softens the edge inside it. The readout uses the same reach to decide whether the
    /// mask meets the box.
    /// </summary>
    public static double FeatherReachOf(double feather) =>
        double.IsNaN(feather) || feather < 0.5 ? 0 : Math.Ceiling(feather * FeatherReach) + 1;

    /// <summary>
    /// Rasterises <paramref name="strokes"/> over <paramref name="region"/> into a
    /// <paramref name="width"/> by <paramref name="height"/> mask.
    /// </summary>
    /// <param name="strokes">The strokes in the order they were drawn; an eraser removes what came before it.</param>
    /// <param name="region">The world rectangle the mask covers (the box).</param>
    /// <param name="width">Output width in pixels.</param>
    /// <param name="height">Output height in pixels.</param>
    /// <param name="feather">Feather in world pixels; 0 keeps hard edges.</param>
    /// <param name="invert">Repaint everything except what is painted.</param>
    /// <remarks>
    /// The strokes are drawn over the region plus the feather's reach, then feathered, then cropped: a
    /// stroke just outside the box still softens the edge inside it, and the blur does not read the
    /// region's border as an edge of the painting. Inverting comes after the feather, so an inverted mask
    /// is full strength right up to the box's border.
    /// </remarks>
    public static CanvasMaskRaster Rasterize(
        IReadOnlyList<CanvasMaskStroke> strokes, Rect region, int width, int height, double feather, bool invert)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (!(region.Width > 0) || !(region.Height > 0))
            throw new ArgumentException("The region must have a positive size.", nameof(region));

        var scaleX = width / region.Width;
        var scaleY = height / region.Height;
        var featherPixels = double.IsNaN(feather) ? 0 : Math.Max(0, feather) * scaleX;
        var margin = (int)FeatherReachOf(featherPixels);

        using var painted = new SKBitmap(width + 2 * margin, height + 2 * margin, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(painted))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(margin, margin);
            canvas.Scale((float)scaleX, (float)scaleY);
            canvas.Translate((float)-region.X, (float)-region.Y);
            DrawStrokes(canvas, strokes, SKColors.White);
        }

        using var feathered = MaskFeathering.Feather(painted, (float)featherPixels);

        var source = feathered.GetPixelSpan();
        var sourceStride = feathered.RowBytes;
        var pixels = new byte[width * height * 4];
        long total = 0;
        byte max = 0;

        for (var y = 0; y < height; y++)
        {
            var row = (y + margin) * sourceStride;
            for (var x = 0; x < width; x++)
            {
                var alpha = source[row + (x + margin) * 4 + 3];
                var value = (byte)(invert ? 255 - alpha : alpha);
                total += value;
                if (value > max)
                    max = value;

                var i = (y * width + x) * 4;
                pixels[i] = value;
                pixels[i + 1] = value;
                pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }
        }

        var output = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        Marshal.Copy(pixels, 0, output.GetPixels(), pixels.Length);
        output.NotifyPixelsChanged();

        return new CanvasMaskRaster(output, total / (255.0 * width * height), max);
    }

    /// <summary>
    /// Draws <paramref name="strokes"/> in <paramref name="color"/>, erasers clearing what is under them.
    /// The caller sets the canvas's transform. Shared with the surface's on-screen overlay, so what is
    /// shown and what is sent are drawn by the same code.
    /// </summary>
    internal static void DrawStrokes(SKCanvas canvas, IReadOnlyList<CanvasMaskStroke> strokes, SKColor color)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = color,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };

        foreach (var stroke in strokes)
        {
            paint.BlendMode = stroke.IsErase ? SKBlendMode.Clear : SKBlendMode.SrcOver;

            if (stroke.Points.Count == 1)
            {
                paint.Style = SKPaintStyle.Fill;
                var p = stroke.Points[0];
                canvas.DrawCircle((float)p.X, (float)p.Y, (float)(stroke.Size / 2), paint);
                continue;
            }

            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = (float)stroke.Size;

            using var path = new SKPath();
            path.MoveTo((float)stroke.Points[0].X, (float)stroke.Points[0].Y);
            for (var i = 1; i < stroke.Points.Count; i++)
                path.LineTo((float)stroke.Points[i].X, (float)stroke.Points[i].Y);

            canvas.DrawPath(path, paint);
        }
    }
}
