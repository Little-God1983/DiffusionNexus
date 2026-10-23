using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// The one feather rule for inpaint masks, shared by the Image Editor's Inpaint tool and the Diffusion
/// Canvas's mask layer (#595), so a feather value means the same thing on both screens.
/// </summary>
public static class MaskFeathering
{
    /// <summary>
    /// Feathers an inpaint mask by dilating then blurring.
    /// Softens hard binary brush edges so the inpainting model can blend at boundaries.
    /// </summary>
    /// <param name="maskBitmap">The mask, read through its alpha channel. Not disposed.</param>
    /// <param name="featherRadius">
    /// Blur sigma in pixels; the mask first grows by half of it. Below 0.5 the mask is copied unchanged.
    /// </param>
    /// <returns>A new bitmap the caller owns.</returns>
    public static SKBitmap Feather(SKBitmap maskBitmap, float featherRadius)
    {
        if (featherRadius < 0.5f)
            return maskBitmap.Copy();

        var dilateRadius = Math.Max(1, (int)(featherRadius * 0.5f));
        var blurSigma = featherRadius;

        var dilated = new SKBitmap(
            maskBitmap.Width, maskBitmap.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(dilated))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint();
            paint.ImageFilter = SKImageFilter.CreateDilate(dilateRadius, dilateRadius);
            canvas.DrawBitmap(maskBitmap, 0, 0, paint);
        }

        var feathered = new SKBitmap(
            maskBitmap.Width, maskBitmap.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(feathered))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint();
            paint.ImageFilter = SKImageFilter.CreateBlur(blurSigma, blurSigma);
            canvas.DrawBitmap(dilated, 0, 0, paint);
        }

        dilated.Dispose();
        return feathered;
    }
}
