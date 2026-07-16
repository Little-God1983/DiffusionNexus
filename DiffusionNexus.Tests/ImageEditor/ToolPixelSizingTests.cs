using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Regression tests proving that the draw, shape, and text tools size their output in
/// <em>image pixels</em>, independently of the current zoom level.
///
/// Previously the slider value was normalized by the displayed image width (which changes
/// with zoom) and then re-expanded by the bitmap width on commit, so the committed size was
/// effectively sliderValue / zoomScale — large when zoomed out, hairline when zoomed in.
/// The fix normalizes by the bitmap pixel width (<c>ImagePixelWidth</c>) instead, so a
/// "20 px" brush always paints 20 image pixels regardless of zoom.
/// </summary>
public class ToolPixelSizingTests
{
    private const int ImagePixelWidth = 1024;

    // Two displayed widths for the same 1024px-wide image => zoom scales of 0.5 and 2.0.
    private const float ZoomedOutDisplayWidth = 512f;
    private const float ZoomedInDisplayWidth = 2048f;

    /// <summary>
    /// The committed value is normalized to image width; multiplying by the bitmap width
    /// (== ImagePixelWidth) yields the painted size in image pixels.
    /// </summary>
    private static float ToImagePixels(float normalizedSize) => normalizedSize * ImagePixelWidth;

    [Theory]
    [InlineData(ZoomedOutDisplayWidth)]
    [InlineData(ZoomedInDisplayWidth)]
    public void DrawingTool_CommitsBrushSizeInImagePixels_RegardlessOfZoom(float displayWidth)
    {
        const float brushSizePx = 20f;
        var tool = new DrawingTool { IsActive = true, BrushSize = brushSizePx, ImagePixelWidth = ImagePixelWidth };
        tool.SetImageBounds(new SKRect(0, 0, displayWidth, displayWidth));

        float capturedNormalized = -1f;
        tool.StrokeCompleted += (_, e) => capturedNormalized = e.BrushSize;

        tool.OnPointerPressed(new SKPoint(displayWidth / 2f, displayWidth / 2f));
        tool.OnPointerReleased();

        ToImagePixels(capturedNormalized).Should().BeApproximately(brushSizePx, 0.001f);
    }

    [Theory]
    [InlineData(ZoomedOutDisplayWidth)]
    [InlineData(ZoomedInDisplayWidth)]
    public void ShapeTool_CommitsStrokeWidthInImagePixels_RegardlessOfZoom(float displayWidth)
    {
        const float strokeWidthPx = 20f;
        var tool = new ShapeTool
        {
            IsActive = true,
            ShapeType = ShapeType.Rectangle,
            StrokeWidth = strokeWidthPx,
            ImagePixelWidth = ImagePixelWidth,
        };
        tool.SetImageBounds(new SKRect(0, 0, displayWidth, displayWidth));

        // Drag out a rectangle large enough to clear the "tiny drag" threshold.
        tool.OnPointerPressed(new SKPoint(10f, 10f));
        tool.OnPointerMoved(new SKPoint(displayWidth - 10f, displayWidth - 10f));
        tool.OnPointerReleased();

        tool.PlacedShape.Should().NotBeNull();
        ToImagePixels(tool.PlacedShape!.StrokeWidth).Should().BeApproximately(strokeWidthPx, 0.001f);
    }

    [Theory]
    [InlineData(ZoomedOutDisplayWidth)]
    [InlineData(ZoomedInDisplayWidth)]
    public void TextTool_PlacesFontSizeInImagePixels_RegardlessOfZoom(float displayWidth)
    {
        const float fontSizePx = 48f;
        const float outlineWidthPx = 4f;
        var tool = new TextTool
        {
            IsActive = true,
            FontSize = fontSizePx,
            OutlineWidth = outlineWidthPx,
            ImagePixelWidth = ImagePixelWidth,
        };
        tool.SetImageBounds(new SKRect(0, 0, displayWidth, displayWidth));

        tool.PlaceTextAt(new SKPoint(displayWidth / 2f, displayWidth / 2f));

        tool.PlacedText.Should().NotBeNull();
        ToImagePixels(tool.PlacedText!.FontSize).Should().BeApproximately(fontSizePx, 0.001f);
        ToImagePixels(tool.PlacedText.OutlineWidth).Should().BeApproximately(outlineWidthPx, 0.001f);
    }

    [Fact]
    public void DrawingTool_SameSliderValue_ProducesSameImagePixelSize_AcrossZoomLevels()
    {
        float CommitAt(float displayWidth)
        {
            var tool = new DrawingTool { IsActive = true, BrushSize = 20f, ImagePixelWidth = ImagePixelWidth };
            tool.SetImageBounds(new SKRect(0, 0, displayWidth, displayWidth));
            float captured = -1f;
            tool.StrokeCompleted += (_, e) => captured = e.BrushSize;
            tool.OnPointerPressed(new SKPoint(displayWidth / 2f, displayWidth / 2f));
            tool.OnPointerReleased();
            return captured;
        }

        CommitAt(ZoomedOutDisplayWidth).Should().BeApproximately(CommitAt(ZoomedInDisplayWidth), 1e-6f);
    }
}
