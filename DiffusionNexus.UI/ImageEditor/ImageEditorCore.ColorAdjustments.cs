using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

public partial class ImageEditorCore
{
    #region Color Balance

    /// <summary>
    /// Applies color balance adjustments to the image.
    /// When in layer mode, applies to the active layer.
    /// </summary>
    public bool ApplyColorBalance(ColorBalanceSettings settings)
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (targetBitmap is null || settings is null || !settings.HasAdjustments)
            return false;

        try
        {
            var width = targetBitmap.Width;
            var height = targetBitmap.Height;
            var result = new SKBitmap(width, height);

            var srcPixels = targetBitmap.Pixels;
            var dstPixels = new SKColor[srcPixels.Length];

            var shadowsCR = settings.ShadowsCyanRed / 100f;
            var shadowsMG = settings.ShadowsMagentaGreen / 100f;
            var shadowsYB = settings.ShadowsYellowBlue / 100f;
            var midtonesCR = settings.MidtonesCyanRed / 100f;
            var midtonesMG = settings.MidtonesMagentaGreen / 100f;
            var midtonesYB = settings.MidtonesYellowBlue / 100f;
            var highlightsCR = settings.HighlightsCyanRed / 100f;
            var highlightsMG = settings.HighlightsMagentaGreen / 100f;
            var highlightsYB = settings.HighlightsYellowBlue / 100f;

            for (var i = 0; i < srcPixels.Length; i++)
            {
                var pixel = srcPixels[i];
                var r = pixel.Red / 255f;
                var g = pixel.Green / 255f;
                var b = pixel.Blue / 255f;

                var lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;

                var shadowWeight = 1f - Math.Clamp(lum * 2f, 0f, 1f);
                var highlightWeight = Math.Clamp((lum - 0.5f) * 2f, 0f, 1f);
                var midtoneWeight = 1f - shadowWeight - highlightWeight;

                var rAdjust = shadowsCR * shadowWeight + midtonesCR * midtoneWeight + highlightsCR * highlightWeight;
                var gAdjust = shadowsMG * shadowWeight + midtonesMG * midtoneWeight + highlightsMG * highlightWeight;
                var bAdjust = shadowsYB * shadowWeight + midtonesYB * midtoneWeight + highlightsYB * highlightWeight;

                r += rAdjust * 0.5f;
                g += gAdjust * 0.5f - rAdjust * 0.15f;
                b += bAdjust * 0.5f - rAdjust * 0.15f;

                if (bAdjust < 0) { r -= bAdjust * 0.3f; g -= bAdjust * 0.3f; }
                if (gAdjust < 0) { r -= gAdjust * 0.3f; b -= gAdjust * 0.3f; }

                if (settings.PreserveLuminosity)
                {
                    var newLum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    if (newLum > 0.001f)
                    {
                        var lumRatio = lum / newLum;
                        r *= lumRatio; g *= lumRatio; b *= lumRatio;
                    }
                }

                var newR = (byte)Math.Clamp((int)(r * 255f), 0, 255);
                var newG = (byte)Math.Clamp((int)(g * 255f), 0, 255);
                var newB = (byte)Math.Clamp((int)(b * 255f), 0, 255);

                dstPixels[i] = new SKColor(newR, newG, newB, pixel.Alpha);
            }

            result.Pixels = dstPixels;
            SetOperationTargetBitmap(result);
            OnImageChanged();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Sets a preview bitmap to display instead of the working bitmap.
    /// </summary>
    public bool SetColorBalancePreview(ColorBalanceSettings settings)
    {
        if (settings is null)
            return false;

        lock (_bitmapLock)
        {
            var targetBitmap = GetOperationTargetBitmap();
            if (targetBitmap is null)
                return false;

            // Dispose old preview
            var oldPreview = _previewBitmap;
            _previewBitmap = null;

            if (!settings.HasAdjustments)
            {
                _isPreviewActive = false;
                oldPreview?.Dispose();
                OnImageChanged();
                return true;
            }

            // Create new preview from target bitmap (not swapping)
            var newPreview = CreateColorBalancePreview(targetBitmap, settings);
            
            
            // Only after new preview is ready, dispose old and assign new
            _previewBitmap = newPreview;
            _isPreviewActive = _previewBitmap is not null;
            oldPreview?.Dispose();
        }

        OnImageChanged();
        return _isPreviewActive;
    }

    /// <summary>
    /// Creates a color balance preview bitmap from the source bitmap.
    /// Does not modify any class fields.
    /// </summary>
    private static SKBitmap? CreateColorBalancePreview(SKBitmap source, ColorBalanceSettings settings)
    {
        if (source is null || settings is null || !settings.HasAdjustments)
            return null;

        try
        {
            var width = source.Width;
            var height = source.Height;
            var result = new SKBitmap(width, height);

            var srcPixels = source.Pixels;
            var dstPixels = new SKColor[srcPixels.Length];

            var shadowsCR = settings.ShadowsCyanRed / 100f;
            var shadowsMG = settings.ShadowsMagentaGreen / 100f;
            var shadowsYB = settings.ShadowsYellowBlue / 100f;
            var midtonesCR = settings.MidtonesCyanRed / 100f;
            var midtonesMG = settings.MidtonesMagentaGreen / 100f;
            var midtonesYB = settings.MidtonesYellowBlue / 100f;
            var highlightsCR = settings.HighlightsCyanRed / 100f;
            var highlightsMG = settings.HighlightsMagentaGreen / 100f;
            var highlightsYB = settings.HighlightsYellowBlue / 100f;

            for (var i = 0; i < srcPixels.Length; i++)
            {
                var pixel = srcPixels[i];
                var r = pixel.Red / 255f;
                var g = pixel.Green / 255f;
                var b = pixel.Blue / 255f;

                var lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;

                var shadowWeight = 1f - Math.Clamp(lum * 2f, 0f, 1f);
                var highlightWeight = Math.Clamp((lum - 0.5f) * 2f, 0f, 1f);
                var midtoneWeight = 1f - shadowWeight - highlightWeight;

                var rAdjust = shadowsCR * shadowWeight + midtonesCR * midtoneWeight + highlightsCR * highlightWeight;
                var gAdjust = shadowsMG * shadowWeight + midtonesMG * midtoneWeight + highlightsMG * highlightWeight;
                var bAdjust = shadowsYB * shadowWeight + midtonesYB * midtoneWeight + highlightsYB * highlightWeight;

                r += rAdjust * 0.5f;
                g += gAdjust * 0.5f - rAdjust * 0.15f;
                b += bAdjust * 0.5f - rAdjust * 0.15f;

                if (bAdjust < 0) { r -= bAdjust * 0.3f; g -= bAdjust * 0.3f; }
                if (gAdjust < 0) { r -= gAdjust * 0.3f; b -= gAdjust * 0.3f; }

                if (settings.PreserveLuminosity)
                {
                    var newLum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    if (newLum > 0.001f)
                    {
                        var lumRatio = lum / newLum;
                        r *= lumRatio; g *= lumRatio; b *= lumRatio;
                    }
                }

                var newR = (byte)Math.Clamp((int)(r * 255f), 0, 255);
                var newG = (byte)Math.Clamp((int)(g * 255f), 0, 255);
                var newB = (byte)Math.Clamp((int)(b * 255f), 0, 255);

                dstPixels[i] = new SKColor(newR, newG, newB, pixel.Alpha);
            }

            result.Pixels = dstPixels;
            return result;
        }
        catch
        {
            return null;
        }
    }

    #endregion Color Balance

    #region Brightness and Contrast

    /// <summary>
    /// Applies brightness and contrast adjustments to the image.
    /// When in layer mode, applies to the active layer.
    /// </summary>
    public bool ApplyBrightnessContrast(BrightnessContrastSettings settings)
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (targetBitmap is null || settings is null || !settings.HasAdjustments)
            return false;

        try
        {
            var result = CreateBrightnessContrastPreview(targetBitmap, settings);
            if (result is null)
                return false;

            SetOperationTargetBitmap(result);
            OnImageChanged();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Sets a preview bitmap with brightness/contrast adjustments.
    /// </summary>
    public bool SetBrightnessContrastPreview(BrightnessContrastSettings settings)
    {
        if (settings is null)
            return false;

        lock (_bitmapLock)
        {
            var targetBitmap = GetOperationTargetBitmap();
            if (targetBitmap is null)
                return false;

            // Dispose old preview
            var oldPreview = _previewBitmap;
            _previewBitmap = null;

            if (!settings.HasAdjustments)
            {
                _isPreviewActive = false;
                oldPreview?.Dispose();
                OnImageChanged();
                return true;
            }

            // Create new preview from target bitmap
            var newPreview = CreateBrightnessContrastPreview(targetBitmap, settings);
            
            // Only after new preview is ready, dispose old and assign new
            _previewBitmap = newPreview;
            _isPreviewActive = _previewBitmap is not null;
            oldPreview?.Dispose();
        }

        OnImageChanged();
        return _isPreviewActive;
    }

    /// <summary>
    /// Creates a brightness/contrast preview bitmap from the source bitmap.
    /// Does not modify any class fields.
    /// </summary>
    private static SKBitmap? CreateBrightnessContrastPreview(SKBitmap source, BrightnessContrastSettings settings)
    {
        if (source is null || settings is null || !settings.HasAdjustments)
            return null;

        try
        {
            var width = source.Width;
            var height = source.Height;
            var result = new SKBitmap(width, height);

            var srcPixels = source.Pixels;
            var dstPixels = new SKColor[srcPixels.Length];

            // Normalize brightness (-100 to +100) to a factor
            // Brightness: add/subtract from pixel values
            var brightnessFactor = settings.Brightness / 100f;
            
            // Normalize contrast (-100 to +100) to a factor
            // Contrast: 0 = gray, 1 = normal, >1 = more contrast
            // Map -100..+100 to 0..2 (with 0 = no change)
            var contrastFactor = (settings.Contrast + 100f) / 100f;

            for (var i = 0; i < srcPixels.Length; i++)
            {
                var pixel = srcPixels[i];
                
                // Convert to 0-1 range
                var r = pixel.Red / 255f;
                var g = pixel.Green / 255f;
                var b = pixel.Blue / 255f;

                // Apply brightness (additive)
                r += brightnessFactor;
                g += brightnessFactor;
                b += brightnessFactor;

                // Apply contrast (multiply around 0.5 midpoint)
                r = (r - 0.5f) * contrastFactor + 0.5f;
                g = (g - 0.5f) * contrastFactor + 0.5f;
                b = (b - 0.5f) * contrastFactor + 0.5f;

                // Clamp and convert back to byte
                var newR = (byte)Math.Clamp((int)(r * 255f), 0, 255);
                var newG = (byte)Math.Clamp((int)(g * 255f), 0, 255);
                var newB = (byte)Math.Clamp((int)(b * 255f), 0, 255);

                dstPixels[i] = new SKColor(newR, newG, newB, pixel.Alpha);
            }

            result.Pixels = dstPixels;
            return result;
        }
        catch
        {
            return null;
        }
    }

    #endregion Brightness and Contrast
}
