using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

public partial class ImageEditorCore
{
    #region Inpainting

    /// <summary>
    /// Finds the existing inpaint mask layer, or creates one if none exists.
    /// Ensures layer mode is enabled before creating.
    /// </summary>
    /// <returns>The inpaint mask layer, or null if creation failed.</returns>
    private Layer? GetOrCreateInpaintMaskLayer()
    {
        // Ensure layer mode is active
        if (!_isLayerMode)
        {
            EnableLayerMode();
        }

        if (_layers is null) return null;

        // Look for existing inpaint mask layer
        var maskLayer = _layers.Layers.FirstOrDefault(l => l.IsInpaintMask);
        if (maskLayer is not null) return maskLayer;

        // Remember which layer the user was editing before we create the mask
        var previousActive = _layers.ActiveLayer;

        // Create the inpaint mask layer directly at the top of the stack
        // (bypassing AddLayer which would insert below an existing mask)
        var newLayer = new Layer(_layers.Width, _layers.Height, "Inpaint Mask")
        {
            IsInpaintMask = true
        };
        _layers.Layers.Add(newLayer);

        // Restore the previous active layer so drawing/shape tools still target it
        _layers.ActiveLayer = previousActive ?? newLayer;

        return newLayer;
    }

    /// <summary>
    /// Applies an inpainting brush stroke to the inpaint mask layer.
    /// Paints white (opaque) pixels on a transparent layer; the compositor
    /// renders these areas as a checkerboard pattern.
    /// </summary>
    /// <param name="normalizedPoints">Points in normalized coordinates (0-1).</param>
    /// <param name="brushSize">Brush size in image pixels.</param>
    /// <returns>True if the stroke was applied successfully.</returns>
    public bool ApplyInpaintStroke(IReadOnlyList<SKPoint> normalizedPoints, float brushSize)
    {
        if (normalizedPoints is null || normalizedPoints.Count == 0)
            return false;

        lock (_bitmapLock)
        {
            var maskLayer = GetOrCreateInpaintMaskLayer();
            if (maskLayer?.Bitmap is null || !maskLayer.CanEdit)
                return false;

            try
            {
                var width = maskLayer.Bitmap.Width;
                var height = maskLayer.Bitmap.Height;

                var imagePoints = normalizedPoints
                    .Select(p => new SKPoint(p.X * width, p.Y * height))
                    .ToList();

                var scaledBrushSize = brushSize * width;

                using var canvas = new SKCanvas(maskLayer.Bitmap);
                using var paint = new SKPaint
                {
                    Color = SKColors.White,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = scaledBrushSize,
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round
                };

                if (imagePoints.Count == 1)
                {
                    paint.Style = SKPaintStyle.Fill;
                    canvas.DrawCircle(imagePoints[0], scaledBrushSize / 2, paint);
                }
                else if (imagePoints.Count == 2)
                {
                    canvas.DrawLine(imagePoints[0], imagePoints[1], paint);
                }
                else
                {
                    using var path = new SKPath();
                    path.MoveTo(imagePoints[0]);
                    for (var i = 1; i < imagePoints.Count; i++)
                    {
                        path.LineTo(imagePoints[i]);
                    }
                    canvas.DrawPath(path, paint);
                }

                canvas.Flush();
                maskLayer.NotifyContentChanged();
            }
            catch
            {
                return false;
            }
        }

        OnImageChanged();
        return true;
    }

    /// <summary>
    /// Clears the inpaint mask layer, removing all painted mask areas.
    /// </summary>
    public void ClearInpaintMask()
    {
        if (_layers is null) return;

        lock (_bitmapLock)
        {
            var maskLayer = _layers.Layers.FirstOrDefault(l => l.IsInpaintMask);
            maskLayer?.Clear();
        }

        OnImageChanged();
    }

    /// <summary>
    /// Extracts the inpaint mask bitmap (white = inpaint, black = keep).
    /// Returns null if no mask layer exists or it is empty.
    /// </summary>
    /// <returns>A copy of the mask bitmap, or null.</returns>
    public SKBitmap? GetInpaintMaskBitmap()
    {
        if (_layers is null) return null;

        lock (_bitmapLock)
        {
            var maskLayer = _layers.Layers.FirstOrDefault(l => l.IsInpaintMask);
            return maskLayer?.Bitmap?.Copy();
        }
    }

    /// <summary>
    /// Captures the current flattened state as the inpaint base image.
    /// Subsequent inpainting generations will use this bitmap instead of re-flattening.
    /// </summary>
    public void SetInpaintBaseBitmap()
    {
        lock (_bitmapLock)
        {
            _inpaintBaseBitmap?.Dispose();
            _inpaintBaseBitmap = _isLayerMode && _layers != null
                ? _layers.Flatten()
                : _workingBitmap?.Copy();
            Interlocked.Increment(ref _inpaintBaseVersion);
        }
    }

    /// <summary>
    /// Returns a copy of the stored inpaint base bitmap.
    /// If none has been captured yet, auto-captures the current state.
    /// </summary>
    public SKBitmap? GetInpaintBaseBitmap()
    {
        lock (_bitmapLock)
        {
            if (_inpaintBaseBitmap is null)
            {
                SetInpaintBaseBitmap();
            }
            return _inpaintBaseBitmap?.Copy();
        }
    }

    /// <summary>
    /// Gets whether an inpaint base bitmap is currently stored.
    /// </summary>
    public bool HasInpaintBase => _inpaintBaseBitmap is not null;

    /// <summary>
    /// Monotonically increasing version that changes whenever the inpaint base is set or cleared.
    /// </summary>
    public long InpaintBaseVersion => Interlocked.Read(ref _inpaintBaseVersion);

    /// <summary>
    /// Clears the stored inpaint base bitmap.
    /// </summary>
    public void ClearInpaintBase()
    {
        lock (_bitmapLock)
        {
            _inpaintBaseBitmap?.Dispose();
            _inpaintBaseBitmap = null;
            Interlocked.Increment(ref _inpaintBaseVersion);
        }
    }

    /// <summary>
    /// Prepares a masked image for AI inpainting by compositing the inpaint base with the
    /// painted mask. Transparent pixels in the result mark the regions to regenerate.
    /// If no inpaint base exists, the current state is auto-captured.
    /// </summary>
    /// <param name="featherRadius">Mask feather radius for softening brush edges (0 = hard).</param>
    /// <returns>Result containing PNG bytes of the masked image, or an error.</returns>
    public InpaintPrepareResult PrepareInpaintMaskedImage(float featherRadius)
    {
        bool baseCaptured = false;
        SKBitmap? baseCopy;
        SKBitmap? maskCopy;

        lock (_bitmapLock)
        {
            if (_inpaintBaseBitmap is null)
            {
                _inpaintBaseBitmap = _isLayerMode && _layers is not null
                    ? _layers.Flatten()
                    : _workingBitmap?.Copy();
                Interlocked.Increment(ref _inpaintBaseVersion);
                baseCaptured = true;
            }

            baseCopy = _inpaintBaseBitmap?.Copy();
            if (baseCopy is null)
                return InpaintPrepareResult.Failed("No image to inpaint.");

            maskCopy = _layers?.Layers
                .FirstOrDefault(l => l.IsInpaintMask)?.Bitmap?.Copy();
        }

        if (maskCopy is null)
        {
            baseCopy.Dispose();
            return InpaintPrepareResult.Failed(
                "No inpaint mask painted. Paint over areas to regenerate.",
                baseCaptured);
        }

        return CompositeAndEncode(baseCopy, maskCopy, featherRadius, baseCaptured);
    }

    /// <summary>
    /// Exports the current inpaint base bitmap as PNG bytes.
    /// Useful for saving a "before" image for comparison workflows.
    /// </summary>
    /// <returns>PNG bytes, or null if no inpaint base has been captured.</returns>
    public byte[]? GetInpaintBaseAsPng()
    {
        SKBitmap? copy;
        lock (_bitmapLock)
        {
            copy = _inpaintBaseBitmap?.Copy();
        }

        if (copy is null) return null;

        try
        {
            using var image = SKImage.FromBitmap(copy);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        finally
        {
            copy.Dispose();
        }
    }

    /// <summary>
    /// Composites the base image with the feathered mask and encodes to PNG.
    /// Disposes both input bitmaps.
    /// </summary>
    private static InpaintPrepareResult CompositeAndEncode(
        SKBitmap baseBitmap, SKBitmap maskBitmap, float featherRadius, bool baseCaptured)
    {
        try
        {
            using var feathered = FeatherMask(maskBitmap, featherRadius);

            // Convert base to unpremultiplied alpha so RGB values are stored straight
            using var unpremul = new SKBitmap(
                baseBitmap.Width, baseBitmap.Height,
                SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using (var canvas = new SKCanvas(unpremul))
                canvas.DrawBitmap(baseBitmap, 0, 0);

            // Set alpha from inverted mask: white (painted) ? alpha 0 (inpaint region)
            var src = unpremul.Pixels;
            var mask = feathered.Pixels;
            var dst = new SKColor[src.Length];
            for (var i = 0; i < src.Length; i++)
            {
                var p = src[i];
                dst[i] = new SKColor(p.Red, p.Green, p.Blue, (byte)(255 - mask[i].Alpha));
            }

            using var result = new SKBitmap(
                baseBitmap.Width, baseBitmap.Height,
                SKColorType.Rgba8888, SKAlphaType.Unpremul);
            result.Pixels = dst;

            using var img = SKImage.FromBitmap(result);
            using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
            return InpaintPrepareResult.Succeeded(encoded.ToArray(), baseCaptured);
        }
        finally
        {
            baseBitmap.Dispose();
            maskBitmap.Dispose();
        }
    }

    /// <summary>
    /// Feathers an inpaint mask by dilating then blurring.
    /// Softens hard binary brush edges so the inpainting model can blend at boundaries.
    /// </summary>
    private static SKBitmap FeatherMask(SKBitmap maskBitmap, float featherRadius)
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

    #endregion Inpainting
}
