using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Defines the shape of the drawing brush.
/// </summary>
public enum BrushShape
{
    Round,
    Square
}

/// <summary>
/// Platform-independent drawing tool with configurable brush settings.
/// Supports freehand drawing and straight lines when Shift is held.
/// </summary>
public class DrawingTool
{
    private bool _isActive;
    private bool _isDrawing;
    private SKPoint _lastPoint;
    private SKPoint _lineStartPoint;
    private bool _isShiftHeld;
    private SKRect _imageRect;
    private readonly List<SKPoint> _currentStrokePoints = [];

    /// <summary>
    /// Gets or sets whether the drawing tool is currently active.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            if (!value)
            {
                _isDrawing = false;
                _currentStrokePoints.Clear();
            }
        }
    }

    /// <summary>
    /// Gets or sets the brush color.
    /// </summary>
    public SKColor BrushColor { get; set; } = SKColors.White;

    /// <summary>
    /// Gets or sets the brush size in pixels.
    /// </summary>
    public float BrushSize { get; set; } = 10f;

    /// <summary>
    /// Gets or sets the brush shape.
    /// </summary>
    public BrushShape BrushShape { get; set; } = BrushShape.Round;

    /// <summary>
    /// Gets whether the user is currently drawing.
    /// </summary>
    public bool IsDrawing => _isDrawing;

    /// <summary>
    /// Gets the current stroke points for preview rendering.
    /// </summary>
    public IReadOnlyList<SKPoint> CurrentStrokePoints => _currentStrokePoints;

    /// <summary>
    /// Gets the line start point when shift is held.
    /// </summary>
    public SKPoint LineStartPoint => _lineStartPoint;

    /// <summary>
    /// Gets or sets whether the shift key is currently held (for straight line drawing).
    /// </summary>
    public bool IsShiftHeld
    {
        get => _isShiftHeld;
        set => _isShiftHeld = value;
    }

    /// <summary>
    /// Event raised when a stroke is completed and should be applied to the image.
    /// </summary>
    public event EventHandler<DrawingStrokeEventArgs>? StrokeCompleted;

    /// <summary>
    /// Event raised when the drawing state changes and a redraw is needed.
    /// </summary>
    public event EventHandler? DrawingChanged;

    /// <summary>
    /// Sets the current image bounds for coordinate mapping.
    /// </summary>
    public void SetImageBounds(SKRect imageRect)
    {
        _imageRect = imageRect;
    }

    /// <summary>
    /// Handles pointer press event to start drawing.
    /// </summary>
    /// <returns>True if the event was handled.</returns>
    public bool OnPointerPressed(SKPoint screenPoint)
    {
        if (!_isActive) return false;
        if (_imageRect.Width <= 0 || _imageRect.Height <= 0) return false;

        // Check if point is within image bounds
        if (!_imageRect.Contains(screenPoint)) return false;

        _isDrawing = true;
        _lastPoint = screenPoint;
        _lineStartPoint = screenPoint;
        _currentStrokePoints.Clear();
        _currentStrokePoints.Add(screenPoint);

        DrawingChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Handles pointer move event to continue drawing.
    /// </summary>
    /// <returns>True if the event was handled.</returns>
    public bool OnPointerMoved(SKPoint screenPoint)
    {
        if (!_isActive || !_isDrawing) return false;

        if (_isShiftHeld)
        {
            // For straight line mode, just update last point for preview
            // but don't add intermediate points
            _lastPoint = screenPoint;
        }
        else
        {
            // Freehand mode - add point to stroke
            _currentStrokePoints.Add(screenPoint);
            _lastPoint = screenPoint;
        }

        DrawingChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Handles pointer release event to complete drawing.
    /// </summary>
    /// <returns>True if the event was handled.</returns>
    public bool OnPointerReleased()
    {
        if (!_isActive || !_isDrawing) return false;

        _isDrawing = false;

        List<SKPoint> strokePoints;
        if (_isShiftHeld)
        {
            // Straight line: just start and end points
            strokePoints = [_lineStartPoint, _lastPoint];
        }
        else
        {
            // Freehand: all points
            strokePoints = [.. _currentStrokePoints];
        }

        if (strokePoints.Count > 0)
        {
            // Convert screen points to image coordinates
            var imagePoints = strokePoints
                .Select(ScreenToImage)
                .ToList();

            var args = new DrawingStrokeEventArgs(
                imagePoints,
                BrushColor,
                BrushSize / GetCurrentScale(),
                BrushShape);

            StrokeCompleted?.Invoke(this, args);
        }

        _currentStrokePoints.Clear();
        DrawingChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Renders the current stroke preview on the canvas.
    /// </summary>
    public void Render(SKCanvas canvas)
    {
        if (!_isActive || !_isDrawing) return;

        using var paint = CreatePaint();

        if (_isShiftHeld)
        {
            // Draw straight line preview
            canvas.DrawLine(_lineStartPoint, _lastPoint, paint);
        }
        else
        {
            // Draw freehand stroke preview
            if (_currentStrokePoints.Count == 1)
            {
                // Single point - draw a dot
                var point = _currentStrokePoints[0];
                if (BrushShape == BrushShape.Round)
                {
                    canvas.DrawCircle(point, BrushSize / 2, paint);
                }
                else
                {
                    var halfSize = BrushSize / 2;
                    canvas.DrawRect(point.X - halfSize, point.Y - halfSize, BrushSize, BrushSize, paint);
                }
            }
            else if (_currentStrokePoints.Count > 1)
            {
                using var path = new SKPath();
                path.MoveTo(_currentStrokePoints[0]);
                for (var i = 1; i < _currentStrokePoints.Count; i++)
                {
                    path.LineTo(_currentStrokePoints[i]);
                }
                canvas.DrawPath(path, paint);
            }
        }
    }

    /// <summary>
    /// Renders a brush cursor preview at the specified position.
    /// </summary>
    public void RenderCursor(SKCanvas canvas, SKPoint position)
    {
        if (!_isActive) return;

        using var paint = new SKPaint
        {
            Color = BrushColor.WithAlpha(128),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };

        if (BrushShape == BrushShape.Round)
        {
            canvas.DrawCircle(position, BrushSize / 2, paint);
        }
        else
        {
            var halfSize = BrushSize / 2;
            canvas.DrawRect(position.X - halfSize, position.Y - halfSize, BrushSize, BrushSize, paint);
        }
    }

    private SKPaint CreatePaint()
    {
        var paint = new SKPaint
        {
            Color = BrushColor,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = BrushSize
        };

        if (BrushShape == BrushShape.Round)
        {
            paint.StrokeCap = SKStrokeCap.Round;
            paint.StrokeJoin = SKStrokeJoin.Round;
        }
        else
        {
            paint.StrokeCap = SKStrokeCap.Square;
            paint.StrokeJoin = SKStrokeJoin.Miter;
        }

        return paint;
    }

    private SKPoint ScreenToImage(SKPoint screenPoint)
    {
        if (_imageRect.Width <= 0 || _imageRect.Height <= 0)
            return screenPoint;

        // Convert from screen coordinates to normalized image coordinates
        var x = (screenPoint.X - _imageRect.Left) / _imageRect.Width;
        var y = (screenPoint.Y - _imageRect.Top) / _imageRect.Height;

        return new SKPoint(x, y);
    }

    private float GetCurrentScale()
    {
        // Get the current scale factor based on image rect
        // This assumes the image rect represents the displayed size
        return _imageRect.Width > 0 ? _imageRect.Width : 1f;
    }
}

/// <summary>
/// Event arguments for when a drawing stroke is completed.
/// </summary>
public class DrawingStrokeEventArgs : EventArgs
{
    /// <summary>
    /// The points in the stroke, in normalized image coordinates (0-1).
    /// </summary>
    public IReadOnlyList<SKPoint> Points { get; }

    /// <summary>
    /// The brush color used for the stroke.
    /// </summary>
    public SKColor Color { get; }

    /// <summary>
    /// The brush size in image pixels.
    /// </summary>
    public float BrushSize { get; }

    /// <summary>
    /// The brush shape used for the stroke.
    /// </summary>
    public BrushShape BrushShape { get; }

    public DrawingStrokeEventArgs(IReadOnlyList<SKPoint> points, SKColor color, float brushSize, BrushShape brushShape)
    {
        Points = points;
        Color = color;
        BrushSize = brushSize;
        BrushShape = brushShape;
    }
}
