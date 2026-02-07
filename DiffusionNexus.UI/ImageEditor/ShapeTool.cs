using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Defines the interaction phase of the shape tool.
/// </summary>
public enum ShapeToolPhase
{
    /// <summary>No shape is active — ready to draw a new one.</summary>
    Idle,
    /// <summary>User is dragging to create a new shape.</summary>
    Drawing,
    /// <summary>A shape has been placed and can be moved/resized/rotated.</summary>
    Placed,
    /// <summary>The placed shape is being moved.</summary>
    Moving,
    /// <summary>The placed shape is being resized via a corner handle.</summary>
    Resizing,
    /// <summary>The placed shape is being rotated via the rotation handle.</summary>
    Rotating
}

/// <summary>
/// Identifies which manipulation handle is being interacted with on a placed shape.
/// </summary>
public enum ShapeManipulationHandle
{
    /// <summary>No handle — click is outside the shape.</summary>
    None,
    /// <summary>Click is inside the shape body — drag to move.</summary>
    Body,
    /// <summary>Top-left corner resize handle.</summary>
    TopLeft,
    /// <summary>Top-right corner resize handle.</summary>
    TopRight,
    /// <summary>Bottom-left corner resize handle.</summary>
    BottomLeft,
    /// <summary>Bottom-right corner resize handle.</summary>
    BottomRight,
    /// <summary>Rotation handle above the shape.</summary>
    Rotate,
    /// <summary>Delete/trash handle next to the rotation handle.</summary>
    Delete
}

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
    Line,
    /// <summary>X/Cross shape.</summary>
    Cross
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
    private ShapeToolPhase _phase = ShapeToolPhase.Idle;
    private SKPoint _startPoint;
    private SKPoint _currentPoint;
    private SKRect _imageRect;
    private ShapeData? _placedShape;

    // Manipulation state
    private ShapeManipulationHandle _activeHandle;
    private SKPoint _manipulationAnchor;
    private SKPoint _placedScreenStart;
    private SKPoint _placedScreenEnd;
    private float _rotationStartAngle;
    private float _rotationBaseAngle;

    private const float HandleRadius = 6f;
    private const float RotateHandleOffset = 30f;
    private const float DeleteHandleOffset = 32f;
    private const float HandleHitRadius = 12f;

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
                CancelPlacedShape();
                _phase = ShapeToolPhase.Idle;
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
    /// Gets or sets whether to constrain proportions (Ctrl held).
    /// When true, rectangles become squares and ellipses become circles.
    /// </summary>
    public bool ConstrainProportions { get; set; }

    /// <summary>
    /// Gets whether the user is currently drawing a shape.
    /// </summary>
    public bool IsDrawing => _phase == ShapeToolPhase.Drawing;

    /// <summary>
    /// Gets the current interaction phase.
    /// </summary>
    public ShapeToolPhase Phase => _phase;

    /// <summary>
    /// Gets whether a shape is currently placed and can be manipulated.
    /// </summary>
    public bool HasPlacedShape => _phase is ShapeToolPhase.Placed or ShapeToolPhase.Moving
        or ShapeToolPhase.Resizing or ShapeToolPhase.Rotating;

    /// <summary>
    /// Gets the start point of the current shape.
    /// </summary>
    public SKPoint StartPoint => _startPoint;

    /// <summary>
    /// Gets the current point (end point) of the shape being drawn.
    /// </summary>
    public SKPoint CurrentPoint => _currentPoint;

    /// <summary>
    /// Gets the placed shape data.
    /// </summary>
    public ShapeData? PlacedShape => _placedShape;

    /// <summary>
    /// Event raised when a shape is committed and should be applied to the image.
    /// </summary>
    public event EventHandler<ShapeCompletedEventArgs>? ShapeCompleted;

    /// <summary>
    /// Event raised when the shape preview changes and a redraw is needed.
    /// </summary>
    public event EventHandler? ShapeChanged;

    /// <summary>
    /// Event raised when the placed-shape state changes (placed or cleared).
    /// </summary>
    public event EventHandler? PlacedShapeStateChanged;

    /// <summary>
    /// Sets the current image bounds for coordinate mapping.
    /// </summary>
    public void SetImageBounds(SKRect imageRect)
    {
        _imageRect = imageRect;
    }

    /// <summary>
    /// Handles pointer press event.
    /// </summary>
    /// <returns>True if the event was handled.</returns>
    public bool OnPointerPressed(SKPoint screenPoint)
    {
        if (!_isActive) return false;
        if (_imageRect.Width <= 0 || _imageRect.Height <= 0) return false;

        switch (_phase)
        {
            case ShapeToolPhase.Placed:
                return HandlePlacedPointerPressed(screenPoint);

            case ShapeToolPhase.Idle:
                if (!_imageRect.Contains(screenPoint)) return false;
                _phase = ShapeToolPhase.Drawing;
                _startPoint = screenPoint;
                _currentPoint = screenPoint;
                ShapeChanged?.Invoke(this, EventArgs.Empty);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Handles pointer move event.
    /// </summary>
    /// <returns>True if the event was handled.</returns>
    public bool OnPointerMoved(SKPoint screenPoint)
    {
        if (!_isActive) return false;

        switch (_phase)
        {
            case ShapeToolPhase.Drawing:
                _currentPoint = screenPoint;
                ShapeChanged?.Invoke(this, EventArgs.Empty);
                return true;

            case ShapeToolPhase.Moving:
                HandleMoving(screenPoint);
                return true;

            case ShapeToolPhase.Resizing:
                HandleResizing(screenPoint);
                return true;

            case ShapeToolPhase.Rotating:
                HandleRotating(screenPoint);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Handles pointer release event.
    /// </summary>
    /// <returns>True if the event was handled.</returns>
    public bool OnPointerReleased()
    {
        if (!_isActive) return false;

        switch (_phase)
        {
            case ShapeToolPhase.Drawing:
                return FinishDrawingToPlaced();

            case ShapeToolPhase.Moving:
            case ShapeToolPhase.Resizing:
            case ShapeToolPhase.Rotating:
                _phase = ShapeToolPhase.Placed;
                ShapeChanged?.Invoke(this, EventArgs.Empty);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Commits the currently placed shape, firing the ShapeCompleted event.
    /// </summary>
    /// <returns>True if a shape was committed.</returns>
    public bool CommitPlacedShape()
    {
        if (_placedShape is null || !HasPlacedShape)
            return false;

        var args = new ShapeCompletedEventArgs(_placedShape);
        ShapeCompleted?.Invoke(this, args);

        _placedShape = null;
        _phase = ShapeToolPhase.Idle;
        ShapeChanged?.Invoke(this, EventArgs.Empty);
        PlacedShapeStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Cancels the currently placed shape without applying it.
    /// </summary>
    public void CancelPlacedShape()
    {
        if (_placedShape is null && _phase == ShapeToolPhase.Idle) return;

        _placedShape = null;
        _phase = ShapeToolPhase.Idle;
        ShapeChanged?.Invoke(this, EventArgs.Empty);
        PlacedShapeStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Determines which manipulation handle is under the given screen point.
    /// </summary>
    public ShapeManipulationHandle HitTestHandle(SKPoint screenPoint)
    {
        if (_placedShape is null || !HasPlacedShape) return ShapeManipulationHandle.None;

        var start = NormalizedToScreen(_placedShape.NormalizedStart);
        var end = NormalizedToScreen(_placedShape.NormalizedEnd);
        var center = new SKPoint((start.X + end.X) / 2f, (start.Y + end.Y) / 2f);
        var rotation = _placedShape.RotationDegrees;

        // Transform the test point into the shape's local (unrotated) coordinate system
        var local = RotatePointAround(screenPoint, center, -rotation);

        var rect = CreateRect(start, end);

        // Corner handles
        if (DistanceSq(local, new SKPoint(rect.Left, rect.Top)) < HandleHitRadius * HandleHitRadius)
            return ShapeManipulationHandle.TopLeft;
        if (DistanceSq(local, new SKPoint(rect.Right, rect.Top)) < HandleHitRadius * HandleHitRadius)
            return ShapeManipulationHandle.TopRight;
        if (DistanceSq(local, new SKPoint(rect.Left, rect.Bottom)) < HandleHitRadius * HandleHitRadius)
            return ShapeManipulationHandle.BottomLeft;
        if (DistanceSq(local, new SKPoint(rect.Right, rect.Bottom)) < HandleHitRadius * HandleHitRadius)
            return ShapeManipulationHandle.BottomRight;

        // Rotation handle
        var rotateHandlePos = new SKPoint(center.X, rect.Top - RotateHandleOffset);
        if (DistanceSq(local, rotateHandlePos) < (HandleHitRadius + 6f) * (HandleHitRadius + 6f))
            return ShapeManipulationHandle.Rotate;

        // Delete handle (to the right of the rotation handle)
        var deleteHandlePos = new SKPoint(center.X + DeleteHandleOffset, rect.Top - RotateHandleOffset);
        if (DistanceSq(local, deleteHandlePos) < (HandleHitRadius + 6f) * (HandleHitRadius + 6f))
            return ShapeManipulationHandle.Delete;

        // Body (inside the shape rect with some padding)
        var expandedRect = SKRect.Create(rect.Left - 4, rect.Top - 4, rect.Width + 8, rect.Height + 8);
        if (expandedRect.Contains(local))
            return ShapeManipulationHandle.Body;

        return ShapeManipulationHandle.None;
    }

    private bool FinishDrawingToPlaced()
    {
        // Apply constraints if active
        var endPoint = ConstrainProportions
            ? GetConstrainedEndPoint(_startPoint, _currentPoint, ShapeType)
            : _currentPoint;

        // Ignore very small drags (< 3px)
        if (Math.Abs(endPoint.X - _startPoint.X) < 3 && Math.Abs(endPoint.Y - _startPoint.Y) < 3)
        {
            _phase = ShapeToolPhase.Idle;
            ShapeChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        // Convert screen points to normalized image coordinates
        var normalizedStart = ScreenToNormalized(_startPoint);
        var normalizedEnd = ScreenToNormalized(endPoint);

        _placedShape = new ShapeData
        {
            ShapeType = ShapeType,
            FillMode = FillMode,
            StrokeColor = StrokeColor,
            FillColor = FillColor,
            StrokeWidth = StrokeWidth / GetCurrentScale(),
            ArrowHeadSize = ArrowHeadSize,
            NormalizedStart = normalizedStart,
            NormalizedEnd = normalizedEnd,
            RotationDegrees = 0f
        };

        _phase = ShapeToolPhase.Placed;
        ShapeChanged?.Invoke(this, EventArgs.Empty);
        PlacedShapeStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool HandlePlacedPointerPressed(SKPoint screenPoint)
    {
        var handle = HitTestHandle(screenPoint);

        if (handle == ShapeManipulationHandle.None)
        {
            // Click outside — commit current shape and start a new one if inside image
            CommitPlacedShape();
            if (_imageRect.Contains(screenPoint))
            {
                _phase = ShapeToolPhase.Drawing;
                _startPoint = screenPoint;
                _currentPoint = screenPoint;
                ShapeChanged?.Invoke(this, EventArgs.Empty);
            }
            return true;
        }

        if (handle == ShapeManipulationHandle.Delete)
        {
            CancelPlacedShape();
            return true;
        }

        _manipulationAnchor = screenPoint;
        _placedScreenStart = NormalizedToScreen(_placedShape!.NormalizedStart);
        _placedScreenEnd = NormalizedToScreen(_placedShape.NormalizedEnd);
        _activeHandle = handle;

        switch (handle)
        {
            case ShapeManipulationHandle.Body:
                _phase = ShapeToolPhase.Moving;
                break;

            case ShapeManipulationHandle.Rotate:
                _phase = ShapeToolPhase.Rotating;
                var center = new SKPoint(
                    (_placedScreenStart.X + _placedScreenEnd.X) / 2f,
                    (_placedScreenStart.Y + _placedScreenEnd.Y) / 2f);
                _rotationStartAngle = MathF.Atan2(
                    screenPoint.Y - center.Y, screenPoint.X - center.X);
                _rotationBaseAngle = _placedShape.RotationDegrees;
                break;

            default: // Corner resize handles
                _phase = ShapeToolPhase.Resizing;
                break;
        }

        ShapeChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void HandleMoving(SKPoint screenPoint)
    {
        if (_placedShape is null) return;

        var dx = screenPoint.X - _manipulationAnchor.X;
        var dy = screenPoint.Y - _manipulationAnchor.Y;

        var newStart = new SKPoint(_placedScreenStart.X + dx, _placedScreenStart.Y + dy);
        var newEnd = new SKPoint(_placedScreenEnd.X + dx, _placedScreenEnd.Y + dy);

        _placedShape.NormalizedStart = ScreenToNormalized(newStart);
        _placedShape.NormalizedEnd = ScreenToNormalized(newEnd);

        ShapeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleResizing(SKPoint screenPoint)
    {
        if (_placedShape is null) return;

        var center = new SKPoint(
            (_placedScreenStart.X + _placedScreenEnd.X) / 2f,
            (_placedScreenStart.Y + _placedScreenEnd.Y) / 2f);

        // Work in local (unrotated) coordinates
        var localPoint = RotatePointAround(screenPoint, center, -_placedShape.RotationDegrees);
        var localStart = RotatePointAround(_placedScreenStart, center, -_placedShape.RotationDegrees);
        var localEnd = RotatePointAround(_placedScreenEnd, center, -_placedShape.RotationDegrees);

        var left = Math.Min(localStart.X, localEnd.X);
        var top = Math.Min(localStart.Y, localEnd.Y);
        var right = Math.Max(localStart.X, localEnd.X);
        var bottom = Math.Max(localStart.Y, localEnd.Y);

        switch (_activeHandle)
        {
            case ShapeManipulationHandle.TopLeft:
                left = localPoint.X;
                top = localPoint.Y;
                break;
            case ShapeManipulationHandle.TopRight:
                right = localPoint.X;
                top = localPoint.Y;
                break;
            case ShapeManipulationHandle.BottomLeft:
                left = localPoint.X;
                bottom = localPoint.Y;
                break;
            case ShapeManipulationHandle.BottomRight:
                right = localPoint.X;
                bottom = localPoint.Y;
                break;
        }

        if (ConstrainProportions)
        {
            var w = Math.Abs(right - left);
            var h = Math.Abs(bottom - top);
            var size = Math.Max(w, h);
            switch (_activeHandle)
            {
                case ShapeManipulationHandle.TopLeft:
                    left = right - size;
                    top = bottom - size;
                    break;
                case ShapeManipulationHandle.TopRight:
                    right = left + size;
                    top = bottom - size;
                    break;
                case ShapeManipulationHandle.BottomLeft:
                    left = right - size;
                    bottom = top + size;
                    break;
                case ShapeManipulationHandle.BottomRight:
                    right = left + size;
                    bottom = top + size;
                    break;
            }
        }

        // Rotate back to screen coordinates
        var newStart = RotatePointAround(new SKPoint(left, top), center, _placedShape.RotationDegrees);
        var newEnd = RotatePointAround(new SKPoint(right, bottom), center, _placedShape.RotationDegrees);

        _placedShape.NormalizedStart = ScreenToNormalized(newStart);
        _placedShape.NormalizedEnd = ScreenToNormalized(newEnd);

        ShapeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleRotating(SKPoint screenPoint)
    {
        if (_placedShape is null) return;

        var screenStart = NormalizedToScreen(_placedShape.NormalizedStart);
        var screenEnd = NormalizedToScreen(_placedShape.NormalizedEnd);
        var center = new SKPoint(
            (screenStart.X + screenEnd.X) / 2f,
            (screenStart.Y + screenEnd.Y) / 2f);

        var currentAngle = MathF.Atan2(
            screenPoint.Y - center.Y, screenPoint.X - center.X);
        var deltaAngle = (currentAngle - _rotationStartAngle) * (180f / MathF.PI);

        _placedShape.RotationDegrees = _rotationBaseAngle + deltaAngle;

        ShapeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Renders the current shape preview and/or placed shape with manipulation handles.
    /// </summary>
    public void Render(SKCanvas canvas)
    {
        if (!_isActive) return;

        // Render drawing preview
        if (_phase == ShapeToolPhase.Drawing)
        {
            var endPoint = ConstrainProportions
                ? GetConstrainedEndPoint(_startPoint, _currentPoint, ShapeType)
                : _currentPoint;

            RenderShape(canvas, _startPoint, endPoint, ShapeType, FillMode, StrokeColor, FillColor, StrokeWidth, ArrowHeadSize);
        }

        // Render placed shape with handles
        if (HasPlacedShape && _placedShape is not null)
        {
            var start = NormalizedToScreen(_placedShape.NormalizedStart);
            var end = NormalizedToScreen(_placedShape.NormalizedEnd);
            var center = new SKPoint((start.X + end.X) / 2f, (start.Y + end.Y) / 2f);
            var screenStrokeWidth = _placedShape.StrokeWidth * GetCurrentScale();

            canvas.Save();
            canvas.RotateDegrees(_placedShape.RotationDegrees, center.X, center.Y);

            RenderShape(canvas, start, end, _placedShape.ShapeType, _placedShape.FillMode,
                _placedShape.StrokeColor, _placedShape.FillColor, screenStrokeWidth, _placedShape.ArrowHeadSize);

            canvas.Restore();

            RenderManipulationHandles(canvas, start, end, _placedShape.RotationDegrees);
        }
    }

    /// <summary>
    /// Renders manipulation handles (corner dots and rotation handle) for the placed shape.
    /// </summary>
    private void RenderManipulationHandles(SKCanvas canvas, SKPoint start, SKPoint end, float rotation)
    {
        var center = new SKPoint((start.X + end.X) / 2f, (start.Y + end.Y) / 2f);

        canvas.Save();
        canvas.RotateDegrees(rotation, center.X, center.Y);

        var rect = CreateRect(start, end);

        // Bounding box dashed outline
        using var linePaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 180),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([6f, 4f], 0)
        };
        canvas.DrawRect(rect, linePaint);

        // Corner handles
        SKPoint[] corners =
        [
            new(rect.Left, rect.Top),
            new(rect.Right, rect.Top),
            new(rect.Right, rect.Bottom),
            new(rect.Left, rect.Bottom)
        ];

        using var handleFill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var handleStroke = new SKPaint { Color = new SKColor(0, 0, 0, 180), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };

        foreach (var corner in corners)
        {
            canvas.DrawCircle(corner, HandleRadius, handleFill);
            canvas.DrawCircle(corner, HandleRadius, handleStroke);
        }

        // Rotation handle stem
        var topCenter = new SKPoint(center.X, rect.Top);
        var rotateCenter = new SKPoint(center.X, rect.Top - RotateHandleOffset);

        using var stemPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 140),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };
        canvas.DrawLine(topCenter, rotateCenter, stemPaint);

        // Rotation handle circle with arrow icon
        using var rotateBg = new SKPaint { Color = new SKColor(60, 60, 60, 220), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(rotateCenter, 12f, rotateBg);
        canvas.DrawCircle(rotateCenter, 12f, handleStroke);

        using var arrowPaint = new SKPaint
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round
        };
        var arcRect = SKRect.Create(rotateCenter.X - 6, rotateCenter.Y - 6, 12, 12);
        using var arcPath = new SKPath();
        arcPath.AddArc(arcRect, -90, 270);
        canvas.DrawPath(arcPath, arrowPaint);

        // Delete/trash handle (to the right of the rotate handle)
        var deleteCenter = new SKPoint(center.X + DeleteHandleOffset, rect.Top - RotateHandleOffset);
        canvas.DrawLine(rotateCenter, deleteCenter, stemPaint);

        using var deleteBg = new SKPaint { Color = new SKColor(140, 30, 30, 220), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(deleteCenter, 12f, deleteBg);
        canvas.DrawCircle(deleteCenter, 12f, handleStroke);

        // Draw X icon inside the delete circle
        using var xPaint = new SKPaint
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round
        };
        const float xSize = 4.5f;
        canvas.DrawLine(deleteCenter.X - xSize, deleteCenter.Y - xSize,
                        deleteCenter.X + xSize, deleteCenter.Y + xSize, xPaint);
        canvas.DrawLine(deleteCenter.X + xSize, deleteCenter.Y - xSize,
                        deleteCenter.X - xSize, deleteCenter.Y + xSize, xPaint);

        canvas.Restore();
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
                DrawArrow(canvas, start, end, strokePaint, strokeWidth);
                break;

            case ShapeType.Cross:
                DrawCross(canvas, rect, strokePaint);
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

    private static void DrawArrow(SKCanvas canvas, SKPoint start, SKPoint end, SKPaint paint, float strokeWidth)
    {
        // Calculate direction
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

        // Arrow head dimensions - proportional to stroke width but with reasonable limits
        var headLength = Math.Max(strokeWidth * 3f, 12f);  // Length of the arrowhead along the shaft
        var headWidth = Math.Max(strokeWidth * 2f, 8f);    // Half-width of the arrowhead base

        // Ensure head doesn't exceed line length
        headLength = Math.Min(headLength, length * 0.4f);
        headWidth = Math.Min(headWidth, headLength * 0.7f);

        // Calculate the base of the arrow head (where the line should stop)
        var basePoint = new SKPoint(
            end.X - headLength * nx,
            end.Y - headLength * ny);

        // Draw the line (stopping at the base of the arrowhead)
        canvas.DrawLine(start, basePoint, paint);

        // Arrow head points
        var arrowPoint1 = new SKPoint(
            basePoint.X + headWidth * px,
            basePoint.Y + headWidth * py);
        var arrowPoint2 = new SKPoint(
            basePoint.X - headWidth * px,
            basePoint.Y - headWidth * py);

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

    private static void DrawCross(SKCanvas canvas, SKRect rect, SKPaint paint)
    {
        // Draw an X shape with two diagonal lines
        // Line from top-left to bottom-right
        canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, paint);
        // Line from top-right to bottom-left
        canvas.DrawLine(rect.Right, rect.Top, rect.Left, rect.Bottom, paint);
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

    private SKPoint NormalizedToScreen(SKPoint normalized)
    {
        if (_imageRect.Width <= 0 || _imageRect.Height <= 0)
            return normalized;

        var x = _imageRect.Left + normalized.X * _imageRect.Width;
        var y = _imageRect.Top + normalized.Y * _imageRect.Height;
        return new SKPoint(x, y);
    }

    private static SKPoint RotatePointAround(SKPoint point, SKPoint center, float degrees)
    {
        var rad = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        return new SKPoint(
            center.X + dx * cos - dy * sin,
            center.Y + dx * sin + dy * cos);
    }

    private static float DistanceSq(SKPoint a, SKPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// Calculates a constrained end point to create squares from rectangles,
    /// circles from ellipses, and equal-sided crosses.
    /// </summary>
    private static SKPoint GetConstrainedEndPoint(SKPoint start, SKPoint current, ShapeType shapeType)
    {
        // Only constrain for Rectangle, Ellipse, and Cross shapes
        if (shapeType != ShapeType.Rectangle && shapeType != ShapeType.Ellipse && shapeType != ShapeType.Cross)
            return current;

        var dx = current.X - start.X;
        var dy = current.Y - start.Y;

        // Use the larger dimension to create a square/circle
        var size = Math.Max(Math.Abs(dx), Math.Abs(dy));

        // Preserve the direction (sign) of the original delta
        var constrainedX = start.X + (dx >= 0 ? size : -size);
        var constrainedY = start.Y + (dy >= 0 ? size : -size);

        return new SKPoint(constrainedX, constrainedY);
    }
}

/// <summary>
/// Data representing a completed shape that can be manipulated before committing.
/// </summary>
public class ShapeData
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
    public SKPoint NormalizedStart { get; set; }

    /// <summary>The end point in normalized image coordinates (0-1).</summary>
    public SKPoint NormalizedEnd { get; set; }

    /// <summary>The rotation angle in degrees around the center of the shape.</summary>
    public float RotationDegrees { get; set; }

    /// <summary>Gets the center point of the shape in normalized coordinates.</summary>
    public SKPoint NormalizedCenter => new(
        (NormalizedStart.X + NormalizedEnd.X) / 2f,
        (NormalizedStart.Y + NormalizedEnd.Y) / 2f);
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
