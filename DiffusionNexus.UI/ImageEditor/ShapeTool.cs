using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Defines the type of shape to draw.
/// </summary>
public enum ShapeType
{
    /// <summary>Freehand drawing mode (default brush tool).</summary>
    Freehand,
    /// <summary>Rectangle/Box shape.</summary>
    Rectangle,
    /// <summary>Ellipse/Circle shape.</summary>
    Ellipse,
    /// <summary>Arrow shape.</summary>
    Arrow,
    /// <summary>Straight line.</summary>
    Line
}

/// <summary>
/// Defines how the shape is rendered.
/// </summary>
public enum ShapeFillMode
{
    /// <summary>Only draw the outline.</summary>
    Stroke,
    /// <summary>Fill the shape with color.</summary>
    Fill,
    /// <summary>Both fill and stroke.</summary>
    FillAndStroke
}

/// <summary>
/// Platform-independent shape drawing tool.
/// Supports drawing rectangles, ellipses, arrows, and lines with fill and stroke options.
/// </summary>
public class ShapeTool
{
    private bool _isActive;
    private bool _isDrawing;
    private SKPoint _startPoint;
    private SKPoint _currentPoint;
    private SKRect _imageRect;
    private ShapeData? _lastShape;

    /// <summary>
    /// Gets or sets whether the shape tool is currently active.
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
            }
        }
    }

    /// <summary>
    /// Gets or sets the current shape type.
    /// </summary>
    public ShapeType ShapeType { get; set; } = ShapeType.Rectangle;

    /// <summary>
    /// Gets or sets the fill mode.
    /// </summary>
    public ShapeFillMode FillMode { get; set; } = ShapeFillMode.Stroke;

    /// <summary>
    /// Gets or sets the stroke color.
    /// </summary>
    public SKColor StrokeColor { get; set; } = SKColors.White;

    /// <summary>
    /// Gets or sets the fill color.
    /// </summary>
    public SKColor FillColor { get; set; } = SKColors.White;

    /// <summary>
    /// Gets or sets the stroke width in pixels.
    /// </summary>
    public float StrokeWidth { get; set; } = 3f;

    /// <summary>
    /// Gets or sets the arrow head size as a multiplier of stroke width.
    /// </summary>
    public float ArrowHeadSize { get; set; } = 4f;

    /// <summary>
    /// Gets whether the user is currently drawing a shape.
    /// </summary>
    public bool IsDrawing => _isDrawing;

    /// <summary>
    /// Gets the start point of the current shape.
    /// </summary>
    public SKPoint StartPoint => _startPoint;

    /// <summary>
    /// Gets the current point (end point) of the shape being drawn.
    /// </summary>
    public SKPoint CurrentPoint => _currentPoint;

    /// <summary>
    /// Gets the last completed shape data for potential editing.
    /// </summary>
    public ShapeData? LastShape => _lastShape;

    /// <summary>
    /// Event raised when a shape is completed and should be applied to the image.
    /// </summary>
    public event EventHandler<ShapeCompletedEventArgs>? ShapeCompleted;

    /// <summary>
    /// Event raised when the shape preview changes and a redraw is needed.
    /// </summary>
    public event EventHandler? ShapeChanged;

    /// <summary>
    /// Sets the current image bounds for coordinate mapping.
    /// </summary>
    public void SetImageBounds(SKRect imageRect)
    {
        _imageRect = imageRect;
    }

    /// <summary>
    /// Handles pointer press event to start drawing a shape.
    /// </summary>
    /// <returns>True if the event was handled.</returns>
    public bool OnPointerPressed(SKPoint screenPoint)
    {
        if (!_isActive) return false;
        if (_imageRect.Width <= 0 || _imageRect.Height <= 0) return false;
        if (!_imageRect.Contains(screenPoint)) return false;

        _isDrawing = true;
        _startPoint = screenPoint;
        _currentPoint = screenPoint;

        ShapeChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Handles pointer move event to update the shape preview.
    /// </summary>
    /// <returns>True if the event was handled.</returns>
    public bool OnPointerMoved(SKPoint screenPoint)
    {
        if (!_isActive || !_isDrawing) return false;

        _currentPoint = screenPoint;
        ShapeChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Handles pointer release event to complete the shape.
    /// </summary>
    /// <returns>True if the event was handled.</returns>
    public bool OnPointerReleased()
    {
        if (!_isActive || !_isDrawing) return false;

        _isDrawing = false;

        // Convert screen points to normalized image coordinates
        var normalizedStart = ScreenToNormalized(_startPoint);
        var normalizedEnd = ScreenToNormalized(_currentPoint);

        // Create shape data
        var shapeData = new ShapeData
        {
            ShapeType = ShapeType,
            FillMode = FillMode,
            StrokeColor = StrokeColor,
            FillColor = FillColor,
            StrokeWidth = StrokeWidth / GetCurrentScale(),
            ArrowHeadSize = ArrowHeadSize,
            NormalizedStart = normalizedStart,
            NormalizedEnd = normalizedEnd
        };

        // Store as last shape for potential editing
        _lastShape = shapeData;

        // Emit completion event
        var args = new ShapeCompletedEventArgs(shapeData);
        ShapeCompleted?.Invoke(this, args);

        ShapeChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Clears the last shape reference.
    /// </summary>
    public void ClearLastShape()
    {
        _lastShape = null;
    }

    /// <summary>
    /// Renders the current shape preview on the canvas.
    /// </summary>
    public void Render(SKCanvas canvas)
    {
        if (!_isActive || !_isDrawing) return;

        RenderShape(canvas, _startPoint, _currentPoint, ShapeType, FillMode, StrokeColor, FillColor, StrokeWidth, ArrowHeadSize);
    }

    /// <summary>
    /// Renders a brush cursor preview at the specified position.
    /// </summary>
    public void RenderCursor(SKCanvas canvas, SKPoint position)
    {
        if (!_isActive) return;

        using var paint = new SKPaint
        {
            Color = StrokeColor.WithAlpha(128),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };

        // Draw a small crosshair cursor
        const float cursorSize = 10f;
        canvas.DrawLine(position.X - cursorSize, position.Y, position.X + cursorSize, position.Y, paint);
        canvas.DrawLine(position.X, position.Y - cursorSize, position.X, position.Y + cursorSize, paint);
    }

    /// <summary>
    /// Renders a shape with the specified parameters.
    /// </summary>
    internal static void RenderShape(
        SKCanvas canvas,
        SKPoint start,
        SKPoint end,
        ShapeType shapeType,
        ShapeFillMode fillMode,
        SKColor strokeColor,
        SKColor fillColor,
        float strokeWidth,
        float arrowHeadSize)
    {
        var rect = CreateRect(start, end);

        using var strokePaint = new SKPaint
        {
            Color = strokeColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        using var fillPaint = new SKPaint
        {
            Color = fillColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        switch (shapeType)
        {
            case ShapeType.Rectangle:
                if (fillMode is ShapeFillMode.Fill or ShapeFillMode.FillAndStroke)
                {
                    canvas.DrawRect(rect, fillPaint);
                }
                if (fillMode is ShapeFillMode.Stroke or ShapeFillMode.FillAndStroke)
                {
                    canvas.DrawRect(rect, strokePaint);
                }
                break;

            case ShapeType.Ellipse:
                if (fillMode is ShapeFillMode.Fill or ShapeFillMode.FillAndStroke)
                {
                    canvas.DrawOval(rect, fillPaint);
                }
                if (fillMode is ShapeFillMode.Stroke or ShapeFillMode.FillAndStroke)
                {
                    canvas.DrawOval(rect, strokePaint);
                }
                break;

            case ShapeType.Line:
                canvas.DrawLine(start, end, strokePaint);
                break;

            case ShapeType.Arrow:
                DrawArrow(canvas, start, end, strokePaint, arrowHeadSize * strokeWidth);
                break;
        }
    }

    private static SKRect CreateRect(SKPoint start, SKPoint end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var right = Math.Max(start.X, end.X);
        var bottom = Math.Max(start.Y, end.Y);
        return new SKRect(left, top, right, bottom);
    }

    private static void DrawArrow(SKCanvas canvas, SKPoint start, SKPoint end, SKPaint paint, float headSize)
    {
        // Draw the line
        canvas.DrawLine(start, end, paint);

        // Calculate arrow head
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);

        if (length < 1) return;

        // Normalize direction
        var nx = dx / length;
        var ny = dy / length;

        // Perpendicular direction
        var px = -ny;
        var py = nx;

        // Arrow head points
        var arrowPoint1 = new SKPoint(
            end.X - headSize * nx + headSize * 0.5f * px,
            end.Y - headSize * ny + headSize * 0.5f * py);
        var arrowPoint2 = new SKPoint(
            end.X - headSize * nx - headSize * 0.5f * px,
            end.Y - headSize * ny - headSize * 0.5f * py);

        // Draw arrow head as filled triangle
        using var path = new SKPath();
        path.MoveTo(end);
        path.LineTo(arrowPoint1);
        path.LineTo(arrowPoint2);
        path.Close();

        using var fillPaint = new SKPaint
        {
            Color = paint.Color,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawPath(path, fillPaint);
    }

    private SKPoint ScreenToNormalized(SKPoint screenPoint)
    {
        if (_imageRect.Width <= 0 || _imageRect.Height <= 0)
            return screenPoint;

        var x = (screenPoint.X - _imageRect.Left) / _imageRect.Width;
        var y = (screenPoint.Y - _imageRect.Top) / _imageRect.Height;

        return new SKPoint(x, y);
    }

    private float GetCurrentScale()
    {
        return _imageRect.Width > 0 ? _imageRect.Width : 1f;
    }
}

/// <summary>
/// Data representing a completed shape.
/// </summary>
public record ShapeData
{
    /// <summary>The type of shape.</summary>
    public required ShapeType ShapeType { get; init; }

    /// <summary>The fill mode.</summary>
    public required ShapeFillMode FillMode { get; init; }

    /// <summary>The stroke color.</summary>
    public required SKColor StrokeColor { get; init; }

    /// <summary>The fill color.</summary>
    public required SKColor FillColor { get; init; }

    /// <summary>The stroke width in normalized coordinates.</summary>
    public required float StrokeWidth { get; init; }

    /// <summary>The arrow head size multiplier.</summary>
    public float ArrowHeadSize { get; init; } = 4f;

    /// <summary>The start point in normalized image coordinates (0-1).</summary>
    public required SKPoint NormalizedStart { get; init; }

    /// <summary>The end point in normalized image coordinates (0-1).</summary>
    public required SKPoint NormalizedEnd { get; init; }
}

/// <summary>
/// Event arguments for when a shape is completed.
/// </summary>
public class ShapeCompletedEventArgs : EventArgs
{
    /// <summary>
    /// The completed shape data.
    /// </summary>
    public ShapeData Shape { get; }

    public ShapeCompletedEventArgs(ShapeData shape)
    {
        Shape = shape;
    }
}
