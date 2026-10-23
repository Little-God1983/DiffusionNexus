using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Represents which directional handle is being dragged during an outward canvas resize.
/// Shared by <see cref="OutpaintTool"/> and <see cref="CanvasExtendTool"/>.
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
/// Outpainting tool: extends the canvas beyond the original image so an AI workflow can
/// fill the new area. Renders directional arrow handles outside each edge and corner and
/// tints the frame by <see cref="Severity"/>. All extension state and drag math live in
/// <see cref="CanvasExtensionTool"/>.
/// </summary>
public class OutpaintTool : CanvasExtensionTool
{
    private const float ArrowSize = 32f;
    private const float ArrowHitSize = 40f;
    private const float HandleGap = 4f;

    /// <inheritdoc />
    public override float FitMargin => 72f;

    /// <inheritdoc />
    protected override float HandleHitRadius => ArrowHitSize;

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
    /// Handle positions are anchored to the extended rect so they ride along with the
    /// outpaint frame as the user drags. Edge handles are centered on each side;
    /// corner handles sit diagonally outside the rect corners.
    /// </summary>
    protected override SKPoint GetHandleCenter(OutpaintHandle handle)
    {
        var rect = GetExtendedScreenRect();
        var outX = ArrowSize + HandleGap;
        var outY = ArrowSize + HandleGap;

        return handle switch
        {
            OutpaintHandle.Top => new SKPoint(rect.MidX, rect.Top - outY),
            OutpaintHandle.Right => new SKPoint(rect.Right + outX, rect.MidY),
            OutpaintHandle.Bottom => new SKPoint(rect.MidX, rect.Bottom + outY),
            OutpaintHandle.Left => new SKPoint(rect.Left - outX, rect.MidY),
            OutpaintHandle.TopLeft => new SKPoint(rect.Left - outX, rect.Top - outY),
            OutpaintHandle.TopRight => new SKPoint(rect.Right + outX, rect.Top - outY),
            OutpaintHandle.BottomLeft => new SKPoint(rect.Left - outX, rect.Bottom + outY),
            OutpaintHandle.BottomRight => new SKPoint(rect.Right + outX, rect.Bottom + outY),
            _ => SKPoint.Empty
        };
    }

    /// <summary>
    /// Renders the outpaint overlay with extension region and arrow handles at the image edges.
    /// </summary>
    public override void Render(SKCanvas canvas, SKRect canvasBounds)
    {
        if (!IsActive || ImageRect.Width <= 0 || ImageRect.Height <= 0) return;

        var extendedRect = GetExtendedScreenRect();

        if (HasExtension)
        {
            DrawExtensionRegion(canvas, extendedRect);
        }

        DrawArrowHandles(canvas);

        if (HasExtension)
        {
            DrawResolutionLabel(canvas, extendedRect);
        }
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

    private void DrawExtensionRegion(SKCanvas canvas, SKRect extendedRect)
    {
        var accent = GetAccentBaseColor();
        var imageRect = ImageRect;

        using var borderPaint = new SKPaint
        {
            Color = accent.WithAlpha(200),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([8f, 4f], 0f)
        };
        canvas.DrawRect(extendedRect, borderPaint);

        using var fillPaint = new SKPaint
        {
            Color = accent.WithAlpha(40),
            Style = SKPaintStyle.Fill
        };

        if (ExtendTop > 0)
            canvas.DrawRect(new SKRect(extendedRect.Left, extendedRect.Top, extendedRect.Right, imageRect.Top), fillPaint);
        if (ExtendBottom > 0)
            canvas.DrawRect(new SKRect(extendedRect.Left, imageRect.Bottom, extendedRect.Right, extendedRect.Bottom), fillPaint);
        if (ExtendLeft > 0)
            canvas.DrawRect(new SKRect(extendedRect.Left, imageRect.Top, imageRect.Left, imageRect.Bottom), fillPaint);
        if (ExtendRight > 0)
            canvas.DrawRect(new SKRect(imageRect.Right, imageRect.Top, extendedRect.Right, imageRect.Bottom), fillPaint);

        using var imageBorderPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 100),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };
        canvas.DrawRect(imageRect, imageBorderPaint);
    }

    private void DrawArrowHandles(SKCanvas canvas)
    {
        DrawArrow(canvas, GetHandleCenter(OutpaintHandle.Top), Direction.Up, ActiveHandle == OutpaintHandle.Top);
        DrawArrow(canvas, GetHandleCenter(OutpaintHandle.Bottom), Direction.Down, ActiveHandle == OutpaintHandle.Bottom);
        DrawArrow(canvas, GetHandleCenter(OutpaintHandle.Left), Direction.Left, ActiveHandle == OutpaintHandle.Left);
        DrawArrow(canvas, GetHandleCenter(OutpaintHandle.Right), Direction.Right, ActiveHandle == OutpaintHandle.Right);
        DrawArrow(canvas, GetHandleCenter(OutpaintHandle.TopLeft), Direction.UpLeft, ActiveHandle == OutpaintHandle.TopLeft);
        DrawArrow(canvas, GetHandleCenter(OutpaintHandle.TopRight), Direction.UpRight, ActiveHandle == OutpaintHandle.TopRight);
        DrawArrow(canvas, GetHandleCenter(OutpaintHandle.BottomLeft), Direction.DownLeft, ActiveHandle == OutpaintHandle.BottomLeft);
        DrawArrow(canvas, GetHandleCenter(OutpaintHandle.BottomRight), Direction.DownRight, ActiveHandle == OutpaintHandle.BottomRight);
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
