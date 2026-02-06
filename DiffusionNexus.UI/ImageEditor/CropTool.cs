using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Represents which handle is being dragged during crop resize.
/// </summary>
public enum CropHandle
{
    None,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
    Move
}

/// <summary>
/// Platform-independent crop tool with rule-of-thirds grid and draggable handles.
/// Stores crop region in normalized coordinates (0-1) relative to the image.
/// </summary>
public class CropTool
{
    private const float HandleRadius = 6f;
    private const float HandleHitRadius = 12f;
    private const float MinCropSizeNormalized = 0.02f; // 2% of image

    // Crop region stored in normalized coordinates (0-1) relative to image
    private float _normalizedLeft;
    private float _normalizedTop;
    private float _normalizedRight;
    private float _normalizedBottom;

    private SKRect _imageRect;
    private CropHandle _activeHandle = CropHandle.None;
    private SKPoint _dragStartPoint;
    
    // Store drag start in normalized coordinates
    private float _dragStartNormLeft;
    private float _dragStartNormTop;
    private float _dragStartNormRight;
    private float _dragStartNormBottom;
    
    private bool _isActive;
    private bool _hasCropRegion;

    /// <summary>
    /// Gets whether the crop tool is currently active.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            if (!value)
            {
                _hasCropRegion = false;
                _activeHandle = CropHandle.None;
            }
        }
    }

    /// <summary>
    /// Gets whether a crop region has been defined.
    /// </summary>
    public bool HasCropRegion => _hasCropRegion;

    /// <summary>
    /// The image width in pixels, used for resolution label rendering.
    /// Set externally when the image dimensions are known.
    /// </summary>
    public int ImagePixelWidth { get; set; }

    /// <summary>
    /// The image height in pixels, used for resolution label rendering.
    /// Set externally when the image dimensions are known.
    /// </summary>
    public int ImagePixelHeight { get; set; }

    /// <summary>
    /// Gets the current crop rectangle in screen coordinates.
    /// </summary>
    public SKRect CropRect => GetScreenCropRect();

    /// <summary>
    /// Gets whether a handle is currently being dragged.
    /// </summary>
    public bool IsDragging => _activeHandle != CropHandle.None;

    /// <summary>
    /// Event raised when the crop region changes.
    /// </summary>
    public event EventHandler? CropRegionChanged;

    /// <summary>
    /// Gets the crop rectangle in screen coordinates from normalized coordinates.
    /// </summary>
    private SKRect GetScreenCropRect()
    {
        if (!_hasCropRegion || _imageRect.Width <= 0 || _imageRect.Height <= 0)
            return SKRect.Empty;

        return new SKRect(
            _imageRect.Left + _normalizedLeft * _imageRect.Width,
            _imageRect.Top + _normalizedTop * _imageRect.Height,
            _imageRect.Left + _normalizedRight * _imageRect.Width,
            _imageRect.Top + _normalizedBottom * _imageRect.Height
        );
    }

    /// <summary>
    /// Converts a screen point to normalized image coordinates.
    /// </summary>
    private (float x, float y) ScreenToNormalized(SKPoint point)
    {
        if (_imageRect.Width <= 0 || _imageRect.Height <= 0)
            return (0, 0);

        var x = (point.X - _imageRect.Left) / _imageRect.Width;
        var y = (point.Y - _imageRect.Top) / _imageRect.Height;
        return (x, y);
    }

    /// <summary>
    /// Sets the image bounds for constraining the crop region.
    /// The crop region stays at the same relative position on the image.
    /// </summary>
    public void SetImageBounds(SKRect imageRect)
    {
        _imageRect = imageRect;
        // No need to update crop region - it's stored in normalized coordinates
        // so it automatically scales with the image
    }

    /// <summary>
    /// Clears the current crop region.
    /// </summary>
    public void ClearCropRegion()
    {
        _hasCropRegion = false;
        _activeHandle = CropHandle.None;
        _normalizedLeft = 0;
        _normalizedTop = 0;
        _normalizedRight = 0;
        _normalizedBottom = 0;
        CropRegionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Handles mouse/pointer down event.
    /// </summary>
    /// <returns>True if the event was handled.</returns>
    public bool OnPointerPressed(SKPoint point)
    {
        if (!_isActive) return false;

        _dragStartPoint = point;

        if (_hasCropRegion)
        {
            // Check if clicking on a handle
            _activeHandle = HitTestHandle(point);
            if (_activeHandle != CropHandle.None)
            {
                // Store drag start in normalized coordinates
                _dragStartNormLeft = _normalizedLeft;
                _dragStartNormTop = _normalizedTop;
                _dragStartNormRight = _normalizedRight;
                _dragStartNormBottom = _normalizedBottom;
                return true;
            }
        }

        // Start new crop region
        var (normX, normY) = ScreenToNormalized(point);
        _normalizedLeft = normX;
        _normalizedTop = normY;
        _normalizedRight = normX;
        _normalizedBottom = normY;
        
        _dragStartNormLeft = normX;
        _dragStartNormTop = normY;
        _dragStartNormRight = normX;
        _dragStartNormBottom = normY;
        
        _activeHandle = CropHandle.BottomRight;
        _hasCropRegion = true;
        return true;
    }

    /// <summary>
    /// Handles mouse/pointer move event.
    /// </summary>
    /// <returns>True if the event was handled.</returns>
    public bool OnPointerMoved(SKPoint point)
    {
        if (!_isActive || _activeHandle == CropHandle.None) return false;

        // Calculate delta in normalized coordinates
        var (startNormX, startNormY) = ScreenToNormalized(_dragStartPoint);
        var (currentNormX, currentNormY) = ScreenToNormalized(point);
        var deltaNormX = currentNormX - startNormX;
        var deltaNormY = currentNormY - startNormY;

        var newLeft = _dragStartNormLeft;
        var newTop = _dragStartNormTop;
        var newRight = _dragStartNormRight;
        var newBottom = _dragStartNormBottom;

        switch (_activeHandle)
        {
            case CropHandle.TopLeft:
                newLeft = _dragStartNormLeft + deltaNormX;
                newTop = _dragStartNormTop + deltaNormY;
                break;
            case CropHandle.Top:
                newTop = _dragStartNormTop + deltaNormY;
                break;
            case CropHandle.TopRight:
                newRight = _dragStartNormRight + deltaNormX;
                newTop = _dragStartNormTop + deltaNormY;
                break;
            case CropHandle.Right:
                newRight = _dragStartNormRight + deltaNormX;
                break;
            case CropHandle.BottomRight:
                newRight = _dragStartNormRight + deltaNormX;
                newBottom = _dragStartNormBottom + deltaNormY;
                break;
            case CropHandle.Bottom:
                newBottom = _dragStartNormBottom + deltaNormY;
                break;
            case CropHandle.BottomLeft:
                newLeft = _dragStartNormLeft + deltaNormX;
                newBottom = _dragStartNormBottom + deltaNormY;
                break;
            case CropHandle.Left:
                newLeft = _dragStartNormLeft + deltaNormX;
                break;
            case CropHandle.Move:
                newLeft = _dragStartNormLeft + deltaNormX;
                newTop = _dragStartNormTop + deltaNormY;
                newRight = _dragStartNormRight + deltaNormX;
                newBottom = _dragStartNormBottom + deltaNormY;
                break;
        }

        // Normalize and constrain
        NormalizeAndConstrain(ref newLeft, ref newTop, ref newRight, ref newBottom);
        
        _normalizedLeft = newLeft;
        _normalizedTop = newTop;
        _normalizedRight = newRight;
        _normalizedBottom = newBottom;
        
        CropRegionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Handles mouse/pointer up event.
    /// </summary>
    /// <returns>True if the event was handled.</returns>
    public bool OnPointerReleased()
    {
        if (!_isActive) return false;

        _activeHandle = CropHandle.None;
        return true;
    }

    /// <summary>
    /// Gets the cursor type for the given point.
    /// </summary>
    public CropHandle GetCursorForPoint(SKPoint point)
    {
        if (!_isActive || !_hasCropRegion) return CropHandle.None;
        return HitTestHandle(point);
    }

    /// <summary>
    /// Renders the crop overlay with rule-of-thirds grid.
    /// </summary>
    public void Render(SKCanvas canvas, SKRect canvasBounds)
    {
        if (!_isActive || !_hasCropRegion) return;

        var cropRect = GetScreenCropRect();
        if (cropRect.IsEmpty) return;

        // Draw darkened area outside crop region
        using var dimPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 180),
            Style = SKPaintStyle.Fill
        };

        // Top
        canvas.DrawRect(new SKRect(canvasBounds.Left, canvasBounds.Top, canvasBounds.Right, cropRect.Top), dimPaint);
        // Bottom
        canvas.DrawRect(new SKRect(canvasBounds.Left, cropRect.Bottom, canvasBounds.Right, canvasBounds.Bottom), dimPaint);
        // Left
        canvas.DrawRect(new SKRect(canvasBounds.Left, cropRect.Top, cropRect.Left, cropRect.Bottom), dimPaint);
        // Right
        canvas.DrawRect(new SKRect(cropRect.Right, cropRect.Top, canvasBounds.Right, cropRect.Bottom), dimPaint);

        // Draw crop border
        using var borderPaint = new SKPaint
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true
        };
        canvas.DrawRect(cropRect, borderPaint);

        // Draw rule-of-thirds grid
        DrawRuleOfThirdsGrid(canvas, cropRect);

        // Draw handles
        DrawHandles(canvas, cropRect);

        // Draw resolution label
        DrawResolutionLabel(canvas, cropRect);
    }

    /// <summary>
    /// Converts the crop rectangle from normalized coordinates to image pixel coordinates.
    /// </summary>
    public SKRectI GetImageCropRect(int imageWidth, int imageHeight)
    {
        if (!_hasCropRegion)
            return new SKRectI(0, 0, imageWidth, imageHeight);

        // Convert normalized coordinates directly to image coordinates
        var left = (int)Math.Round(_normalizedLeft * imageWidth);
        var top = (int)Math.Round(_normalizedTop * imageHeight);
        var right = (int)Math.Round(_normalizedRight * imageWidth);
        var bottom = (int)Math.Round(_normalizedBottom * imageHeight);

        // Clamp to image bounds
        left = Math.Clamp(left, 0, imageWidth);
        top = Math.Clamp(top, 0, imageHeight);
        right = Math.Clamp(right, 0, imageWidth);
        bottom = Math.Clamp(bottom, 0, imageHeight);

        return new SKRectI(left, top, right, bottom);
    }

    private void DrawRuleOfThirdsGrid(SKCanvas canvas, SKRect cropRect)
    {
        using var gridPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 128),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };

        var thirdWidth = cropRect.Width / 3f;
        var thirdHeight = cropRect.Height / 3f;

        // Vertical lines
        canvas.DrawLine(
            cropRect.Left + thirdWidth, cropRect.Top,
            cropRect.Left + thirdWidth, cropRect.Bottom,
            gridPaint);
        canvas.DrawLine(
            cropRect.Left + thirdWidth * 2, cropRect.Top,
            cropRect.Left + thirdWidth * 2, cropRect.Bottom,
            gridPaint);

        // Horizontal lines
        canvas.DrawLine(
            cropRect.Left, cropRect.Top + thirdHeight,
            cropRect.Right, cropRect.Top + thirdHeight,
            gridPaint);
        canvas.DrawLine(
            cropRect.Left, cropRect.Top + thirdHeight * 2,
            cropRect.Right, cropRect.Top + thirdHeight * 2,
            gridPaint);
    }

    private void DrawHandles(SKCanvas canvas, SKRect cropRect)
    {
        using var handleFillPaint = new SKPaint
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        using var handleStrokePaint = new SKPaint
        {
            Color = new SKColor(80, 80, 80),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };

        var handles = GetHandlePositions(cropRect);
        foreach (var handle in handles)
        {
            canvas.DrawCircle(handle, HandleRadius, handleFillPaint);
            canvas.DrawCircle(handle, HandleRadius, handleStrokePaint);
        }
    }

    private static SKPoint[] GetHandlePositions(SKRect cropRect)
    {
        var midX = cropRect.MidX;
        var midY = cropRect.MidY;

        return
        [
            new SKPoint(cropRect.Left, cropRect.Top),      // TopLeft
            new SKPoint(midX, cropRect.Top),                // Top
            new SKPoint(cropRect.Right, cropRect.Top),     // TopRight
            new SKPoint(cropRect.Right, midY),              // Right
            new SKPoint(cropRect.Right, cropRect.Bottom),  // BottomRight
            new SKPoint(midX, cropRect.Bottom),             // Bottom
            new SKPoint(cropRect.Left, cropRect.Bottom),   // BottomLeft
            new SKPoint(cropRect.Left, midY)                // Left
        ];
    }

    private CropHandle HitTestHandle(SKPoint point)
    {
        var cropRect = GetScreenCropRect();
        if (cropRect.IsEmpty) return CropHandle.None;

        var handles = GetHandlePositions(cropRect);
        var handleTypes = new[]
        {
            CropHandle.TopLeft, CropHandle.Top, CropHandle.TopRight,
            CropHandle.Right, CropHandle.BottomRight, CropHandle.Bottom,
            CropHandle.BottomLeft, CropHandle.Left
        };

        for (int i = 0; i < handles.Length; i++)
        {
            if (IsPointNearHandle(point, handles[i]))
                return handleTypes[i];
        }

        // Check if inside the crop region for move
        if (cropRect.Contains(point))
            return CropHandle.Move;

        return CropHandle.None;
    }

    private static bool IsPointNearHandle(SKPoint point, SKPoint handleCenter)
    {
        var dx = point.X - handleCenter.X;
        var dy = point.Y - handleCenter.Y;
        return dx * dx + dy * dy <= HandleHitRadius * HandleHitRadius;
    }

    /// <summary>
    /// Gets the crop region dimensions in image pixels.
    /// </summary>
    public (int Width, int Height) GetCropPixelDimensions()
    {
        if (!_hasCropRegion || ImagePixelWidth <= 0 || ImagePixelHeight <= 0)
            return (0, 0);

        var w = (int)Math.Round((_normalizedRight - _normalizedLeft) * ImagePixelWidth);
        var h = (int)Math.Round((_normalizedBottom - _normalizedTop) * ImagePixelHeight);
        return (Math.Max(w, 1), Math.Max(h, 1));
    }

    /// <summary>
    /// Expands the crop region to fill the image on the short side while maintaining the current aspect ratio.
    /// </summary>
    public void FitToImage()
    {
        if (!_hasCropRegion) return;

        var cropW = _normalizedRight - _normalizedLeft;
        var cropH = _normalizedBottom - _normalizedTop;
        if (cropW <= 0 || cropH <= 0) return;

        var aspectRatio = cropW / cropH;

        // Determine which dimension to fill
        float newW, newH;
        if (aspectRatio >= 1f)
        {
            // Wider than tall: fill width, compute height
            newW = 1f;
            newH = newW / aspectRatio;
            if (newH > 1f) { newH = 1f; newW = newH * aspectRatio; }
        }
        else
        {
            // Taller than wide: fill height, compute width
            newH = 1f;
            newW = newH * aspectRatio;
            if (newW > 1f) { newW = 1f; newH = newW / aspectRatio; }
        }

        // Center the crop
        _normalizedLeft = (1f - newW) / 2f;
        _normalizedTop = (1f - newH) / 2f;
        _normalizedRight = _normalizedLeft + newW;
        _normalizedBottom = _normalizedTop + newH;

        CropRegionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adjusts the crop region to the given aspect ratio while keeping size as large as possible.
    /// The region stays centered at its current center.
    /// </summary>
    /// <param name="ratioW">Width component of the desired aspect ratio.</param>
    /// <param name="ratioH">Height component of the desired aspect ratio.</param>
    public void SetAspectRatio(float ratioW, float ratioH)
    {
        if (!_hasCropRegion || ratioW <= 0 || ratioH <= 0) return;

        var cropW = _normalizedRight - _normalizedLeft;
        var cropH = _normalizedBottom - _normalizedTop;
        if (cropW <= 0 || cropH <= 0) return;

        var centerX = (_normalizedLeft + _normalizedRight) / 2f;
        var centerY = (_normalizedTop + _normalizedBottom) / 2f;

        // Use the image pixel aspect to convert normalized aspect ratio
        // Normalized coords are 0-1 for both axes, so we must account for image dimensions
        var targetNormRatio = (ratioW / ratioH) * ((float)ImagePixelHeight / ImagePixelWidth);

        float newW, newH;
        var currentNormRatio = cropW / cropH;

        if (targetNormRatio > currentNormRatio)
        {
            // New ratio is wider: keep width, shrink height
            newW = cropW;
            newH = newW / targetNormRatio;
        }
        else
        {
            // New ratio is taller: keep height, shrink width
            newH = cropH;
            newW = newH * targetNormRatio;
        }

        // Ensure minimum size
        newW = Math.Max(newW, MinCropSizeNormalized);
        newH = Math.Max(newH, MinCropSizeNormalized);

        // Position centered, then clamp to image bounds
        _normalizedLeft = centerX - newW / 2f;
        _normalizedTop = centerY - newH / 2f;
        _normalizedRight = centerX + newW / 2f;
        _normalizedBottom = centerY + newH / 2f;

        // Shift into bounds if needed
        if (_normalizedLeft < 0) { _normalizedRight -= _normalizedLeft; _normalizedLeft = 0; }
        if (_normalizedTop < 0) { _normalizedBottom -= _normalizedTop; _normalizedTop = 0; }
        if (_normalizedRight > 1) { _normalizedLeft -= (_normalizedRight - 1); _normalizedRight = 1; }
        if (_normalizedBottom > 1) { _normalizedTop -= (_normalizedBottom - 1); _normalizedBottom = 1; }

        // Final clamp
        _normalizedLeft = Math.Clamp(_normalizedLeft, 0, 1);
        _normalizedTop = Math.Clamp(_normalizedTop, 0, 1);
        _normalizedRight = Math.Clamp(_normalizedRight, 0, 1);
        _normalizedBottom = Math.Clamp(_normalizedBottom, 0, 1);

        CropRegionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Switches the current crop aspect ratio from W:H to H:W.
    /// </summary>
    public void SwitchAspectRatio()
    {
        if (!_hasCropRegion) return;

        var (pixW, pixH) = GetCropPixelDimensions();
        if (pixW <= 0 || pixH <= 0) return;

        // Swap: the new ratio is H:W
        SetAspectRatio(pixH, pixW);
    }

    private void DrawResolutionLabel(SKCanvas canvas, SKRect cropRect)
    {
        var (pixW, pixH) = GetCropPixelDimensions();
        if (pixW <= 0 || pixH <= 0) return;

        var text = $"{pixW} x {pixH}";

        using var font = new SKFont(SKTypeface.Default, 12f);
        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };

        var textBounds = new SKRect();
        font.MeasureText(text, out textBounds, textPaint);

        var labelX = cropRect.MidX - textBounds.Width / 2f;
        var labelY = cropRect.Top - 8f;

        // If the label would go above the canvas, place it inside the crop at the top
        if (labelY - textBounds.Height < 0)
            labelY = cropRect.Top + textBounds.Height + 6f;

        // Background pill
        var bgRect = new SKRect(
            labelX - 6f,
            labelY - textBounds.Height - 2f,
            labelX + textBounds.Width + 6f,
            labelY + 4f);

        using var bgPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 180),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawRoundRect(bgRect, 4f, 4f, bgPaint);
        canvas.DrawText(text, labelX, labelY, font, textPaint);
    }

    private void NormalizeAndConstrain(ref float left, ref float top, ref float right, ref float bottom)
    {
        // Normalize (ensure left < right, top < bottom)
        if (left > right) (left, right) = (right, left);
        if (top > bottom) (top, bottom) = (bottom, top);

        // Ensure minimum size
        if (right - left < MinCropSizeNormalized)
            right = left + MinCropSizeNormalized;
        if (bottom - top < MinCropSizeNormalized)
            bottom = top + MinCropSizeNormalized;

        // Constrain to image bounds (0-1)
        if (_activeHandle == CropHandle.Move)
        {
            // For move, shift the entire region to stay within bounds
            var width = right - left;
            var height = bottom - top;

            if (left < 0)
            {
                left = 0;
                right = width;
            }
            if (right > 1)
            {
                right = 1;
                left = 1 - width;
            }
            if (top < 0)
            {
                top = 0;
                bottom = height;
            }
            if (bottom > 1)
            {
                bottom = 1;
                top = 1 - height;
            }
        }
        else
        {
            // For resize, clamp individual edges
            left = Math.Clamp(left, 0, 1 - MinCropSizeNormalized);
            right = Math.Clamp(right, MinCropSizeNormalized, 1);
            top = Math.Clamp(top, 0, 1 - MinCropSizeNormalized);
            bottom = Math.Clamp(bottom, MinCropSizeNormalized, 1);
        }
    }
}
