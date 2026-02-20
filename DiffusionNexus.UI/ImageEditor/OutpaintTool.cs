using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Represents which directional handle is being dragged during outpaint resize.
/// </summary>
public enum OutpaintHandle
{
    None,
    Top,
    Right,
    Bottom,
    Left
}

/// <summary>
/// Platform-independent outpainting tool that allows extending the canvas beyond the original image.
/// Renders directional arrow handles on each edge that can be dragged outward only.
/// Stores extension amounts in pixels relative to the original image dimensions.
/// </summary>
public class OutpaintTool
{
    private const float ArrowSize = 16f;
    private const float ArrowHitSize = 24f;
    private const float MinExtensionPixels = 0f;

    private SKRect _imageRect;
    private OutpaintHandle _activeHandle = OutpaintHandle.None;
    private SKPoint _dragStartPoint;

    // Extension stored in pixels (how many pixels to add on each side)
    private int _extendTop;
    private int _extendRight;
    private int _extendBottom;
    private int _extendLeft;

    // Drag start state
    private int _dragStartExtendTop;
    private int _dragStartExtendRight;
    private int _dragStartExtendBottom;
    private int _dragStartExtendLeft;

    private bool _isActive;

    /// <summary>
    /// Gets or sets whether the outpaint tool is currently active.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            if (!value)
            {
                _activeHandle = OutpaintHandle.None;
                Reset();
            }
        }
    }

    /// <summary>
    /// The original image width in pixels.
    /// </summary>
    public int ImagePixelWidth { get; set; }

    /// <summary>
    /// The original image height in pixels.
    /// </summary>
    public int ImagePixelHeight { get; set; }

    /// <summary>
    /// Gets the pixel extension for the top edge.
    /// </summary>
    public int ExtendTop => _extendTop;

    /// <summary>
    /// Gets the pixel extension for the right edge.
    /// </summary>
    public int ExtendRight => _extendRight;

    /// <summary>
    /// Gets the pixel extension for the bottom edge.
    /// </summary>
    public int ExtendBottom => _extendBottom;

    /// <summary>
    /// Gets the pixel extension for the left edge.
    /// </summary>
    public int ExtendLeft => _extendLeft;

    /// <summary>
    /// Gets whether any extension has been applied.
    /// </summary>
    public bool HasExtension => _extendTop > 0 || _extendRight > 0 || _extendBottom > 0 || _extendLeft > 0;

    /// <summary>
    /// Gets whether a handle is currently being dragged.
    /// </summary>
    public bool IsDragging => _activeHandle != OutpaintHandle.None;

    /// <summary>
    /// Event raised when the outpaint region changes.
    /// </summary>
    public event EventHandler? RegionChanged;

    /// <summary>
    /// Gets the new total resolution including extensions.
    /// </summary>
    public (int Width, int Height) GetNewDimensions()
    {
        if (ImagePixelWidth <= 0 || ImagePixelHeight <= 0)
            return (0, 0);

        return (ImagePixelWidth + _extendLeft + _extendRight,
                ImagePixelHeight + _extendTop + _extendBottom);
    }

    /// <summary>
    /// Sets the image bounds for rendering arrow handles.
    /// </summary>
    public void SetImageBounds(SKRect imageRect)
    {
        _imageRect = imageRect;
    }

    /// <summary>
    /// Resets all extensions to zero.
    /// </summary>
    public void Reset()
    {
        _extendTop = 0;
        _extendRight = 0;
        _extendBottom = 0;
        _extendLeft = 0;
        _activeHandle = OutpaintHandle.None;
        RegionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sets the extension amounts for each edge in image pixels.
    /// Values are clamped to zero (no negative extensions).
    /// </summary>
    public void SetExtension(int top, int right, int bottom, int left)
    {
        _extendTop = Math.Max(0, top);
        _extendRight = Math.Max(0, right);
        _extendBottom = Math.Max(0, bottom);
        _extendLeft = Math.Max(0, left);
        RegionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sets extension to match a target aspect ratio, expanding symmetrically.
    /// The image is never made smaller, only extended on the necessary sides.
    /// </summary>
    /// <param name="ratioW">Width component of the desired aspect ratio.</param>
    /// <param name="ratioH">Height component of the desired aspect ratio.</param>
    public void SetAspectRatio(float ratioW, float ratioH)
    {
        if (ratioW <= 0 || ratioH <= 0 || ImagePixelWidth <= 0 || ImagePixelHeight <= 0)
            return;

        var currentW = ImagePixelWidth;
        var currentH = ImagePixelHeight;
        var targetRatio = ratioW / ratioH;
        var currentRatio = (float)currentW / currentH;

        int newW, newH;

        if (targetRatio > currentRatio)
        {
            // Need to extend width
            newW = (int)Math.Round(currentH * targetRatio);
            newH = currentH;
        }
        else
        {
            // Need to extend height
            newW = currentW;
            newH = (int)Math.Round(currentW / targetRatio);
        }

        // Distribute extension symmetrically
        var totalExtendX = Math.Max(0, newW - currentW);
        var totalExtendY = Math.Max(0, newH - currentH);

        _extendLeft = totalExtendX / 2;
        _extendRight = totalExtendX - _extendLeft;
        _extendTop = totalExtendY / 2;
        _extendBottom = totalExtendY - _extendTop;

        RegionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Handles pointer pressed event.
    /// </summary>
    /// <returns>True if the event was handled.</returns>
    public bool OnPointerPressed(SKPoint point)
    {
        if (!_isActive) return false;

        _activeHandle = HitTestHandle(point);
        if (_activeHandle == OutpaintHandle.None)
            return false;

        _dragStartPoint = point;
        _dragStartExtendTop = _extendTop;
        _dragStartExtendRight = _extendRight;
        _dragStartExtendBottom = _extendBottom;
        _dragStartExtendLeft = _extendLeft;
        return true;
    }

    /// <summary>
    /// Handles pointer moved event.
    /// </summary>
    /// <returns>True if the event was handled.</returns>
    public bool OnPointerMoved(SKPoint point)
    {
        if (!_isActive || _activeHandle == OutpaintHandle.None) return false;

        var deltaX = point.X - _dragStartPoint.X;
        var deltaY = point.Y - _dragStartPoint.Y;

        // Convert screen delta to pixel delta based on image-to-screen scale
        var scaleX = _imageRect.Width > 0 ? (float)ImagePixelWidth / _imageRect.Width : 1f;
        var scaleY = _imageRect.Height > 0 ? (float)ImagePixelHeight / _imageRect.Height : 1f;

        switch (_activeHandle)
        {
            case OutpaintHandle.Top:
                // Dragging top arrow upward (negative deltaY) extends top
                _extendTop = Math.Max(0, _dragStartExtendTop - (int)(deltaY * scaleY));
                break;
            case OutpaintHandle.Bottom:
                // Dragging bottom arrow downward (positive deltaY) extends bottom
                _extendBottom = Math.Max(0, _dragStartExtendBottom + (int)(deltaY * scaleY));
                break;
            case OutpaintHandle.Left:
                // Dragging left arrow leftward (negative deltaX) extends left
                _extendLeft = Math.Max(0, _dragStartExtendLeft - (int)(deltaX * scaleX));
                break;
            case OutpaintHandle.Right:
                // Dragging right arrow rightward (positive deltaX) extends right
                _extendRight = Math.Max(0, _dragStartExtendRight + (int)(deltaX * scaleX));
                break;
        }

        RegionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Handles pointer released event.
    /// </summary>
    /// <returns>True if the event was handled.</returns>
    public bool OnPointerReleased()
    {
        if (!_isActive) return false;
        _activeHandle = OutpaintHandle.None;
        return true;
    }

    /// <summary>
    /// Gets the cursor type for the given point.
    /// </summary>
    public OutpaintHandle GetCursorForPoint(SKPoint point)
    {
        if (!_isActive) return OutpaintHandle.None;
        return HitTestHandle(point);
    }

    /// <summary>
    /// Renders the outpaint overlay with extension region and arrow handles at the image edges.
    /// </summary>
    public void Render(SKCanvas canvas, SKRect canvasBounds)
    {
        if (!_isActive || _imageRect.Width <= 0 || _imageRect.Height <= 0) return;

        // Calculate the extended rect in screen coordinates
        var extendedRect = GetExtendedScreenRect();

        if (HasExtension)
        {
            // Draw the extension region as a semi-transparent overlay
            DrawExtensionRegion(canvas, canvasBounds, extendedRect);
        }

        // Draw directional arrow handles on each edge of the image
        DrawArrowHandles(canvas);

        // Draw the resolution label
        if (HasExtension)
        {
            DrawResolutionLabel(canvas, extendedRect);
        }
    }

    /// <summary>
    /// Gets the extended rectangle in screen coordinates.
    /// </summary>
    private SKRect GetExtendedScreenRect()
    {
        if (ImagePixelWidth <= 0 || ImagePixelHeight <= 0)
            return _imageRect;

        var pixelsPerScreenX = _imageRect.Width / ImagePixelWidth;
        var pixelsPerScreenY = _imageRect.Height / ImagePixelHeight;

        return new SKRect(
            _imageRect.Left - _extendLeft * pixelsPerScreenX,
            _imageRect.Top - _extendTop * pixelsPerScreenY,
            _imageRect.Right + _extendRight * pixelsPerScreenX,
            _imageRect.Bottom + _extendBottom * pixelsPerScreenY);
    }

    private void DrawExtensionRegion(SKCanvas canvas, SKRect canvasBounds, SKRect extendedRect)
    {
        // Draw a border around the extended area
        using var borderPaint = new SKPaint
        {
            Color = new SKColor(76, 175, 80, 200), // Green border
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([8f, 4f], 0f)
        };
        canvas.DrawRect(extendedRect, borderPaint);

        // Fill the extension areas (between extended rect and image rect) with a subtle pattern
        using var fillPaint = new SKPaint
        {
            Color = new SKColor(76, 175, 80, 40),
            Style = SKPaintStyle.Fill
        };

        // Top extension
        if (_extendTop > 0)
        {
            canvas.DrawRect(new SKRect(extendedRect.Left, extendedRect.Top, extendedRect.Right, _imageRect.Top), fillPaint);
        }

        // Bottom extension
        if (_extendBottom > 0)
        {
            canvas.DrawRect(new SKRect(extendedRect.Left, _imageRect.Bottom, extendedRect.Right, extendedRect.Bottom), fillPaint);
        }

        // Left extension
        if (_extendLeft > 0)
        {
            canvas.DrawRect(new SKRect(extendedRect.Left, _imageRect.Top, _imageRect.Left, _imageRect.Bottom), fillPaint);
        }

        // Right extension
        if (_extendRight > 0)
        {
            canvas.DrawRect(new SKRect(_imageRect.Right, _imageRect.Top, extendedRect.Right, _imageRect.Bottom), fillPaint);
        }

        // Draw a subtle border around the original image to distinguish it
        using var imageBorderPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 100),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };
        canvas.DrawRect(_imageRect, imageBorderPaint);
    }

    private void DrawArrowHandles(SKCanvas canvas)
    {
        var midX = _imageRect.MidX;
        var midY = _imageRect.MidY;

        // Top arrow (pointing up)
        DrawArrow(canvas, new SKPoint(midX, _imageRect.Top - ArrowSize - 4f), Direction.Up,
                  _activeHandle == OutpaintHandle.Top);

        // Bottom arrow (pointing down)
        DrawArrow(canvas, new SKPoint(midX, _imageRect.Bottom + ArrowSize + 4f), Direction.Down,
                  _activeHandle == OutpaintHandle.Bottom);

        // Left arrow (pointing left)
        DrawArrow(canvas, new SKPoint(_imageRect.Left - ArrowSize - 4f, midY), Direction.Left,
                  _activeHandle == OutpaintHandle.Left);

        // Right arrow (pointing right)
        DrawArrow(canvas, new SKPoint(_imageRect.Right + ArrowSize + 4f, midY), Direction.Right,
                  _activeHandle == OutpaintHandle.Right);
    }

    private static void DrawArrow(SKCanvas canvas, SKPoint center, Direction direction, bool isActive)
    {
        using var fillPaint = new SKPaint
        {
            Color = isActive ? new SKColor(76, 175, 80, 255) : new SKColor(200, 200, 200, 200),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        using var strokePaint = new SKPaint
        {
            Color = isActive ? new SKColor(56, 142, 60) : new SKColor(100, 100, 100),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true
        };

        // Draw a circular background
        canvas.DrawCircle(center, ArrowSize * 0.8f, fillPaint);
        canvas.DrawCircle(center, ArrowSize * 0.8f, strokePaint);

        // Draw the arrow triangle
        using var arrowPaint = new SKPaint
        {
            Color = isActive ? SKColors.White : new SKColor(40, 40, 40),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        var path = new SKPath();
        var halfArrow = ArrowSize * 0.35f;

        switch (direction)
        {
            case Direction.Up:
                path.MoveTo(center.X, center.Y - halfArrow);
                path.LineTo(center.X - halfArrow, center.Y + halfArrow * 0.5f);
                path.LineTo(center.X + halfArrow, center.Y + halfArrow * 0.5f);
                break;
            case Direction.Down:
                path.MoveTo(center.X, center.Y + halfArrow);
                path.LineTo(center.X - halfArrow, center.Y - halfArrow * 0.5f);
                path.LineTo(center.X + halfArrow, center.Y - halfArrow * 0.5f);
                break;
            case Direction.Left:
                path.MoveTo(center.X - halfArrow, center.Y);
                path.LineTo(center.X + halfArrow * 0.5f, center.Y - halfArrow);
                path.LineTo(center.X + halfArrow * 0.5f, center.Y + halfArrow);
                break;
            case Direction.Right:
                path.MoveTo(center.X + halfArrow, center.Y);
                path.LineTo(center.X - halfArrow * 0.5f, center.Y - halfArrow);
                path.LineTo(center.X - halfArrow * 0.5f, center.Y + halfArrow);
                break;
        }

        path.Close();
        canvas.DrawPath(path, arrowPaint);
    }

    private void DrawResolutionLabel(SKCanvas canvas, SKRect extendedRect)
    {
        var (newW, newH) = GetNewDimensions();
        if (newW <= 0 || newH <= 0) return;

        var text = $"{newW} x {newH}";

        using var font = new SKFont(SKTypeface.Default, 12f);
        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };

        font.MeasureText(text, out var textBounds, textPaint);

        var labelX = extendedRect.MidX - textBounds.Width / 2f;
        var labelY = extendedRect.Top - 8f;

        // If the label would go above the canvas, place it inside
        if (labelY - textBounds.Height < 0)
            labelY = extendedRect.Top + textBounds.Height + 6f;

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

    private OutpaintHandle HitTestHandle(SKPoint point)
    {
        var midX = _imageRect.MidX;
        var midY = _imageRect.MidY;

        // Top handle
        var topCenter = new SKPoint(midX, _imageRect.Top - ArrowSize - 4f);
        if (IsPointNearHandle(point, topCenter))
            return OutpaintHandle.Top;

        // Bottom handle
        var bottomCenter = new SKPoint(midX, _imageRect.Bottom + ArrowSize + 4f);
        if (IsPointNearHandle(point, bottomCenter))
            return OutpaintHandle.Bottom;

        // Left handle
        var leftCenter = new SKPoint(_imageRect.Left - ArrowSize - 4f, midY);
        if (IsPointNearHandle(point, leftCenter))
            return OutpaintHandle.Left;

        // Right handle
        var rightCenter = new SKPoint(_imageRect.Right + ArrowSize + 4f, midY);
        if (IsPointNearHandle(point, rightCenter))
            return OutpaintHandle.Right;

        return OutpaintHandle.None;
    }

    private static bool IsPointNearHandle(SKPoint point, SKPoint handleCenter)
    {
        var dx = point.X - handleCenter.X;
        var dy = point.Y - handleCenter.Y;
        return dx * dx + dy * dy <= ArrowHitSize * ArrowHitSize;
    }

    private enum Direction
    {
        Up,
        Down,
        Left,
        Right
    }
}
