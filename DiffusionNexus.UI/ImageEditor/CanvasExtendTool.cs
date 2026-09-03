using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Canvas Extend tool: grows the canvas around the image without generating content.
/// Behaves like the crop tool visually — round handles on the frame's corners and edge
/// midpoints — but the handles only move outward. The new area is previewed as a
/// checkerboard (it stays transparent when applied) with a green tint and a dashed frame.
/// The frame is drawn from the moment the tool activates, so activating it "selects the
/// whole canvas".
/// </summary>
public sealed class CanvasExtendTool : CanvasExtensionTool
{
    private const float HandleRadius = 6f;
    private const float HandleHitRadiusPixels = 12f;
    private const int CheckerCell = 16;

    private static readonly SKColor Accent = new(76, 175, 80);
    private static readonly SKColor Amber = new(255, 193, 7);
    private static readonly SKBitmap CheckerTile = BuildCheckerTile();

    private static readonly OutpaintHandle[] AllHandles =
    [
        OutpaintHandle.TopLeft, OutpaintHandle.Top, OutpaintHandle.TopRight, OutpaintHandle.Right,
        OutpaintHandle.BottomRight, OutpaintHandle.Bottom, OutpaintHandle.BottomLeft, OutpaintHandle.Left
    ];

    /// <inheritdoc />
    public override float FitMargin => 32f;

    /// <inheritdoc />
    protected override float HandleHitRadius => HandleHitRadiusPixels;

    /// <inheritdoc />
    protected override SKPoint GetHandleCenter(OutpaintHandle handle)
    {
        var rect = GetExtendedScreenRect();
        return handle switch
        {
            OutpaintHandle.TopLeft => new SKPoint(rect.Left, rect.Top),
            OutpaintHandle.Top => new SKPoint(rect.MidX, rect.Top),
            OutpaintHandle.TopRight => new SKPoint(rect.Right, rect.Top),
            OutpaintHandle.Right => new SKPoint(rect.Right, rect.MidY),
            OutpaintHandle.BottomRight => new SKPoint(rect.Right, rect.Bottom),
            OutpaintHandle.Bottom => new SKPoint(rect.MidX, rect.Bottom),
            OutpaintHandle.BottomLeft => new SKPoint(rect.Left, rect.Bottom),
            OutpaintHandle.Left => new SKPoint(rect.Left, rect.MidY),
            _ => SKPoint.Empty
        };
    }

    /// <inheritdoc />
    public override void Render(SKCanvas canvas, SKRect canvasBounds)
    {
        if (!IsActive || ImageRect.Width <= 0 || ImageRect.Height <= 0) return;

        var frame = GetExtendedScreenRect();

        if (HasExtension)
        {
            DrawNewArea(canvas, frame);
            DrawImageOutline(canvas);
        }

        DrawFrame(canvas, frame);
        DrawHandles(canvas);
        DrawResolutionLabel(canvas, frame);
    }

    private void DrawNewArea(SKCanvas canvas, SKRect frame)
    {
        using var shader = SKShader.CreateBitmap(CheckerTile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
        using var checkerPaint = new SKPaint { Shader = shader };
        using var tintPaint = new SKPaint { Color = Accent.WithAlpha(40), Style = SKPaintStyle.Fill };

        foreach (var strip in GetExtensionStrips(frame))
        {
            canvas.DrawRect(strip, checkerPaint);
            canvas.DrawRect(strip, tintPaint);
        }
    }

    private IEnumerable<SKRect> GetExtensionStrips(SKRect frame)
    {
        var image = ImageRect;
        if (ExtendTop > 0) yield return new SKRect(frame.Left, frame.Top, frame.Right, image.Top);
        if (ExtendBottom > 0) yield return new SKRect(frame.Left, image.Bottom, frame.Right, frame.Bottom);
        if (ExtendLeft > 0) yield return new SKRect(frame.Left, image.Top, image.Left, image.Bottom);
        if (ExtendRight > 0) yield return new SKRect(image.Right, image.Top, frame.Right, image.Bottom);
    }

    private void DrawImageOutline(SKCanvas canvas)
    {
        using var paint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 100),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };
        canvas.DrawRect(ImageRect, paint);
    }

    private static void DrawFrame(SKCanvas canvas, SKRect frame)
    {
        using var paint = new SKPaint
        {
            Color = Accent.WithAlpha(200),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([8f, 4f], 0f)
        };
        canvas.DrawRect(frame, paint);
    }

    private void DrawHandles(SKCanvas canvas)
    {
        using var fillPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var activePaint = new SKPaint { Color = IsShrinkBlocked ? Amber : Accent, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var strokePaint = new SKPaint { Color = new SKColor(80, 80, 80), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };

        foreach (var handle in AllHandles)
        {
            var center = GetHandleCenter(handle);
            canvas.DrawCircle(center, HandleRadius, handle == ActiveHandle ? activePaint : fillPaint);
            canvas.DrawCircle(center, HandleRadius, strokePaint);
        }
    }

    private void DrawResolutionLabel(SKCanvas canvas, SKRect frame)
    {
        var (newW, newH) = GetNewDimensions();
        if (newW <= 0 || newH <= 0) return;

        var text = $"{newW} x {newH}";

        using var font = new SKFont(SKTypeface.Default, 12f);
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        font.MeasureText(text, out var textBounds, textPaint);

        var labelX = frame.MidX - textBounds.Width / 2f;
        var labelY = frame.Top - 8f - HandleRadius;

        // If the label would go above the canvas, place it inside the frame at the top
        if (labelY - textBounds.Height < 0)
            labelY = frame.Top + textBounds.Height + 6f + HandleRadius;

        var bgRect = new SKRect(
            labelX - 6f,
            labelY - textBounds.Height - 2f,
            labelX + textBounds.Width + 6f,
            labelY + 4f);

        using var bgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 180), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(bgRect, 4f, 4f, bgPaint);
        canvas.DrawText(text, labelX, labelY, font, textPaint);
    }

    private static SKBitmap BuildCheckerTile()
    {
        var tile = new SKBitmap(CheckerCell * 2, CheckerCell * 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(tile);
        canvas.Clear(new SKColor(0x2B, 0x2B, 0x2B));
        using var light = new SKPaint { Color = new SKColor(0x3B, 0x3B, 0x3B), Style = SKPaintStyle.Fill };
        canvas.DrawRect(new SKRect(0, 0, CheckerCell, CheckerCell), light);
        canvas.DrawRect(new SKRect(CheckerCell, CheckerCell, CheckerCell * 2, CheckerCell * 2), light);
        return tile;
    }
}
