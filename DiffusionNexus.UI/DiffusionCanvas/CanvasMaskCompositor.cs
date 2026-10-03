using SkiaSharp;

namespace DiffusionNexus.UI.DiffusionCanvas;

/// <summary>
/// Puts the pixels an inpaint mask kept back into a generated result (#595).
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
/// </para>
/// </remarks>
public static class CanvasMaskCompositor
{
    /// <summary>
    /// Blends <paramref name="generatedPng"/> over <paramref name="original"/> through <paramref name="mask"/>
    /// (white = take the generated pixel, black = keep the original) and returns the PNG, or null when the
    /// sizes disagree or the result cannot be decoded, in which case the caller keeps the result as it is.
    /// </summary>
    /// <param name="generatedPng">The backend's result.</param>
    /// <param name="original">The region the run started from. Not disposed.</param>
    /// <param name="mask">The mask that was sent, read through its red channel. Not disposed.</param>
    public static byte[]? KeepUnmasked(byte[] generatedPng, SKBitmap original, SKBitmap mask)
    {
        ArgumentNullException.ThrowIfNull(generatedPng);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(mask);

        // SKBitmap.Decode throws rather than returning null on bytes no codec recognises.
        using var stream = new SKMemoryStream(generatedPng);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
            return null;

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null
            || decoded.Width != original.Width || decoded.Height != original.Height
            || mask.Width != original.Width || mask.Height != original.Height)
        {
            return null;
        }

        using var generated = ToRgba(decoded);
        using var kept = ToRgba(original);
        using var weights = ToRgba(mask);

        var g = generated.GetPixelSpan();
        var k = kept.GetPixelSpan();
        var w = weights.GetPixelSpan();
        var pixels = new byte[g.Length];

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var m = w[i];          // red channel of the opaque grey mask
            var inv = 255 - m;
            pixels[i] = (byte)((k[i] * inv + g[i] * m + 127) / 255);
            pixels[i + 1] = (byte)((k[i + 1] * inv + g[i + 1] * m + 127) / 255);
            pixels[i + 2] = (byte)((k[i + 2] * inv + g[i + 2] * m + 127) / 255);
            pixels[i + 3] = 255;
        }

        using var output = new SKBitmap(original.Width, original.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, output.GetPixels(), pixels.Length);
        output.NotifyPixelsChanged();

        using var image = SKImage.FromBitmap(output);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
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
