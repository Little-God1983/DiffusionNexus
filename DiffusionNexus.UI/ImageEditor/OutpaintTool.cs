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
    Left,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>
/// How aggressive the current outpaint extension is, relative to the source image area.
/// Drives the canvas accent color and the panel warning.
/// </summary>
public enum OutpaintSeverity
{
    None,
    Caution,
    Strong
}

/// <summary>
/// Platform-independent outpainting tool that allows extending the canvas beyond the original image.
/// Renders directional arrow handles on each edge that can be dragged outward only.
/// Stores extension amounts in pixels relative to the original image dimensions.
/// </summary>
public class OutpaintTool
{
    private const float ArrowSize = 32f;
    private const float ArrowHitSize = 40f;
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
    /// Area of the extended canvas divided by the area of the original image. Returns 1.0 when
    /// no extension is present or the source dimensions are unknown.
    /// </summary>
    public float AreaRatio
    {
        get
        {
            if (ImagePixelWidth <= 0 || ImagePixelHeight <= 0) return 1f;
            var (newW, newH) = GetNewDimensions();
            var orig = (long)ImagePixelWidth * ImagePixelHeight;
            if (orig <= 0) return 1f;
            return (float)((long)newW * newH) / orig;
        }
    }

    /// <summary>
    /// Severity tier based on <see cref="AreaRatio"/>: ≥2.00 → Strong, ≥1.50 → Caution, otherwise None.
    /// </summary>
    public OutpaintSeverity Severity
    {
        get
        {
            var ratio = AreaRatio;
            if (ratio >= 2.00f) return OutpaintSeverity.Strong;
            if (ratio >= 1.50f) return OutpaintSeverity.Caution;
            return OutpaintSeverity.None;
        }
    }

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

        // Corner handles extend two adjacent edges simultaneously from one drag.
        var extendTopDelta = -(int)(deltaY * scaleY);
        var extendBottomDelta = (int)(deltaY * scaleY);
        var extendLeftDelta = -(int)(deltaX * scaleX);
        var extendRightDelta = (int)(deltaX * scaleX);

        switch (_activeHandle)
        {
            case OutpaintHandle.Top:
                _extendTop = Math.Max(0, _dragStartExtendTop + extendTopDelta);
                break;
            case OutpaintHandle.Bottom:
                _extendBottom = Math.Max(0, _dragStartExtendBottom + extendBottomDelta);
                break;
            case OutpaintHandle.Left:
                _extendLeft = Math.Max(0, _dragStartExtendLeft + extendLeftDelta);
                break;
            case OutpaintHandle.Right:
                _extendRight = Math.Max(0, _dragStartExtendRight + extendRightDelta);
                break;
            case OutpaintHandle.TopLeft:
                _extendTop = Math.Max(0, _dragStartExtendTop + extendTopDelta);
                _extendLeft = Math.Max(0, _dragStartExtendLeft + extendLeftDelta);
                break;
            case OutpaintHandle.TopRight:
                _extendTop = Math.Max(0, _dragStartExtendTop + extendTopDelta);
                _extendRight = Math.Max(0, _dragStartExtendRight + extendRightDelta);
                break;
            case OutpaintHandle.BottomLeft:
                _extendBottom = Math.Max(0, _dragStartExtendBottom + extendBottomDelta);
                _extendLeft = Math.Max(0, _dragStartExtendLeft + extendLeftDelta);
                break;
            case OutpaintHandle.BottomRight:
                _extendBottom = Math.Max(0, _dragStartExtendBottom + extendBottomDelta);
                _extendRight = Math.Max(0, _dragStartExtendRight + extendRightDelta);
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

    /// <summary>
    /// Base accent color (RGB only, alpha is applied at the call site) that reflects the
    /// current severity tier: green / amber / red‑orange.
    /// </summary>
    private SKColor GetAccentBaseColor() => Severity switch
    {
        OutpaintSeverity.Strong => new SKColor(255, 87, 34),    // red‑orange
        OutpaintSeverity.Caution => new SKColor(255, 193, 7),   // amber
        _ => new SKColor(76, 175, 80),                          // green
    };

    private void DrawExtensionRegion(SKCanvas canvas, SKRect canvasBounds, SKRect extendedRect)
    {
        var accent = GetAccentBaseColor();

        // Draw a border around the extended area (accent color, severity-tinted)
        using var borderPaint = new SKPaint
        {
            Color = accent.WithAlpha(200),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([8f, 4f], 0f)
        };
        canvas.DrawRect(extendedRect, borderPaint);

        // Fill the extension areas (between extended rect and image rect) with a subtle pattern
        using var fillPaint = new SKPaint
        {
            Color = accent.WithAlpha(40),
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
        var c = GetHandleCenters();

        DrawArrow(canvas, c.Top, Direction.Up, _activeHandle == OutpaintHandle.Top);
        DrawArrow(canvas, c.Bottom, Direction.Down, _activeHandle == OutpaintHandle.Bottom);
        DrawArrow(canvas, c.Left, Direction.Left, _activeHandle == OutpaintHandle.Left);
        DrawArrow(canvas, c.Right, Direction.Right, _activeHandle == OutpaintHandle.Right);
        DrawArrow(canvas, c.TopLeft, Direction.UpLeft, _activeHandle == OutpaintHandle.TopLeft);
        DrawArrow(canvas, c.TopRight, Direction.UpRight, _activeHandle == OutpaintHandle.TopRight);
        DrawArrow(canvas, c.BottomLeft, Direction.DownLeft, _activeHandle == OutpaintHandle.BottomLeft);
        DrawArrow(canvas, c.BottomRight, Direction.DownRight, _activeHandle == OutpaintHandle.BottomRight);
    }

    /// <summary>
    /// Handle positions are anchored to the extended rect so they ride along with the
    /// outpaint frame as the user drags. Edge handles are centered on each side;
    /// corner handles sit diagonally outside the rect corners and extend two
    /// adjacent edges at once when dragged.
    /// </summary>
    private (SKPoint Top, SKPoint Right, SKPoint Bottom, SKPoint Left,
             SKPoint TopLeft, SKPoint TopRight, SKPoint BottomLeft, SKPoint BottomRight)
        GetHandleCenters()
    {
        var rect = GetExtendedScreenRect();
        var midX = rect.MidX;
        var midY = rect.MidY;
        const float gap = 4f;
        var outX = ArrowSize + gap;
        var outY = ArrowSize + gap;

        return (
            Top: new SKPoint(midX, rect.Top - outY),
            Right: new SKPoint(rect.Right + outX, midY),
            Bottom: new SKPoint(midX, rect.Bottom + outY),
            Left: new SKPoint(rect.Left - outX, midY),
            TopLeft: new SKPoint(rect.Left - outX, rect.Top - outY),
            TopRight: new SKPoint(rect.Right + outX, rect.Top - outY),
            BottomLeft: new SKPoint(rect.Left - outX, rect.Bottom + outY),
            BottomRight: new SKPoint(rect.Right + outX, rect.Bottom + outY));
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
            StrokeWidth = 2f,
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

            // Diagonal arrowheads — the cardinal "Up" triangle rotated by ±45°.
            // tip = ±(√2/2, √2/2)·halfArrow; base spans perpendicular to the diagonal,
            // backed off by half a halfArrow so the tip sits at the outer corner.
            case Direction.UpLeft:
                path.MoveTo(center.X - halfArrow * 0.707f, center.Y - halfArrow * 0.707f);
                path.LineTo(center.X + halfArrow * 1.061f, center.Y - halfArrow * 0.354f);
                path.LineTo(center.X - halfArrow * 0.354f, center.Y + halfArrow * 1.061f);
                break;
            case Direction.UpRight:
                path.MoveTo(center.X + halfArrow * 0.707f, center.Y - halfArrow * 0.707f);
                path.LineTo(center.X + halfArrow * 0.354f, center.Y + halfArrow * 1.061f);
                path.LineTo(center.X - halfArrow * 1.061f, center.Y - halfArrow * 0.354f);
                break;
            case Direction.DownLeft:
                path.MoveTo(center.X - halfArrow * 0.707f, center.Y + halfArrow * 0.707f);
                path.LineTo(center.X - halfArrow * 0.354f, center.Y - halfArrow * 1.061f);
                path.LineTo(center.X + halfArrow * 1.061f, center.Y + halfArrow * 0.354f);
                break;
            case Direction.DownRight:
                path.MoveTo(center.X + halfArrow * 0.707f, center.Y + halfArrow * 0.707f);
                path.LineTo(center.X - halfArrow * 1.061f, center.Y + halfArrow * 0.354f);
                path.LineTo(center.X + halfArrow * 0.354f, center.Y - halfArrow * 1.061f);
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
        // Clear the top arrow handle which is anchored to the extended rect.
        var labelY = extendedRect.Top - ArrowSize * 2f - 8f;

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

        if (Severity != OutpaintSeverity.None)
        {
            using var labelStrokePaint = new SKPaint
            {
                Color = GetAccentBaseColor().WithAlpha(230),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
                IsAntialias = true
            };
            canvas.DrawRoundRect(bgRect, 4f, 4f, labelStrokePaint);
        }

        canvas.DrawText(text, labelX, labelY, font, textPaint);
    }

    private OutpaintHandle HitTestHandle(SKPoint point)
    {
        var c = GetHandleCenters();

        // Corners first — their hit zones don't overlap the edge handles (which
        // are centered on each side), but corners are the "more specific" choice
        // when a stray click lands in the diagonal area, so resolve them first.
        if (IsPointNearHandle(point, c.TopLeft)) return OutpaintHandle.TopLeft;
        if (IsPointNearHandle(point, c.TopRight)) return OutpaintHandle.TopRight;
        if (IsPointNearHandle(point, c.BottomLeft)) return OutpaintHandle.BottomLeft;
        if (IsPointNearHandle(point, c.BottomRight)) return OutpaintHandle.BottomRight;

        if (IsPointNearHandle(point, c.Top)) return OutpaintHandle.Top;
        if (IsPointNearHandle(point, c.Bottom)) return OutpaintHandle.Bottom;
        if (IsPointNearHandle(point, c.Left)) return OutpaintHandle.Left;
        if (IsPointNearHandle(point, c.Right)) return OutpaintHandle.Right;

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
        Right,
        UpLeft,
        UpRight,
        DownLeft,
        DownRight
    }
}
