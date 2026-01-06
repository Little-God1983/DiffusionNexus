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
/// </summary>
public class CropTool
{
    private const float HandleRadius = 6f;
    private const float HandleHitRadius = 12f;
    private const float MinCropSize = 20f;

    private SKRect _cropRect;
    private SKRect _imageRect;
    private CropHandle _activeHandle = CropHandle.None;
    private SKPoint _dragStartPoint;
    private SKRect _dragStartRect;
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
    /// Gets the current crop rectangle in screen coordinates.
    /// </summary>
    public SKRect CropRect => _cropRect;

    /// <summary>
    /// Gets whether a handle is currently being dragged.
    /// </summary>
    public bool IsDragging => _activeHandle != CropHandle.None;

    /// <summary>
    /// Event raised when the crop region changes.
    /// </summary>
    public event EventHandler? CropRegionChanged;

    /// <summary>
    /// Sets the image bounds for constraining the crop region.
    /// </summary>
    public void SetImageBounds(SKRect imageRect)
    {
        _imageRect = imageRect;
        
        // Don't auto-initialize crop region - let user drag to create it
    }

    /// <summary>
    /// Clears the current crop region.
    /// </summary>
    public void ClearCropRegion()
    {
        _hasCropRegion = false;
        _activeHandle = CropHandle.None;
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
                _dragStartRect = _cropRect;
                return true;
            }
        }

        // Start new crop region
        _cropRect = new SKRect(point.X, point.Y, point.X, point.Y);
        _dragStartRect = _cropRect;
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

        var deltaX = point.X - _dragStartPoint.X;
        var deltaY = point.Y - _dragStartPoint.Y;

        var newRect = _dragStartRect;

        switch (_activeHandle)
        {
            case CropHandle.TopLeft:
                newRect.Left = _dragStartRect.Left + deltaX;
                newRect.Top = _dragStartRect.Top + deltaY;
                break;
            case CropHandle.Top:
                newRect.Top = _dragStartRect.Top + deltaY;
                break;
            case CropHandle.TopRight:
                newRect.Right = _dragStartRect.Right + deltaX;
                newRect.Top = _dragStartRect.Top + deltaY;
                break;
            case CropHandle.Right:
                newRect.Right = _dragStartRect.Right + deltaX;
                break;
            case CropHandle.BottomRight:
                newRect.Right = _dragStartRect.Right + deltaX;
                newRect.Bottom = _dragStartRect.Bottom + deltaY;
                break;
            case CropHandle.Bottom:
                newRect.Bottom = _dragStartRect.Bottom + deltaY;
                break;
            case CropHandle.BottomLeft:
                newRect.Left = _dragStartRect.Left + deltaX;
                newRect.Bottom = _dragStartRect.Bottom + deltaY;
                break;
            case CropHandle.Left:
                newRect.Left = _dragStartRect.Left + deltaX;
                break;
            case CropHandle.Move:
                newRect.Offset(deltaX, deltaY);
                break;
        }

        // Normalize and constrain
        _cropRect = NormalizeAndConstrain(newRect);
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

        // Draw darkened area outside crop region
        using var dimPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 180),
            Style = SKPaintStyle.Fill
        };

        // Top
        canvas.DrawRect(new SKRect(canvasBounds.Left, canvasBounds.Top, canvasBounds.Right, _cropRect.Top), dimPaint);
        // Bottom
        canvas.DrawRect(new SKRect(canvasBounds.Left, _cropRect.Bottom, canvasBounds.Right, canvasBounds.Bottom), dimPaint);
        // Left
        canvas.DrawRect(new SKRect(canvasBounds.Left, _cropRect.Top, _cropRect.Left, _cropRect.Bottom), dimPaint);
        // Right
        canvas.DrawRect(new SKRect(_cropRect.Right, _cropRect.Top, canvasBounds.Right, _cropRect.Bottom), dimPaint);

        // Draw crop border
        using var borderPaint = new SKPaint
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true
        };
        canvas.DrawRect(_cropRect, borderPaint);

        // Draw rule-of-thirds grid
        DrawRuleOfThirdsGrid(canvas);

        // Draw handles
        DrawHandles(canvas);
    }

    /// <summary>
    /// Converts the crop rectangle from screen coordinates to image coordinates.
    /// </summary>
    public SKRectI GetImageCropRect(int imageWidth, int imageHeight)
    {
        if (!_hasCropRegion || _imageRect.Width <= 0 || _imageRect.Height <= 0)
            return new SKRectI(0, 0, imageWidth, imageHeight);

        // Calculate scale from screen to image
        var scaleX = imageWidth / _imageRect.Width;
        var scaleY = imageHeight / _imageRect.Height;

        // Convert screen coordinates to image coordinates
        var left = (int)Math.Round((_cropRect.Left - _imageRect.Left) * scaleX);
        var top = (int)Math.Round((_cropRect.Top - _imageRect.Top) * scaleY);
        var right = (int)Math.Round((_cropRect.Right - _imageRect.Left) * scaleX);
        var bottom = (int)Math.Round((_cropRect.Bottom - _imageRect.Top) * scaleY);

        // Clamp to image bounds
        left = Math.Clamp(left, 0, imageWidth);
        top = Math.Clamp(top, 0, imageHeight);
        right = Math.Clamp(right, 0, imageWidth);
        bottom = Math.Clamp(bottom, 0, imageHeight);

        return new SKRectI(left, top, right, bottom);
    }

    private void DrawRuleOfThirdsGrid(SKCanvas canvas)
    {
        using var gridPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 128),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };

        var thirdWidth = _cropRect.Width / 3f;
        var thirdHeight = _cropRect.Height / 3f;

        // Vertical lines
        canvas.DrawLine(
            _cropRect.Left + thirdWidth, _cropRect.Top,
            _cropRect.Left + thirdWidth, _cropRect.Bottom,
            gridPaint);
        canvas.DrawLine(
            _cropRect.Left + thirdWidth * 2, _cropRect.Top,
            _cropRect.Left + thirdWidth * 2, _cropRect.Bottom,
            gridPaint);

        // Horizontal lines
        canvas.DrawLine(
            _cropRect.Left, _cropRect.Top + thirdHeight,
            _cropRect.Right, _cropRect.Top + thirdHeight,
            gridPaint);
        canvas.DrawLine(
            _cropRect.Left, _cropRect.Top + thirdHeight * 2,
            _cropRect.Right, _cropRect.Top + thirdHeight * 2,
            gridPaint);
    }

    private void DrawHandles(SKCanvas canvas)
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

        var handles = GetHandlePositions();
        foreach (var handle in handles)
        {
            canvas.DrawCircle(handle, HandleRadius, handleFillPaint);
            canvas.DrawCircle(handle, HandleRadius, handleStrokePaint);
        }
    }

    private SKPoint[] GetHandlePositions()
    {
        var midX = _cropRect.MidX;
        var midY = _cropRect.MidY;

        return
        [
            new SKPoint(_cropRect.Left, _cropRect.Top),      // TopLeft
            new SKPoint(midX, _cropRect.Top),                 // Top
            new SKPoint(_cropRect.Right, _cropRect.Top),     // TopRight
            new SKPoint(_cropRect.Right, midY),               // Right
            new SKPoint(_cropRect.Right, _cropRect.Bottom),  // BottomRight
            new SKPoint(midX, _cropRect.Bottom),              // Bottom
            new SKPoint(_cropRect.Left, _cropRect.Bottom),   // BottomLeft
            new SKPoint(_cropRect.Left, midY)                 // Left
        ];
    }

    private CropHandle HitTestHandle(SKPoint point)
    {
        var handles = GetHandlePositions();
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
        if (_cropRect.Contains(point))
            return CropHandle.Move;

        return CropHandle.None;
    }

    private static bool IsPointNearHandle(SKPoint point, SKPoint handleCenter)
    {
        var dx = point.X - handleCenter.X;
        var dy = point.Y - handleCenter.Y;
        return dx * dx + dy * dy <= HandleHitRadius * HandleHitRadius;
    }

    private SKRect NormalizeAndConstrain(SKRect rect)
    {
        // Normalize (ensure left < right, top < bottom)
        var left = Math.Min(rect.Left, rect.Right);
        var right = Math.Max(rect.Left, rect.Right);
        var top = Math.Min(rect.Top, rect.Bottom);
        var bottom = Math.Max(rect.Top, rect.Bottom);

        // Ensure minimum size
        if (right - left < MinCropSize)
            right = left + MinCropSize;
        if (bottom - top < MinCropSize)
            bottom = top + MinCropSize;

        // Constrain to image bounds
        if (left < _imageRect.Left)
        {
            var shift = _imageRect.Left - left;
            left = _imageRect.Left;
            if (_activeHandle == CropHandle.Move)
                right += shift;
        }
        if (right > _imageRect.Right)
        {
            var shift = right - _imageRect.Right;
            right = _imageRect.Right;
            if (_activeHandle == CropHandle.Move)
                left -= shift;
        }
        if (top < _imageRect.Top)
        {
            var shift = _imageRect.Top - top;
            top = _imageRect.Top;
            if (_activeHandle == CropHandle.Move)
                bottom += shift;
        }
        if (bottom > _imageRect.Bottom)
        {
            var shift = bottom - _imageRect.Bottom;
            bottom = _imageRect.Bottom;
            if (_activeHandle == CropHandle.Move)
                top -= shift;
        }

        // Final clamp
        left = Math.Clamp(left, _imageRect.Left, _imageRect.Right - MinCropSize);
        right = Math.Clamp(right, _imageRect.Left + MinCropSize, _imageRect.Right);
        top = Math.Clamp(top, _imageRect.Top, _imageRect.Bottom - MinCropSize);
        bottom = Math.Clamp(bottom, _imageRect.Top + MinCropSize, _imageRect.Bottom);

        return new SKRect(left, top, right, bottom);
    }
}
