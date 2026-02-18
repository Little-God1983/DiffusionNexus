using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Defines the interaction phase of the text tool.
/// </summary>
public enum TextToolPhase
{
    /// <summary>No text element is active — ready to place a new one.</summary>
    Idle,
    /// <summary>A text element has been placed and can be moved/resized/edited.</summary>
    Placed,
    /// <summary>The placed text element is being moved.</summary>
    Moving,
    /// <summary>The placed text element is being resized via a corner handle.</summary>
    Resizing,
    /// <summary>The placed text element is being rotated via the rotation handle.</summary>
    Rotating
}

/// <summary>
/// Identifies which manipulation handle is being interacted with on a placed text element.
/// </summary>
public enum TextManipulationHandle
{
    /// <summary>No handle — click is outside the text element.</summary>
    None,
    /// <summary>Click is inside the text body — drag to move.</summary>
    Body,
    /// <summary>Top-left corner resize handle.</summary>
    TopLeft,
    /// <summary>Top-right corner resize handle.</summary>
    TopRight,
    /// <summary>Bottom-left corner resize handle.</summary>
    BottomLeft,
    /// <summary>Bottom-right corner resize handle.</summary>
    BottomRight,
    /// <summary>Rotation handle above the text element.</summary>
    Rotate,
    /// <summary>Delete/trash handle.</summary>
    Delete
}

/// <summary>
/// Platform-independent text tool for the image editor.
/// Supports placing, editing, moving, and styling text elements
/// that are committed as new layers.
/// </summary>
public class TextTool
{
    private bool _isActive;
    private TextToolPhase _phase = TextToolPhase.Idle;
    private SKRect _imageRect;
    private TextElementData? _placedText;

    // Manipulation state
    private TextManipulationHandle _activeHandle;
    private SKPoint _manipulationAnchor;
    private SKPoint _placedScreenTopLeft;
    private SKPoint _placedScreenBottomRight;
    private float _rotationStartAngle;
    private float _rotationBaseAngle;

    private const float HandleRadius = 6f;
    private const float RotateHandleOffset = 30f;
    private const float DeleteHandleOffset = 32f;
    private const float HandleHitRadius = 12f;
    private const float DefaultTextBoxWidth = 300f;
    private const float DefaultTextBoxHeight = 100f;

    /// <summary>
    /// Gets or sets whether the text tool is currently active.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            if (!value)
            {
                // Commit rather than cancel so the last placed text is preserved
                if (HasPlacedText)
                    CommitPlacedText();
                _phase = TextToolPhase.Idle;
            }
        }
    }

    /// <summary>
    /// Gets or sets the text content.
    /// </summary>
    public string Text { get; set; } = "Text";

    /// <summary>
    /// Gets or sets the font family name.
    /// </summary>
    public string FontFamily { get; set; } = "Arial";

    /// <summary>
    /// Gets or sets the font size in pixels.
    /// </summary>
    public float FontSize { get; set; } = 48f;

    /// <summary>
    /// Gets or sets whether the font is bold.
    /// </summary>
    public bool IsBold { get; set; }

    /// <summary>
    /// Gets or sets whether the font is italic.
    /// </summary>
    public bool IsItalic { get; set; }

    /// <summary>
    /// Gets or sets the text color.
    /// </summary>
    public SKColor TextColor { get; set; } = SKColors.White;

    /// <summary>
    /// Gets or sets the outline color.
    /// </summary>
    public SKColor OutlineColor { get; set; } = SKColors.Black;

    /// <summary>
    /// Gets or sets the outline width in pixels (0 = no outline).
    /// </summary>
    public float OutlineWidth { get; set; }

    /// <summary>
    /// Gets the current interaction phase.
    /// </summary>
    public TextToolPhase Phase => _phase;

    /// <summary>
    /// Gets whether a text element is currently placed and can be manipulated.
    /// </summary>
    public bool HasPlacedText => _phase is TextToolPhase.Placed or TextToolPhase.Moving
        or TextToolPhase.Resizing or TextToolPhase.Rotating;

    /// <summary>
    /// Gets the placed text element data.
    /// </summary>
    public TextElementData? PlacedText => _placedText;

    /// <summary>
    /// Event raised when a text element is committed and should be applied to the image as a new layer.
    /// </summary>
    public event EventHandler<TextCompletedEventArgs>? TextCompleted;

    /// <summary>
    /// Event raised when the text preview changes and a redraw is needed.
    /// </summary>
    public event EventHandler? TextChanged;

    /// <summary>
    /// Event raised when the placed-text state changes (placed or cleared).
    /// </summary>
    public event EventHandler? PlacedTextStateChanged;

    /// <summary>
    /// Sets the current image bounds for coordinate mapping.
    /// </summary>
    public void SetImageBounds(SKRect imageRect)
    {
        _imageRect = imageRect;
    }

    /// <summary>
    /// Places a new text element at the specified screen point.
    /// If a text element is already placed, it is committed first.
    /// </summary>
    public void PlaceTextAt(SKPoint screenPoint)
    {
        if (!_isActive || _imageRect.Width <= 0 || _imageRect.Height <= 0) return;

        // If there's already a placed text, commit it first
        if (HasPlacedText)
            CommitPlacedText();

        var halfW = DefaultTextBoxWidth / 2f;
        var halfH = DefaultTextBoxHeight / 2f;

        var normalizedTopLeft = ScreenToNormalized(new SKPoint(screenPoint.X - halfW, screenPoint.Y - halfH));
        var normalizedBottomRight = ScreenToNormalized(new SKPoint(screenPoint.X + halfW, screenPoint.Y + halfH));

        _placedText = new TextElementData
        {
            Text = Text,
            FontFamily = FontFamily,
            FontSize = FontSize / GetCurrentScale(),
            IsBold = IsBold,
            IsItalic = IsItalic,
            TextColor = TextColor,
            OutlineColor = OutlineColor,
            OutlineWidth = OutlineWidth / GetCurrentScale(),
            NormalizedTopLeft = normalizedTopLeft,
            NormalizedBottomRight = normalizedBottomRight,
            RotationDegrees = 0f
        };

        _phase = TextToolPhase.Placed;
        TextChanged?.Invoke(this, EventArgs.Empty);
        PlacedTextStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Places a new text element at the center of the visible image area.
    /// </summary>
    [Obsolete("Use PlaceTextAt instead. This method will be removed in a future version.")]
    public void PlaceText()
    {
        if (!_isActive || _imageRect.Width <= 0 || _imageRect.Height <= 0) return;

        PlaceTextAt(new SKPoint(_imageRect.MidX, _imageRect.MidY));
    }

    /// <summary>
    /// Handles pointer press event.
    /// </summary>
    /// <returns>True if the event was handled.</returns>
    public bool OnPointerPressed(SKPoint screenPoint)
    {
        if (!_isActive) return false;
        if (_imageRect.Width <= 0 || _imageRect.Height <= 0) return false;

        if (_phase == TextToolPhase.Placed)
            return HandlePlacedPointerPressed(screenPoint);

        // Click on the image in Idle state places a new text element at the click point
        if (_phase == TextToolPhase.Idle && _imageRect.Contains(screenPoint))
        {
            PlaceTextAt(screenPoint);
            return true;
        }

        return false;
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
            case TextToolPhase.Moving:
                HandleMoving(screenPoint);
                return true;

            case TextToolPhase.Resizing:
                HandleResizing(screenPoint);
                return true;

            case TextToolPhase.Rotating:
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
            case TextToolPhase.Moving:
            case TextToolPhase.Resizing:
            case TextToolPhase.Rotating:
                _phase = TextToolPhase.Placed;
                TextChanged?.Invoke(this, EventArgs.Empty);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Commits the currently placed text element, firing the TextCompleted event.
    /// </summary>
    /// <returns>True if a text element was committed.</returns>
    public bool CommitPlacedText()
    {
        if (_placedText is null || !HasPlacedText)
            return false;

        if (string.IsNullOrWhiteSpace(_placedText.Text))
        {
            CancelPlacedText();
            return false;
        }

        var args = new TextCompletedEventArgs(_placedText);
        TextCompleted?.Invoke(this, args);

        _placedText = null;
        _phase = TextToolPhase.Idle;
        TextChanged?.Invoke(this, EventArgs.Empty);
        PlacedTextStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Cancels the currently placed text element without applying it.
    /// </summary>
    public void CancelPlacedText()
    {
        if (_placedText is null && _phase == TextToolPhase.Idle) return;

        _placedText = null;
        _phase = TextToolPhase.Idle;
        TextChanged?.Invoke(this, EventArgs.Empty);
        PlacedTextStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Updates the properties of the currently placed text element from current tool settings.
    /// </summary>
    public void UpdatePlacedTextProperties()
    {
        if (_placedText is null) return;

        _placedText.Text = Text;
        _placedText.FontFamily = FontFamily;
        _placedText.FontSize = FontSize / GetCurrentScale();
        _placedText.IsBold = IsBold;
        _placedText.IsItalic = IsItalic;
        _placedText.TextColor = TextColor;
        _placedText.OutlineColor = OutlineColor;
        _placedText.OutlineWidth = OutlineWidth / GetCurrentScale();

        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Determines which manipulation handle is under the given screen point.
    /// </summary>
    public TextManipulationHandle HitTestHandle(SKPoint screenPoint)
    {
        if (_placedText is null || !HasPlacedText) return TextManipulationHandle.None;

        var topLeft = NormalizedToScreen(_placedText.NormalizedTopLeft);
        var bottomRight = NormalizedToScreen(_placedText.NormalizedBottomRight);
        var center = new SKPoint((topLeft.X + bottomRight.X) / 2f, (topLeft.Y + bottomRight.Y) / 2f);
        var rotation = _placedText.RotationDegrees;

        // Transform the test point into the text element's local (unrotated) coordinate system
        var local = RotatePointAround(screenPoint, center, -rotation);

        var rect = new SKRect(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Max(topLeft.X, bottomRight.X),
            Math.Max(topLeft.Y, bottomRight.Y));

        // Corner handles
        if (DistanceSq(local, new SKPoint(rect.Left, rect.Top)) < HandleHitRadius * HandleHitRadius)
            return TextManipulationHandle.TopLeft;
        if (DistanceSq(local, new SKPoint(rect.Right, rect.Top)) < HandleHitRadius * HandleHitRadius)
            return TextManipulationHandle.TopRight;
        if (DistanceSq(local, new SKPoint(rect.Left, rect.Bottom)) < HandleHitRadius * HandleHitRadius)
            return TextManipulationHandle.BottomLeft;
        if (DistanceSq(local, new SKPoint(rect.Right, rect.Bottom)) < HandleHitRadius * HandleHitRadius)
            return TextManipulationHandle.BottomRight;

        // Rotation handle (above top center)
        var rotateHandlePos = new SKPoint(center.X, rect.Top - RotateHandleOffset);
        if (DistanceSq(local, rotateHandlePos) < (HandleHitRadius + 6f) * (HandleHitRadius + 6f))
            return TextManipulationHandle.Rotate;

        // Delete handle (to the right of the rotation handle)
        var deleteHandlePos = new SKPoint(center.X + DeleteHandleOffset, rect.Top - RotateHandleOffset);
        if (DistanceSq(local, deleteHandlePos) < (HandleHitRadius + 6f) * (HandleHitRadius + 6f))
            return TextManipulationHandle.Delete;

        // Body (inside the text rect with some padding)
        var expandedRect = SKRect.Create(rect.Left - 4, rect.Top - 4, rect.Width + 8, rect.Height + 8);
        if (expandedRect.Contains(local))
            return TextManipulationHandle.Body;

        return TextManipulationHandle.None;
    }

    /// <summary>
    /// Renders the text preview and manipulation handles.
    /// </summary>
    public void Render(SKCanvas canvas)
    {
        if (!_isActive || _placedText is null || !HasPlacedText) return;

        var topLeft = NormalizedToScreen(_placedText.NormalizedTopLeft);
        var bottomRight = NormalizedToScreen(_placedText.NormalizedBottomRight);
        var rect = new SKRect(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Max(topLeft.X, bottomRight.X),
            Math.Max(topLeft.Y, bottomRight.Y));
        var center = new SKPoint((topLeft.X + bottomRight.X) / 2f, (topLeft.Y + bottomRight.Y) / 2f);

        var screenFontSize = _placedText.FontSize * GetCurrentScale();
        var screenOutlineWidth = _placedText.OutlineWidth * GetCurrentScale();

        canvas.Save();
        canvas.RotateDegrees(_placedText.RotationDegrees, center.X, center.Y);

        RenderText(canvas, _placedText.Text, rect, screenFontSize,
            _placedText.FontFamily, _placedText.IsBold, _placedText.IsItalic,
            _placedText.TextColor, _placedText.OutlineColor, screenOutlineWidth);

        canvas.Restore();

        RenderManipulationHandles(canvas, topLeft, bottomRight, _placedText.RotationDegrees);
    }

    /// <summary>
    /// Renders text within the specified rectangle on the canvas.
    /// </summary>
    internal static void RenderText(
        SKCanvas canvas,
        string text,
        SKRect rect,
        float fontSize,
        string fontFamily,
        bool isBold,
        bool isItalic,
        SKColor textColor,
        SKColor outlineColor,
        float outlineWidth)
    {
        if (string.IsNullOrEmpty(text)) return;

        var typeface = SKTypeface.FromFamilyName(
            fontFamily,
            isBold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            isItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

        using var font = new SKFont(typeface, fontSize)
        {
            Edging = SKFontEdging.SubpixelAntialias
        };

        var lines = text.Split('\n');
        var lineHeight = fontSize * 1.2f;
        var y = rect.Top + fontSize;

        // Draw outline first if needed
        if (outlineWidth > 0)
        {
            using var outlinePaint = new SKPaint
            {
                Color = outlineColor,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = outlineWidth,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round
            };

            var outlineY = y;
            foreach (var line in lines)
            {
                canvas.DrawText(line, rect.Left, outlineY, font, outlinePaint);
                outlineY += lineHeight;
            }
        }

        // Draw fill text
        using var fillPaint = new SKPaint
        {
            Color = textColor,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        foreach (var line in lines)
        {
            canvas.DrawText(line, rect.Left, y, font, fillPaint);
            y += lineHeight;
        }
    }

    /// <summary>
    /// Renders manipulation handles for the placed text element, supporting rotation.
    /// </summary>
    private static void RenderManipulationHandles(SKCanvas canvas, SKPoint start, SKPoint end, float rotation)
    {
        var center = new SKPoint((start.X + end.X) / 2f, (start.Y + end.Y) / 2f);

        canvas.Save();
        canvas.RotateDegrees(rotation, center.X, center.Y);

        var rect = new SKRect(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Max(start.X, end.X),
            Math.Max(start.Y, end.Y));

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

    private bool HandlePlacedPointerPressed(SKPoint screenPoint)
    {
        var handle = HitTestHandle(screenPoint);

        if (handle == TextManipulationHandle.None)
        {
            // Click outside — commit current text and place a new one if inside image
            CommitPlacedText();
            if (_imageRect.Contains(screenPoint))
            {
                PlaceTextAt(screenPoint);
            }
            return true;
        }

        if (handle == TextManipulationHandle.Delete)
        {
            CancelPlacedText();
            return true;
        }

        _manipulationAnchor = screenPoint;
        _placedScreenTopLeft = NormalizedToScreen(_placedText!.NormalizedTopLeft);
        _placedScreenBottomRight = NormalizedToScreen(_placedText.NormalizedBottomRight);
        _activeHandle = handle;

        switch (handle)
        {
            case TextManipulationHandle.Body:
                _phase = TextToolPhase.Moving;
                break;

            case TextManipulationHandle.Rotate:
                _phase = TextToolPhase.Rotating;
                var center = new SKPoint(
                    (_placedScreenTopLeft.X + _placedScreenBottomRight.X) / 2f,
                    (_placedScreenTopLeft.Y + _placedScreenBottomRight.Y) / 2f);
                _rotationStartAngle = MathF.Atan2(
                    screenPoint.Y - center.Y, screenPoint.X - center.X);
                _rotationBaseAngle = _placedText.RotationDegrees;
                break;

            default: // Corner resize handles
                _phase = TextToolPhase.Resizing;
                break;
        }

        TextChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void HandleMoving(SKPoint screenPoint)
    {
        if (_placedText is null) return;

        var dx = screenPoint.X - _manipulationAnchor.X;
        var dy = screenPoint.Y - _manipulationAnchor.Y;

        var newTopLeft = new SKPoint(_placedScreenTopLeft.X + dx, _placedScreenTopLeft.Y + dy);
        var newBottomRight = new SKPoint(_placedScreenBottomRight.X + dx, _placedScreenBottomRight.Y + dy);

        _placedText.NormalizedTopLeft = ScreenToNormalized(newTopLeft);
        _placedText.NormalizedBottomRight = ScreenToNormalized(newBottomRight);

        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleResizing(SKPoint screenPoint)
    {
        if (_placedText is null) return;

        var center = new SKPoint(
            (_placedScreenTopLeft.X + _placedScreenBottomRight.X) / 2f,
            (_placedScreenTopLeft.Y + _placedScreenBottomRight.Y) / 2f);

        // Work in local (unrotated) coordinates
        var localPoint = RotatePointAround(screenPoint, center, -_placedText.RotationDegrees);
        var localTopLeft = RotatePointAround(_placedScreenTopLeft, center, -_placedText.RotationDegrees);
        var localBottomRight = RotatePointAround(_placedScreenBottomRight, center, -_placedText.RotationDegrees);

        var left = Math.Min(localTopLeft.X, localBottomRight.X);
        var top = Math.Min(localTopLeft.Y, localBottomRight.Y);
        var right = Math.Max(localTopLeft.X, localBottomRight.X);
        var bottom = Math.Max(localTopLeft.Y, localBottomRight.Y);

        switch (_activeHandle)
        {
            case TextManipulationHandle.TopLeft:
                left = localPoint.X;
                top = localPoint.Y;
                break;
            case TextManipulationHandle.TopRight:
                right = localPoint.X;
                top = localPoint.Y;
                break;
            case TextManipulationHandle.BottomLeft:
                left = localPoint.X;
                bottom = localPoint.Y;
                break;
            case TextManipulationHandle.BottomRight:
                right = localPoint.X;
                bottom = localPoint.Y;
                break;
        }

        // Ensure minimum size
        if (Math.Abs(right - left) < 20 || Math.Abs(bottom - top) < 20) return;

        // Rotate back to screen coordinates
        var newTopLeft = RotatePointAround(new SKPoint(left, top), center, _placedText.RotationDegrees);
        var newBottomRight = RotatePointAround(new SKPoint(right, bottom), center, _placedText.RotationDegrees);

        _placedText.NormalizedTopLeft = ScreenToNormalized(newTopLeft);
        _placedText.NormalizedBottomRight = ScreenToNormalized(newBottomRight);

        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleRotating(SKPoint screenPoint)
    {
        if (_placedText is null) return;

        var screenTopLeft = NormalizedToScreen(_placedText.NormalizedTopLeft);
        var screenBottomRight = NormalizedToScreen(_placedText.NormalizedBottomRight);
        var center = new SKPoint(
            (screenTopLeft.X + screenBottomRight.X) / 2f,
            (screenTopLeft.Y + screenBottomRight.Y) / 2f);

        var currentAngle = MathF.Atan2(
            screenPoint.Y - center.Y, screenPoint.X - center.X);
        var deltaAngle = (currentAngle - _rotationStartAngle) * (180f / MathF.PI);

        _placedText.RotationDegrees = _rotationBaseAngle + deltaAngle;

        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private SKPoint ScreenToNormalized(SKPoint screenPoint)
    {
        if (_imageRect.Width <= 0 || _imageRect.Height <= 0)
            return screenPoint;

        var x = (screenPoint.X - _imageRect.Left) / _imageRect.Width;
        var y = (screenPoint.Y - _imageRect.Top) / _imageRect.Height;

        return new SKPoint(x, y);
    }

    private SKPoint NormalizedToScreen(SKPoint normalized)
    {
        if (_imageRect.Width <= 0 || _imageRect.Height <= 0)
            return normalized;

        var x = _imageRect.Left + normalized.X * _imageRect.Width;
        var y = _imageRect.Top + normalized.Y * _imageRect.Height;
        return new SKPoint(x, y);
    }

    private float GetCurrentScale()
    {
        return _imageRect.Width > 0 ? _imageRect.Width : 1f;
    }

    private static float DistanceSq(SKPoint a, SKPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static SKPoint RotatePointAround(SKPoint point, SKPoint center, float degrees)
    {
        var radians = degrees * (MathF.PI / 180f);
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        return new SKPoint(
            center.X + dx * cos - dy * sin,
            center.Y + dx * sin + dy * cos);
    }
}

/// <summary>
/// Data representing a text element that can be manipulated before committing to a layer.
/// </summary>
public class TextElementData
{
    /// <summary>The text content.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The font family name.</summary>
    public string FontFamily { get; set; } = "Arial";

    /// <summary>The font size in normalized coordinates relative to image width.</summary>
    public float FontSize { get; set; } = 0.05f;

    /// <summary>Whether the font is bold.</summary>
    public bool IsBold { get; set; }

    /// <summary>Whether the font is italic.</summary>
    public bool IsItalic { get; set; }

    /// <summary>The text color.</summary>
    public SKColor TextColor { get; set; } = SKColors.White;

    /// <summary>The outline color.</summary>
    public SKColor OutlineColor { get; set; } = SKColors.Black;

    /// <summary>The outline width in normalized coordinates relative to image width.</summary>
    public float OutlineWidth { get; set; }

    /// <summary>The top-left position in normalized image coordinates (0-1).</summary>
    public SKPoint NormalizedTopLeft { get; set; }

    /// <summary>The bottom-right position in normalized image coordinates (0-1).</summary>
    public SKPoint NormalizedBottomRight { get; set; }

    /// <summary>The rotation angle in degrees.</summary>
    public float RotationDegrees { get; set; }

    /// <summary>Gets the center point of the text element in normalized coordinates.</summary>
    public SKPoint NormalizedCenter => new(
        (NormalizedTopLeft.X + NormalizedBottomRight.X) / 2f,
        (NormalizedTopLeft.Y + NormalizedBottomRight.Y) / 2f);
}

/// <summary>
/// Event arguments for when a text element is completed and should be applied.
/// </summary>
public class TextCompletedEventArgs : EventArgs
{
    /// <summary>
    /// The completed text element data.
    /// </summary>
    public TextElementData TextElement { get; }

    public TextCompletedEventArgs(TextElementData textElement)
    {
        TextElement = textElement;
    }
}
