using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// The classic "see-through" checkerboard painted under the image so transparent pixels
/// are visible against the dark canvas. Without it a transparent area (an extended
/// canvas strip, a removed background) is indistinguishable from the canvas background.
/// Cells are screen pixels, anchored at the image's top-left so the pattern moves with
/// the image when panning instead of sliding underneath it.
/// </summary>
public static class TransparencyCheckerboard
{
    /// <summary>Cell size in screen pixels.</summary>
    public const int Cell = 8;

    /// <summary>The darker of the two cell colours.</summary>
    public static readonly SKColor Dark = new(0x66, 0x66, 0x66);

    /// <summary>The lighter of the two cell colours.</summary>
    public static readonly SKColor Light = new(0x99, 0x99, 0x99);

    // One tile for the whole process: intentionally never disposed, it lives as long as
    // the app and is only ever read through a shader.
    private static readonly SKBitmap Tile = BuildTile();

    /// <summary>Fills <paramref name="rect"/> with the checkerboard.</summary>
    public static void Draw(SKCanvas canvas, SKRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        using var shader = SKShader.CreateBitmap(
            Tile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
            SKMatrix.CreateTranslation(rect.Left, rect.Top));
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(rect, paint);
    }

    private static SKBitmap BuildTile()
    {
        var tile = new SKBitmap(Cell * 2, Cell * 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(tile);
        canvas.Clear(Dark);
        using var light = new SKPaint { Color = Light, Style = SKPaintStyle.Fill };
        canvas.DrawRect(new SKRect(0, 0, Cell, Cell), light);
        canvas.DrawRect(new SKRect(Cell, Cell, Cell * 2, Cell * 2), light);
        return tile;
    }
}
