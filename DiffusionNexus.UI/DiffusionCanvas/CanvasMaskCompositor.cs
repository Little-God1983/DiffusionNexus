using SkiaSharp;

namespace DiffusionNexus.UI.DiffusionCanvas;

/// <summary>
/// Puts the pixels an inpaint mask kept back into each generated result of a batch (#595).
/// </summary>
/// <remarks>
/// <para>
/// Neither backend returns the kept area untouched. Both confine the denoise in latent space (sd.cpp's
/// per-step blend, ComfyUI's <c>SetLatentNoiseMask</c>), but the whole latent is then VAE-decoded, so the
/// area outside the mask comes back through a VAE round trip: colours shift a little and fine detail
/// softens. The canvas accepts the whole box as the new layer, so without this the "kept" pixels would be
/// a slightly altered copy, visible as a seam at the box border.
/// </para>
/// <para>
/// Done on the canvas rather than in each backend's graph so one rule covers every backend: result =
/// original × (1 − mask) + generated × mask, per channel, with the feathered mask as the blend weight.
/// The original and the mask are converted once, when the batch starts; each candidate then costs one
/// decode, one blend into the output's own pixels, and one encode.
/// </para>
/// </remarks>
public sealed class CanvasMaskCompositor : IDisposable
{
    private readonly SKBitmap _original;
    private readonly SKBitmap _mask;

    /// <param name="original">The region the run started from: what the backend received. Copied, not kept.</param>
    /// <param name="mask">The mask that was sent, read through its red channel. Copied, not kept.</param>
    public CanvasMaskCompositor(SKBitmap original, SKBitmap mask)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(mask);
        if (mask.Width != original.Width || mask.Height != original.Height)
            throw new ArgumentException("The mask must be the size of the region it was rasterised for.", nameof(mask));

        _original = ToRgba(original);
        try
        {
            _mask = ToRgba(mask);
        }
        catch
        {
            _original.Dispose();
            throw;
        }
    }

    /// <summary>Width of the region, in pixels.</summary>
    public int Width => _original.Width;

    /// <summary>Height of the region, in pixels.</summary>
    public int Height => _original.Height;

    /// <summary>
    /// Blends <paramref name="generatedPng"/> over the original through the mask (white = take the generated
    /// pixel, black = keep the original) and returns the PNG, or null when the result cannot be decoded or is
    /// a different size, in which case the caller keeps the result as it is.
    /// </summary>
    public byte[]? KeepUnmasked(byte[] generatedPng)
    {
        ArgumentNullException.ThrowIfNull(generatedPng);

        // SKBitmap.Decode throws rather than returning null on bytes no codec recognises.
        using var stream = new SKMemoryStream(generatedPng);
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.Info.Width != Width || codec.Info.Height != Height)
            return null;

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
            return null;

        using var generated = ToRgba(decoded);
        using var output = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var pixmap = output.PeekPixels())
        {
            var o = pixmap.GetPixelSpan();
            var g = generated.GetPixelSpan();
            var k = _original.GetPixelSpan();
            var w = _mask.GetPixelSpan();

            for (var i = 0; i < o.Length; i += 4)
            {
                var m = w[i];          // red channel of the opaque grey mask
                var inv = 255 - m;
                o[i] = (byte)((k[i] * inv + g[i] * m + 127) / 255);
                o[i + 1] = (byte)((k[i + 1] * inv + g[i + 1] * m + 127) / 255);
                o[i + 2] = (byte)((k[i + 2] * inv + g[i + 2] * m + 127) / 255);
                o[i + 3] = 255;
            }
        }

        output.NotifyPixelsChanged();

        using var image = SKImage.FromBitmap(output);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public void Dispose()
    {
        _original.Dispose();
        _mask.Dispose();
    }

    /// <summary>A tightly packed, unpremultiplied RGBA copy, so every bitmap is read with the same layout.</summary>
    private static SKBitmap ToRgba(SKBitmap source)
    {
        var copy = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(copy))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(source, 0, 0);
        }

        return copy;
    }
}
