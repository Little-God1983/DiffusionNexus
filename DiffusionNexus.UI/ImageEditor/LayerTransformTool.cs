using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>Which part of the transform box the pointer is on.</summary>
public enum TransformHandle
{
    None, Body, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Rotate
}

/// <summary>
/// Move / Transform tool for the active layer. Holds a translation, scale (sign = flip) and
/// rotation about the centre of the layer's bounds at arm time and exposes them as one
/// canvas-space <see cref="Matrix"/>. Never touches pixels: the compositor previews the matrix,
/// <c>ImageEditorCore.ApplyLayerTransform</c> rasterizes it on <see cref="Commit"/>.
/// Handle vocabulary and geometry follow <see cref="ShapeTool"/>.
/// </summary>
public sealed class LayerTransformTool
{
    private enum Phase { Idle, Moving, Scaling, Rotating }

    public const float HandleRadius = 6f;
    public const float HandleHitRadius = 12f;
    public const float RotateHandleOffset = 30f;
    public const float RotationSnapDegrees = 15f;

    private bool _isActive;
    private Layer? _layer;
    private SKRectI _sourceBounds;
    private SKRect _imageRect;
    private Phase _phase = Phase.Idle;
    private TransformHandle _activeHandle;

    private SKPoint _translation;
    private float _scaleX = 1f;
    private float _scaleY = 1f;
    private float _rotation;

    // Drag bookkeeping (canvas px unless noted)
    private SKPoint _dragStartCanvas;
    private SKPoint _translationAtPress;
    private float _scaleXAtPress, _scaleYAtPress;
    private float _rotationAtPress;
    private float _pressAngleDegrees;
    private bool _aspectForThisDrag;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            if (!value)
            {
                Commit();           // a deliberate move is never lost silently (Shape/Text precedent)
                Disarm();
            }
        }
    }

    public bool IsArmed => _layer is not null;
    public Layer? Layer => _layer;
    public SKRectI SourceBounds => _sourceBounds;
    public int ImagePixelWidth { get; set; }
    public int ImagePixelHeight { get; set; }
    /// <summary>Screen px per canvas px.</summary>
    public float Scale => ImagePixelWidth > 0 && _imageRect.Width > 0 ? _imageRect.Width / ImagePixelWidth : 1f;

    public SKPoint Translation => _translation;
    public float ScaleX => _scaleX;
    public float ScaleY => _scaleY;
    public float RotationDegrees => _rotation;
    public bool KeepAspect { get; set; } = true;
    /// <summary>Ctrl state from the control: inverts <see cref="KeepAspect"/> for a corner drag.</summary>
    public bool ConstrainProportionsOverride { get; set; }
    /// <summary>Shift state from the control: snap rotation to 15°.</summary>
    public bool SnapRotation { get; set; }
    public bool IsDragging => _phase != Phase.Idle;

    public event EventHandler? TransformChanged;
    public event EventHandler? CommitRequested;
    public event EventHandler? ArmedLayerChanged;

    private SKPoint Pivot => new(_sourceBounds.MidX, _sourceBounds.MidY);

    /// <summary>Canvas-space transform of the source bounds.</summary>
    public SKMatrix Matrix
    {
        get
        {
            var c = Pivot;
            var m = SKMatrix.CreateTranslation(-c.X, -c.Y);
            m = m.PostConcat(SKMatrix.CreateScale(_scaleX, _scaleY));
            m = m.PostConcat(SKMatrix.CreateRotationDegrees(_rotation));
            m = m.PostConcat(SKMatrix.CreateTranslation(c.X + _translation.X, c.Y + _translation.Y));
            return m;
        }
    }

    public SKRect TransformedBounds => Matrix.MapRect(SKRect.Create(_sourceBounds.Left, _sourceBounds.Top, _sourceBounds.Width, _sourceBounds.Height));

    /// <summary>Axis-aligned box before rotation (what the handles sit on), canvas px.</summary>
    private SKRect UnrotatedBox
    {
        get
        {
            var c = Pivot;
            var w = _sourceBounds.Width * MathF.Abs(_scaleX);
            var h = _sourceBounds.Height * MathF.Abs(_scaleY);
            var cx = c.X + _translation.X;
            var cy = c.Y + _translation.Y;
            return new SKRect(cx - w / 2f, cy - h / 2f, cx + w / 2f, cy + h / 2f);
        }
    }

    public bool HasTransform =>
        MathF.Abs(_translation.X) > 0.5f || MathF.Abs(_translation.Y) > 0.5f ||
        MathF.Abs(_scaleX - 1f) > 1e-3f || MathF.Abs(_scaleY - 1f) > 1e-3f ||
        MathF.Abs(_rotation) > 1e-3f;

    public void Arm(Layer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        _layer = layer;
        _sourceBounds = layer.Bounds;
        ResetState();
        ArmedLayerChanged?.Invoke(this, EventArgs.Empty);
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Disarm()
    {
        if (_layer is null) return;
        _layer = null;
        ResetState();
        ArmedLayerChanged?.Invoke(this, EventArgs.Empty);
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetImageBounds(SKRect imageRect) => _imageRect = imageRect;

    /// <summary>Back to identity; the layer stays armed.</summary>
    public void Reset()
    {
        ResetState();
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetState()
    {
        _translation = SKPoint.Empty;
        _scaleX = _scaleY = 1f;
        _rotation = 0f;
        _phase = Phase.Idle;
        _activeHandle = TransformHandle.None;
    }

    /// <summary>Raises <see cref="CommitRequested"/> when there is something to apply.</summary>
    public bool Commit()
    {
        if (_layer is null || !HasTransform) return false;
        CommitRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    #region Coordinate mapping

    private SKPoint ScreenToCanvas(SKPoint p) => new((p.X - _imageRect.Left) / Scale, (p.Y - _imageRect.Top) / Scale);
    private SKPoint CanvasToScreen(SKPoint p) => new(_imageRect.Left + p.X * Scale, _imageRect.Top + p.Y * Scale);

    private static SKPoint RotatePointAround(SKPoint point, SKPoint center, float degrees)
    {
        var rad = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        return new SKPoint(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }

    private static float DistanceSq(SKPoint a, SKPoint b) { var dx = a.X - b.X; var dy = a.Y - b.Y; return dx * dx + dy * dy; }

    /// <summary>Screen position of a handle on the rotated box.</summary>
    private SKPoint HandleScreenPoint(TransformHandle handle)
    {
        var box = UnrotatedBox;
        var centre = new SKPoint(box.MidX, box.MidY);
        var local = handle switch
        {
            TransformHandle.TopLeft => new SKPoint(box.Left, box.Top),
            TransformHandle.Top => new SKPoint(box.MidX, box.Top),
            TransformHandle.TopRight => new SKPoint(box.Right, box.Top),
            TransformHandle.Right => new SKPoint(box.Right, box.MidY),
            TransformHandle.BottomRight => new SKPoint(box.Right, box.Bottom),
            TransformHandle.Bottom => new SKPoint(box.MidX, box.Bottom),
            TransformHandle.BottomLeft => new SKPoint(box.Left, box.Bottom),
            TransformHandle.Left => new SKPoint(box.Left, box.MidY),
            TransformHandle.Rotate => new SKPoint(box.MidX, box.Top - RotateHandleOffset / Scale),
            _ => centre
        };
        return CanvasToScreen(RotatePointAround(local, centre, _rotation));
    }

    #endregion

    #region Hit testing

    private static readonly TransformHandle[] HitOrder =
    [
        TransformHandle.TopLeft, TransformHandle.TopRight, TransformHandle.BottomLeft, TransformHandle.BottomRight,
        TransformHandle.Top, TransformHandle.Right, TransformHandle.Bottom, TransformHandle.Left,
        TransformHandle.Rotate
    ];

    public TransformHandle HitTest(SKPoint screenPoint)
    {
        if (_layer is null) return TransformHandle.None;

        // Nearest match wins (not first-in-order): in a small box a corner and an adjacent
        // edge/centre point can both fall inside a uniform radius, and the closer one is the
        // one the user meant. Straight edges get the tighter HandleRadius tolerance so a click
        // near the box centre (equidistant from two edge midpoints) falls through to Body
        // instead of snapping to whichever edge happens to be checked first.
        var best = TransformHandle.None;
        var bestDistSq = float.MaxValue;
        foreach (var h in HitOrder)
        {
            var r = h switch
            {
                TransformHandle.Rotate => HandleHitRadius + 6f,
                TransformHandle.Top or TransformHandle.Right or TransformHandle.Bottom or TransformHandle.Left => HandleRadius,
                _ => HandleHitRadius
            };
            var distSq = DistanceSq(screenPoint, HandleScreenPoint(h));
            if (distSq <= r * r && distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = h;
            }
        }
        if (best != TransformHandle.None) return best;

        // Body: rotate the pointer into the box's local space (canvas px) and test the box.
        var box = UnrotatedBox;
        var centre = new SKPoint(box.MidX, box.MidY);
        var local = RotatePointAround(ScreenToCanvas(screenPoint), centre, -_rotation);
        return box.Contains(local) ? TransformHandle.Body : TransformHandle.None;
    }

    public TransformHandle GetCursorForPoint(SKPoint screenPoint) => HitTest(screenPoint);

    #endregion

    #region Gestures

    public bool OnPointerPressed(SKPoint screenPoint)
    {
        if (!_isActive || _layer is null) return false;

        var handle = HitTest(screenPoint);
        if (handle == TransformHandle.None) return false;

        _activeHandle = handle;
        _dragStartCanvas = ScreenToCanvas(screenPoint);
        _translationAtPress = _translation;
        _scaleXAtPress = _scaleX;
        _scaleYAtPress = _scaleY;
        _rotationAtPress = _rotation;
        _aspectForThisDrag = KeepAspect ^ ConstrainProportionsOverride;

        var box = UnrotatedBox;
        var centre = new SKPoint(box.MidX, box.MidY);
        _pressAngleDegrees = MathF.Atan2(_dragStartCanvas.Y - centre.Y, _dragStartCanvas.X - centre.X) * 180f / MathF.PI;

        _phase = handle switch
        {
            TransformHandle.Body => Phase.Moving,
            TransformHandle.Rotate => Phase.Rotating,
            _ => Phase.Scaling
        };
        return true;
    }

    public bool OnPointerMoved(SKPoint screenPoint)
    {
        if (!_isActive || _layer is null || _phase == Phase.Idle) return false;

        var canvasPoint = ScreenToCanvas(screenPoint);
        switch (_phase)
        {
            case Phase.Moving:
                _translation = new SKPoint(
                    _translationAtPress.X + (canvasPoint.X - _dragStartCanvas.X),
                    _translationAtPress.Y + (canvasPoint.Y - _dragStartCanvas.Y));
                break;
            case Phase.Scaling:
                ApplyScaleDrag(canvasPoint);
                break;
            case Phase.Rotating:
                ApplyRotateDrag(canvasPoint);
                break;
        }

        TransformChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool OnPointerReleased()
    {
        if (_phase == Phase.Idle) return false;
        _phase = Phase.Idle;
        _activeHandle = TransformHandle.None;
        TransformChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void ApplyScaleDrag(SKPoint canvasPoint)
    {
        // Work in the box's local (unrotated) space around the centre at press time.
        var c = Pivot;
        var centreAtPress = new SKPoint(c.X + _translationAtPress.X, c.Y + _translationAtPress.Y);
        var localStart = RotatePointAround(_dragStartCanvas, centreAtPress, -_rotationAtPress);
        var localNow = RotatePointAround(canvasPoint, centreAtPress, -_rotationAtPress);

        var w0 = _sourceBounds.Width * MathF.Abs(_scaleXAtPress);
        var h0 = _sourceBounds.Height * MathF.Abs(_scaleYAtPress);
        var box = new SKRect(centreAtPress.X - w0 / 2f, centreAtPress.Y - h0 / 2f, centreAtPress.X + w0 / 2f, centreAtPress.Y + h0 / 2f);

        // Anchor = the opposite corner/edge; the dragged side follows the pointer.
        var left = box.Left; var top = box.Top; var right = box.Right; var bottom = box.Bottom;
        var dx = localNow.X - localStart.X;
        var dy = localNow.Y - localStart.Y;
        var movesLeft = _activeHandle is TransformHandle.TopLeft or TransformHandle.Left or TransformHandle.BottomLeft;
        var movesRight = _activeHandle is TransformHandle.TopRight or TransformHandle.Right or TransformHandle.BottomRight;
        var movesTop = _activeHandle is TransformHandle.TopLeft or TransformHandle.Top or TransformHandle.TopRight;
        var movesBottom = _activeHandle is TransformHandle.BottomLeft or TransformHandle.Bottom or TransformHandle.BottomRight;
        if (movesLeft) left += dx;
        if (movesRight) right += dx;
        if (movesTop) top += dy;
        if (movesBottom) bottom += dy;

        var newW = right - left;
        var newH = bottom - top;
        var fx = w0 > 0 ? newW / w0 : 1f;
        var fy = h0 > 0 ? newH / h0 : 1f;

        var isCorner = _activeHandle is TransformHandle.TopLeft or TransformHandle.TopRight or TransformHandle.BottomLeft or TransformHandle.BottomRight;
        if (isCorner && _aspectForThisDrag)
        {
            var f = MathF.Abs(fx) >= MathF.Abs(fy) ? fx : fy;
            fx = f; fy = f;
            newW = w0 * fx; newH = h0 * fy;
            // Re-anchor the box on the opposite corner.
            if (movesLeft) left = right - newW; else right = left + newW;
            if (movesTop) top = bottom - newH; else bottom = top + newH;
        }

        const float minFactor = 0.01f;
        if (MathF.Abs(fx) < minFactor || MathF.Abs(fy) < minFactor) return;

        _scaleX = MathF.Sign(_scaleXAtPress) * MathF.Abs(_scaleXAtPress) * fx;
        _scaleY = MathF.Sign(_scaleYAtPress) * MathF.Abs(_scaleYAtPress) * fy;

        // New centre in local space, rotated back into canvas space => translation.
        var newLocalCentre = new SKPoint((left + right) / 2f, (top + bottom) / 2f);
        var newCentre = RotatePointAround(newLocalCentre, centreAtPress, _rotationAtPress);
        _translation = new SKPoint(newCentre.X - c.X, newCentre.Y - c.Y);
    }

    private void ApplyRotateDrag(SKPoint canvasPoint)
    {
        var box = UnrotatedBox;
        var centre = new SKPoint(box.MidX, box.MidY);
        var angle = MathF.Atan2(canvasPoint.Y - centre.Y, canvasPoint.X - centre.X) * 180f / MathF.PI;
        var rotation = _rotationAtPress + (angle - _pressAngleDegrees);
        if (SnapRotation) rotation = MathF.Round(rotation / RotationSnapDegrees) * RotationSnapDegrees;
        _rotation = NormalizeDegrees(rotation);
    }

    private static float NormalizeDegrees(float d)
    {
        d %= 360f;
        if (d > 180f) d -= 360f;
        if (d <= -180f) d += 360f;
        return d;
    }

    #endregion

    #region Panel setters

    /// <summary>Top-left of <see cref="TransformedBounds"/> in canvas px.</summary>
    public void SetPosition(float x, float y)
    {
        var b = TransformedBounds;
        _translation = new SKPoint(_translation.X + (x - b.Left), _translation.Y + (y - b.Top));
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Size of the unrotated box in canvas px, keeping its top-left where it is. With
    /// <see cref="KeepAspect"/> the width wins and the height follows the source aspect.
    /// </summary>
    public void SetSize(float w, float h)
    {
        if (w < 1f || h < 1f || _sourceBounds.Width == 0 || _sourceBounds.Height == 0) return;
        var before = TransformedBounds;
        var fx = w / _sourceBounds.Width;
        var fy = KeepAspect ? fx : h / _sourceBounds.Height;
        _scaleX = MathF.Sign(_scaleX) * fx;
        _scaleY = MathF.Sign(_scaleY) * fy;
        var after = TransformedBounds;
        _translation = new SKPoint(_translation.X + (before.Left - after.Left), _translation.Y + (before.Top - after.Top));
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetRotation(float degrees)
    {
        _rotation = NormalizeDegrees(degrees);
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    public void FlipHorizontal() { _scaleX = -_scaleX; TransformChanged?.Invoke(this, EventArgs.Empty); }
    public void FlipVertical() { _scaleY = -_scaleY; TransformChanged?.Invoke(this, EventArgs.Empty); }

    public void Nudge(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        _translation = new SKPoint(_translation.X + dx, _translation.Y + dy);
        TransformChanged?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region Rendering

    /// <summary>
    /// Off-canvas part of the preview at 50 % (the compositor draws the in-canvas part), the
    /// dashed rotated box, eight scale handles, the rotate handle with its stem, and a size /
    /// angle label under the box.
    /// </summary>
    public void Render(SKCanvas canvas, SKRect canvasBounds)
    {
        if (!_isActive || _layer?.Bitmap is null) return;

        var s = Scale;
        // canvas px -> screen px
        var toScreen = SKMatrix.CreateScale(s, s).PostConcat(SKMatrix.CreateTranslation(_imageRect.Left, _imageRect.Top));

        if (HasTransform)
        {
            canvas.Save();
            canvas.ClipRect(_imageRect, SKClipOperation.Difference);
            canvas.Concat(toScreen);
            canvas.Concat(Matrix);
            using var dim = new SKPaint { Color = SKColors.White.WithAlpha(128), IsAntialias = true };
            canvas.DrawBitmap(_layer.Bitmap, _layer.OffsetX, _layer.OffsetY, dim);
            canvas.Restore();
        }

        var box = UnrotatedBox;
        var centre = CanvasToScreen(new SKPoint(box.MidX, box.MidY));
        var screenBox = new SKRect(
            centre.X - box.Width * s / 2f, centre.Y - box.Height * s / 2f,
            centre.X + box.Width * s / 2f, centre.Y + box.Height * s / 2f);

        canvas.Save();
        canvas.RotateDegrees(_rotation, centre.X, centre.Y);

        using var dashEffect = SKPathEffect.CreateDash([6f, 4f], 0);
        using var linePaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 180), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f,
            IsAntialias = true, PathEffect = dashEffect
        };
        canvas.DrawRect(screenBox, linePaint);

        using var handleFill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var handleStroke = new SKPaint { Color = new SKColor(0, 0, 0, 180), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        SKPoint[] handles =
        [
            new(screenBox.Left, screenBox.Top), new(screenBox.MidX, screenBox.Top), new(screenBox.Right, screenBox.Top),
            new(screenBox.Right, screenBox.MidY), new(screenBox.Right, screenBox.Bottom), new(screenBox.MidX, screenBox.Bottom),
            new(screenBox.Left, screenBox.Bottom), new(screenBox.Left, screenBox.MidY)
        ];
        foreach (var h in handles)
        {
            canvas.DrawCircle(h, HandleRadius, handleFill);
            canvas.DrawCircle(h, HandleRadius, handleStroke);
        }

        var topCentre = new SKPoint(screenBox.MidX, screenBox.Top);
        var rotateCentre = new SKPoint(screenBox.MidX, screenBox.Top - RotateHandleOffset);
        using var stemPaint = new SKPaint { Color = new SKColor(255, 255, 255, 140), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
        canvas.DrawLine(topCentre, rotateCentre, stemPaint);
        using var rotateBg = new SKPaint { Color = new SKColor(60, 60, 60, 220), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(rotateCentre, 12f, rotateBg);
        canvas.DrawCircle(rotateCentre, 12f, handleStroke);
        using var arcPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        canvas.DrawArc(new SKRect(rotateCentre.X - 6f, rotateCentre.Y - 6f, rotateCentre.X + 6f, rotateCentre.Y + 6f), -220f, 260f, false, arcPaint);

        canvas.Restore();

        DrawLabel(canvas, screenBox, centre);
    }

    private void DrawLabel(SKCanvas canvas, SKRect screenBox, SKPoint centre)
    {
        var b = TransformedBounds;
        var text = MathF.Abs(_rotation) > 1e-3f
            ? $"{MathF.Round(b.Width)} x {MathF.Round(b.Height)}  ·  {_rotation:0.#}°"
            : $"{MathF.Round(b.Width)} x {MathF.Round(b.Height)}";

        using var font = new SKFont(SKTypeface.Default, 12f);
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        font.MeasureText(text, out var textBounds, textPaint);

        var halfDiag = MathF.Sqrt(screenBox.Width * screenBox.Width + screenBox.Height * screenBox.Height) / 2f;
        var labelX = centre.X - textBounds.Width / 2f;
        var labelY = centre.Y + halfDiag + 8f + textBounds.Height;

        var bgRect = new SKRect(labelX - 6f, labelY - textBounds.Height - 2f, labelX + textBounds.Width + 6f, labelY + 4f);
        using var bgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 180), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(bgRect, 4f, 4f, bgPaint);
        canvas.DrawText(text, labelX, labelY, font, textPaint);
    }

    #endregion
}
