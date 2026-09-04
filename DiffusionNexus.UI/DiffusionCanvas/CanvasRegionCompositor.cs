using Avalonia;
using SkiaSharp;

namespace DiffusionNexus.UI.DiffusionCanvas;

/// <summary>One already-decoded raster with the world rectangle it occupies.</summary>
public readonly record struct CanvasCompositeSource(SKBitmap Bitmap, Rect WorldRect);

/// <summary>
/// The pixels found under the bounding box, plus how much of the region they actually cover.
/// </summary>
/// <param name="Bitmap">
/// Premultiplied RGBA at the box's latent size. Uncovered area is transparent — call
/// <see cref="CanvasRegionCompositor.EncodeAsPng"/> with a fill to flatten it before handing it to a backend.
/// </param>
/// <param name="Coverage">Fraction (0–1) of the output pixels that are effectively opaque.</param>
public sealed record CanvasRegionComposite(SKBitmap Bitmap, double Coverage) : IDisposable
{
    /// <summary>True when nothing on the canvas lies under the box — a plain text2img generation.</summary>
    public bool IsEmpty => Coverage <= 0;

    /// <summary>True when the box sits entirely on existing pixels — a clean img2img.</summary>
    public bool IsFullyCovered => Coverage >= 0.999;

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// Builds "what is under the bounding box" as an image the backend can take as an init image.
///
/// The region is composited <b>arithmetically</b> from the accepted rasters' own pixels — it is not a
/// screenshot of the canvas. That matters twice over: this repo has no <c>RenderTargetBitmap</c> or
/// <c>SKSurface</c> anywhere and no precedent for snapshotting a live Avalonia visual tree, and doing the
/// maths ourselves keeps the result independent of zoom, DPI scaling and whatever the user can currently
/// see. It also means every behaviour here is unit-testable with no Avalonia platform.
/// </summary>
public static class CanvasRegionCompositor
{
    /// <summary>
    /// Alpha at or below which a pixel counts as "not there". Anti-aliased raster edges land just above
    /// zero, and treating those as coverage would report a box touching a neighbour's edge as img2img.
    /// </summary>
    public const byte OpaqueAlphaThreshold = 8;

    /// <summary>
    /// Mid-grey, used to flatten the *uncovered* part of a partially covered region before encoding.
    /// Neutral grey is the least-opinionated thing to hand a denoiser: black or white would bias the
    /// generation toward a dark or blown-out edge.
    /// </summary>
    public static readonly SKColor NeutralFill = new(0x80, 0x80, 0x80, 0xFF);

    /// <summary>
    /// Draws every raster overlapping <paramref name="worldRegion"/> into a bitmap of the box's latent
    /// size. Sources are drawn in list order, so the caller's z-order (last = top) is preserved.
    /// </summary>
    /// <param name="sources">Decoded rasters with their world rectangles. Not disposed by this method.</param>
    /// <param name="worldRegion">The bounding box, in world units.</param>
    /// <param name="outputWidth">Latent width — the output bitmap's pixel width.</param>
    /// <param name="outputHeight">Latent height — the output bitmap's pixel height.</param>
    public static CanvasRegionComposite Composite(
        IReadOnlyList<CanvasCompositeSource> sources,
        Rect worldRegion,
        int outputWidth,
        int outputHeight)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputHeight, 1);

        if (worldRegion.Width <= 0 || worldRegion.Height <= 0)
            throw new ArgumentException("The generation region must have a positive width and height.", nameof(worldRegion));

        var output = new SKBitmap(outputWidth, outputHeight, SKColorType.Rgba8888, SKAlphaType.Premul);

        // SkiaSharp hands back an empty bitmap instead of throwing when the native allocation fails —
        // the same guard ImageEditorCore.ApplyCanvasExtend and Layer.CreateResizedBitmap both carry.
        if (output.IsEmpty || output.Width != outputWidth || output.Height != outputHeight)
        {
            output.Dispose();
            throw new InvalidOperationException(
                $"Failed to allocate a {outputWidth}x{outputHeight} bitmap for the generation region.");
        }

        try
        {
            using (var canvas = new SKCanvas(output))
            {
                canvas.Clear(SKColors.Transparent);

                var scaleX = outputWidth / worldRegion.Width;
                var scaleY = outputHeight / worldRegion.Height;

                foreach (var source in sources)
                {
                    if (source.Bitmap is null || source.Bitmap.IsEmpty)
                        continue;
                    if (!source.WorldRect.Intersects(worldRegion))
                        continue;

                    var dest = new SKRect(
                        (float)((source.WorldRect.X - worldRegion.X) * scaleX),
                        (float)((source.WorldRect.Y - worldRegion.Y) * scaleY),
                        (float)((source.WorldRect.Right - worldRegion.X) * scaleX),
                        (float)((source.WorldRect.Bottom - worldRegion.Y) * scaleY));

                    var src = new SKRect(0, 0, source.Bitmap.Width, source.Bitmap.Height);
                    canvas.DrawBitmap(source.Bitmap, src, dest);
                }
            }

            return new CanvasRegionComposite(output, MeasureCoverage(output));
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Decodes the rasters that overlap <paramref name="worldRegion"/> from their saved PNGs, ready for
    /// <see cref="Composite"/>. Rasters with no readable file are skipped rather than failing the whole
    /// generation — a missing output file must not stop the user generating.
    /// </summary>
    /// <remarks>The caller owns the returned bitmaps and must dispose them.</remarks>
    public static IReadOnlyList<CanvasCompositeSource> LoadIntersecting(
        IEnumerable<ICanvasRaster> rasters,
        Rect worldRegion,
        Action<string>? onSkipped = null)
    {
        ArgumentNullException.ThrowIfNull(rasters);

        var loaded = new List<CanvasCompositeSource>();
        try
        {
            foreach (var raster in rasters)
            {
                var rect = raster.WorldRect;
                if (rect.Width <= 0 || rect.Height <= 0 || !rect.Intersects(worldRegion))
                    continue;

                if (string.IsNullOrWhiteSpace(raster.ImagePath) || !File.Exists(raster.ImagePath))
                {
                    onSkipped?.Invoke($"raster at {rect} has no readable image file");
                    continue;
                }

                var bitmap = SKBitmap.Decode(raster.ImagePath);
                if (bitmap is null || bitmap.IsEmpty)
                {
                    bitmap?.Dispose();
                    onSkipped?.Invoke($"'{raster.ImagePath}' could not be decoded");
                    continue;
                }

                loaded.Add(new CanvasCompositeSource(bitmap, rect));
            }
        }
        catch
        {
            foreach (var source in loaded)
                source.Bitmap.Dispose();
            throw;
        }

        return loaded;
    }

    /// <summary>
    /// Encodes a composite to PNG bytes, optionally flattening it onto a solid colour first.
    /// </summary>
    /// <remarks>
    /// The intermediate bitmap is deliberately <see cref="SKAlphaType.Unpremul"/>: encoding
    /// premultiplied data straight to PNG "can result in blank/transparent output", as
    /// <c>ImageEditorCore.Inpainting</c> records having learned the hard way.
    /// </remarks>
    public static byte[] EncodeAsPng(SKBitmap source, SKColor? flattenOnto = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var output = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        if (output.IsEmpty)
            throw new InvalidOperationException($"Failed to allocate a {source.Width}x{source.Height} bitmap for PNG encoding.");

        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(flattenOnto ?? SKColors.Transparent);
            var rect = new SKRect(0, 0, source.Width, source.Height);
            canvas.DrawBitmap(source, rect, rect);
        }

        using var image = SKImage.FromBitmap(output);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("SkiaSharp returned no data when encoding the generation region to PNG.");

        return data.ToArray();
    }

    /// <summary>Fraction of pixels whose alpha is above <see cref="OpaqueAlphaThreshold"/>.</summary>
    private static double MeasureCoverage(SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixelSpan();
        if (pixels.IsEmpty)
            return 0;

        var rowBytes = bitmap.RowBytes;
        var bytesPerPixel = bitmap.BytesPerPixel;
        if (bytesPerPixel < 4)
            return 0;

        long covered = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var rowStart = y * rowBytes;
            for (var x = 0; x < bitmap.Width; x++)
            {
                // Rgba8888 and Bgra8888 both carry alpha in the fourth byte of the pixel.
                if (pixels[rowStart + x * bytesPerPixel + 3] > OpaqueAlphaThreshold)
                    covered++;
            }
        }

        return (double)covered / ((long)bitmap.Width * bitmap.Height);
    }
}
