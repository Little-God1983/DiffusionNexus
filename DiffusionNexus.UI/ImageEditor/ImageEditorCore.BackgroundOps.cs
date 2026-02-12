using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

public partial class ImageEditorCore
{
    #region Background Removal

    /// <summary>
    /// Applies an alpha mask to the current image for background removal.
    /// When in layer mode, applies to the active layer.
    /// </summary>
    /// <param name="maskData">Grayscale mask data where 255 = foreground, 0 = background.</param>
    /// <param name="width">Width of the mask in pixels.</param>
    /// <param name="height">Height of the mask in pixels.</param>
    /// <returns>True if the mask was applied successfully.</returns>
    public bool ApplyBackgroundMask(byte[] maskData, int width, int height)
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (maskData is null || targetBitmap is null)
            return false;

        if (width != targetBitmap.Width || height != targetBitmap.Height)
            return false;

        if (maskData.Length != width * height)
            return false;

        try
        {
            lock (_bitmapLock)
            {
                var pixels = targetBitmap.Pixels;
                var newPixels = new SKColor[pixels.Length];

                for (var i = 0; i < pixels.Length; i++)
                {
                    var pixel = pixels[i];
                    var maskValue = maskData[i];

                    // Apply mask as alpha channel
                    newPixels[i] = new SKColor(pixel.Red, pixel.Green, pixel.Blue, maskValue);
                }

                // Create new bitmap with the masked pixels
                var result = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                result.Pixels = newPixels;
                SetOperationTargetBitmap(result);
            }

            OnImageChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Applies the background removal mask as layers, creating:
    /// 1. Subject layer (top) - foreground with background transparent
    /// 2. Background layer (middle) - background with subject transparent (inverted mask)
    /// 3. Original layer (bottom) - the existing layer is kept as the original
    /// Automatically enables layer mode if not already enabled.
    /// </summary>
    /// <param name="maskData">Grayscale mask data where 255 = foreground, 0 = background.</param>
    /// <param name="width">Width of the mask in pixels.</param>
    /// <param name="height">Height of the mask in pixels.</param>
    /// <returns>True if the layers were created successfully.</returns>
    public bool ApplyBackgroundMaskWithLayers(byte[] maskData, int width, int height)
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (maskData is null || targetBitmap is null)
            return false;

        if (width != targetBitmap.Width || height != targetBitmap.Height)
            return false;

        if (maskData.Length != width * height)
            return false;

        try
        {
            lock (_bitmapLock)
            {
                var pixels = targetBitmap.Pixels;

                // Create subject bitmap (foreground with background transparent)
                var subjectPixels = new SKColor[pixels.Length];
                for (var i = 0; i < pixels.Length; i++)
                {
                    var pixel = pixels[i];
                    var maskValue = maskData[i];
                    subjectPixels[i] = new SKColor(pixel.Red, pixel.Green, pixel.Blue, maskValue);
                }
                var subjectBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                subjectBitmap.Pixels = subjectPixels;

                // Create background bitmap (background with subject transparent - inverted mask)
                var backgroundPixels = new SKColor[pixels.Length];
                for (var i = 0; i < pixels.Length; i++)
                {
                    var pixel = pixels[i];
                    var invertedMaskValue = (byte)(255 - maskData[i]);
                    backgroundPixels[i] = new SKColor(pixel.Red, pixel.Green, pixel.Blue, invertedMaskValue);
                }
                var backgroundBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                backgroundBitmap.Pixels = backgroundPixels;

                if (_layers != null)
                {
                    // Already in layer mode - the active layer IS the original, just rename it
                    // and add Subject and Background layers above it
                    var activeLayer = _layers.ActiveLayer;
                    if (activeLayer != null)
                    {
                        activeLayer.Name = "Original";
                    }
                    
                    // Add Background and Subject layers on top
                    _services?.Layers.AddLayerFromBitmap(backgroundBitmap, "Background");
                    _services?.Layers.AddLayerFromBitmap(subjectBitmap, "Subject");
                }
                else if (_services is not null)
                {
                    // Not in layer mode - enable it with the 3-layer structure
                    var originalBitmap = targetBitmap.Copy();
                    
                    _services.Layers.EnableLayerMode(originalBitmap, "Original");
                    _services.Layers.AddLayerFromBitmap(backgroundBitmap, "Background");
                    _services.Layers.AddLayerFromBitmap(subjectBitmap, "Subject");
                }
            }

            OnImageChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the raw RGBA pixel data from the current target bitmap (active layer or working bitmap).
    /// Used for passing to background removal service.
    /// </summary>
    /// <returns>Tuple of (imageData, width, height) or null if no image is loaded.</returns>
    public (byte[] Data, int Width, int Height)? GetWorkingBitmapData()
    {
        lock (_bitmapLock)
        {
            var targetBitmap = GetOperationTargetBitmap();
            if (targetBitmap is null)
                return null;

            var width = targetBitmap.Width;
            var height = targetBitmap.Height;
            var pixels = targetBitmap.Pixels;
            var data = new byte[width * height * 4]; // RGBA

            for (var i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                var offset = i * 4;
                data[offset] = pixel.Red;
                data[offset + 1] = pixel.Green;
                data[offset + 2] = pixel.Blue;
                data[offset + 3] = pixel.Alpha;
            }

            return (data, width, height);
        }
    }

    #endregion Background Removal

    #region Background Fill

    /// <summary>
    /// Fills transparent areas of the image with the specified solid color.
    /// Uses alpha compositing to blend the fill color behind the existing image.
    /// When in layer mode, applies to the active layer.
    /// </summary>
    /// <param name="settings">The background fill settings containing the fill color.</param>
    /// <returns>True if the fill was applied successfully.</returns>
    public bool ApplyBackgroundFill(BackgroundFillSettings settings)
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (targetBitmap is null || settings is null)
            return false;

        try
        {
            var result = CreateBackgroundFillBitmap(targetBitmap, settings);
            if (result is null)
                return false;

            lock (_bitmapLock)
            {
                SetOperationTargetBitmap(result);
            }

            OnImageChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets a preview with the background filled using the specified color.
    /// Does not modify the working bitmap until ApplyPreview is called.
    /// </summary>
    /// <param name="settings">The background fill settings containing the fill color.</param>
    /// <returns>True if the preview was set successfully.</returns>
    public bool SetBackgroundFillPreview(BackgroundFillSettings settings)
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


            try
            {
                // Create new preview with background filled
                var newPreview = CreateBackgroundFillBitmap(targetBitmap, settings);

                _previewBitmap = newPreview;
                _isPreviewActive = _previewBitmap is not null;
                oldPreview?.Dispose();
            }
            catch
            {
                oldPreview?.Dispose();
                return false;
            }
        }

        OnImageChanged();
        return _isPreviewActive;
    }

    /// <summary>
    /// Creates a new bitmap with transparent areas filled with the specified color.
    /// Uses alpha compositing: result = foreground * alpha + background * (1 - alpha)
    /// </summary>
    private static SKBitmap? CreateBackgroundFillBitmap(SKBitmap source, BackgroundFillSettings settings)
    {
        if (source is null || settings is null)
            return null;

        try
        {
            var width = source.Width;
            var height = source.Height;
            
            // Create result bitmap without alpha (opaque)
            var result = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);

            var srcPixels = source.Pixels;
            var dstPixels = new SKColor[srcPixels.Length];

            var fillR = settings.Red;
            var fillG = settings.Green;
            var fillB = settings.Blue;

            for (var i = 0; i < srcPixels.Length; i++)
            {
                var pixel = srcPixels[i];
                var alpha = pixel.Alpha / 255f;

                // Alpha compositing: foreground * alpha + background * (1 - alpha)
                var r = (byte)Math.Clamp((int)(pixel.Red * alpha + fillR * (1f - alpha)), 0, 255);
                var g = (byte)Math.Clamp((int)(pixel.Green * alpha + fillG * (1f - alpha)), 0, 255);
                var b = (byte)Math.Clamp((int)(pixel.Blue * alpha + fillB * (1f - alpha)), 0, 255);

                dstPixels[i] = new SKColor(r, g, b, 255);
            }

            result.Pixels = dstPixels;
            return result;
        }
        catch
        {
            return null;
        }
    }

    #endregion Background Fill
}
