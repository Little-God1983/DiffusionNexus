using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Regression tests for two pre-existing text-tool bugs:
/// 1. Text overflowed the bounding box when the font was large — the box now auto-fits
///    (always encloses) the text.
/// 2. A line break rendered a missing-glyph box ("tofu") because a stray '\r' from CRLF
///    input was drawn — line endings are now normalized before layout/measurement.
/// </summary>
public class TextToolBoundingBoxTests
{
    private const int ImageWidth = 1000;
    private const int ImageHeight = 800;
    private const float LineHeightFactor = 1.2f;

    // Rendered at 100% zoom (displayed size == pixel size) to keep the arithmetic exact.
    private static TextTool CreatePlaced(string text, float fontSize)
    {
        var tool = new TextTool
        {
            IsActive = true,
            Text = text,
            FontSize = fontSize,
            ImagePixelWidth = ImageWidth,
            ImagePixelHeight = ImageHeight,
        };
        tool.SetImageBounds(new SKRect(0, 0, ImageWidth, ImageHeight));
        tool.PlaceTextAt(new SKPoint(ImageWidth / 2f, ImageHeight / 2f));
        return tool;
    }

    private static float BoxHeightPx(TextTool tool) =>
        (tool.PlacedText!.NormalizedBottomRight.Y - tool.PlacedText.NormalizedTopLeft.Y) * ImageHeight;

    private static float BoxWidthPx(TextTool tool) =>
        (tool.PlacedText!.NormalizedBottomRight.X - tool.PlacedText.NormalizedTopLeft.X) * ImageWidth;

    [Fact]
    public void BoundingBox_EnclosesSingleLine_WithHeightOfOneLine()
    {
        var tool = CreatePlaced("Hello", 100f);

        // One line: height == fontSize * line-height factor (font-independent).
        BoxHeightPx(tool).Should().BeApproximately(100f * LineHeightFactor, 0.5f);
    }

    [Fact]
    public void BoundingBox_GrowsWithFontSize_SoTextNeverOverflows()
    {
        var small = CreatePlaced("Hello", 50f);
        var large = CreatePlaced("Hello", 200f);

        BoxHeightPx(large).Should().BeGreaterThan(BoxHeightPx(small));
        BoxWidthPx(large).Should().BeGreaterThan(BoxWidthPx(small));
    }

    [Fact]
    public void BoundingBox_GrowsWithLineCount()
    {
        var oneLine = CreatePlaced("Hello", 100f);
        var twoLines = CreatePlaced("Hello\nWorld", 100f);

        BoxHeightPx(twoLines).Should().BeApproximately(BoxHeightPx(oneLine) * 2f, 0.5f);
    }

    [Fact]
    public void BoundingBox_GrowsWithLineWidth()
    {
        var narrow = CreatePlaced("a", 100f);
        var wide = CreatePlaced("aaaaaaaaaa", 100f);

        BoxWidthPx(wide).Should().BeGreaterThan(BoxWidthPx(narrow));
    }

    [Fact]
    public void CrlfLineBreak_IsTreatedSameAsLf_NoStrayCarriageReturn()
    {
        var lf = CreatePlaced("Hello\nWorld", 100f);
        var crlf = CreatePlaced("Hello\r\nWorld", 100f);

        // If the stray '\r' were kept, the first line ("Hello\r") would measure differently
        // and/or render a tofu box. Identical box dimensions confirm normalization.
        BoxHeightPx(crlf).Should().BeApproximately(BoxHeightPx(lf), 0.5f);
        BoxWidthPx(crlf).Should().BeApproximately(BoxWidthPx(lf), 0.5f);
    }

    [Fact]
    public void ResizingCorner_ScalesFontSize_AndBoxStillEnclosesText()
    {
        var tool = CreatePlaced("Hello", 100f);
        var startFont = tool.PlacedText!.FontSize;
        var startHeight = BoxHeightPx(tool);

        // Grab the bottom-right corner and drag outward (away from center) to enlarge.
        DragBottomRightOutward(tool, factor: 2f);

        tool.PlacedText.FontSize.Should().BeGreaterThan(startFont);
        BoxHeightPx(tool).Should().BeGreaterThan(startHeight);
        // Box height continues to match the (now larger) single line.
        BoxHeightPx(tool).Should().BeApproximately(tool.PlacedText.FontSize * ImageWidth * LineHeightFactor, 0.5f);
    }

    [Fact]
    public void ResizingCorner_UpdatesFontSizeSetting_AndRaisesFontSizeChanged()
    {
        var tool = CreatePlaced("Hello", 100f);
        float? raisedPixels = null;
        tool.FontSizeChanged += (_, px) => raisedPixels = px;

        DragBottomRightOutward(tool, factor: 2f);

        // The tool's pixel font-size setting (what the slider binds to) must follow the drag.
        tool.FontSize.Should().BeGreaterThan(100f);
        raisedPixels.Should().NotBeNull();
        raisedPixels!.Value.Should().BeApproximately(tool.FontSize, 0.5f);
    }

    [Fact]
    public void AfterResize_EditingText_KeepsResizedFontSize_NotSliderDefault()
    {
        var tool = CreatePlaced("Hello", 100f);
        DragBottomRightOutward(tool, factor: 2f);
        var resizedFontPx = tool.FontSize;
        resizedFontPx.Should().BeGreaterThan(100f);

        // Simulate the user typing: settings are re-applied from the tool's own (now updated)
        // FontSize, then the placed properties refresh. The text must NOT shrink back.
        tool.Text = "Hello there";
        tool.UpdatePlacedTextProperties();

        (tool.PlacedText!.FontSize * ImageWidth).Should().BeApproximately(resizedFontPx, 0.5f);
    }

    private static void DragBottomRightOutward(TextTool tool, float factor)
    {
        var br = new SKPoint(
            tool.PlacedText!.NormalizedBottomRight.X * ImageWidth,
            tool.PlacedText.NormalizedBottomRight.Y * ImageHeight);
        var center = new SKPoint(ImageWidth / 2f, ImageHeight / 2f);
        var pulled = new SKPoint(
            center.X + (br.X - center.X) * factor,
            center.Y + (br.Y - center.Y) * factor);

        tool.OnPointerPressed(br);
        tool.OnPointerMoved(pulled);
        tool.OnPointerReleased();
    }
}
