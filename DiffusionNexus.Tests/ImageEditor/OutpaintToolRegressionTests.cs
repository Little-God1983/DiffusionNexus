using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Pins the Outpaint tool's observable behaviour across the extraction of the
/// CanvasExtensionTool base class: arrow handles 36 px outside the frame with a
/// 40 px hit radius, corner drags extending two edges, outward-only extension.
/// </summary>
public class OutpaintToolRegressionTests
{
    private const int Size = 1000; // 1000x1000 image rendered at 100% => screen px == image px

    private static OutpaintTool CreateActive()
    {
        var tool = new OutpaintTool { IsActive = true, ImagePixelWidth = Size, ImagePixelHeight = Size };
        tool.SetImageBounds(new SKRect(0, 0, Size, Size));
        return tool;
    }

    [Fact]
    public void RightArrow_SitsThirtySixPixelsOutsideFrame_WithFortyPixelHitRadius()
    {
        var tool = CreateActive();

        // Centre of the right arrow: (Size + 36, Size/2). 39 px away still hits, 41 px misses.
        tool.GetCursorForPoint(new SKPoint(Size + 36, Size / 2f)).Should().Be(OutpaintHandle.Right);
        tool.GetCursorForPoint(new SKPoint(Size + 36 + 39, Size / 2f)).Should().Be(OutpaintHandle.Right);
        tool.GetCursorForPoint(new SKPoint(Size + 36 + 41, Size / 2f)).Should().Be(OutpaintHandle.None);
    }

    [Fact]
    public void TopLeftArrow_SitsDiagonallyOutsideFrame()
    {
        var tool = CreateActive();

        tool.GetCursorForPoint(new SKPoint(-36, -36)).Should().Be(OutpaintHandle.TopLeft);
    }

    [Fact]
    public void DraggingCornerOutward_ExtendsTwoEdges()
    {
        var tool = CreateActive();

        tool.OnPointerPressed(new SKPoint(Size + 36, Size + 36)).Should().BeTrue(); // bottom-right arrow
        tool.OnPointerMoved(new SKPoint(Size + 36 + 100, Size + 36 + 50));
        tool.OnPointerReleased();

        tool.ExtendRight.Should().Be(100);
        tool.ExtendBottom.Should().Be(50);
        tool.ExtendLeft.Should().Be(0);
        tool.ExtendTop.Should().Be(0);
        tool.GetNewDimensions().Should().Be((Size + 100, Size + 50));
    }

    [Fact]
    public void DraggingEdgeInward_ClampsAtZero()
    {
        var tool = CreateActive();

        tool.OnPointerPressed(new SKPoint(Size + 36, Size / 2f));
        tool.OnPointerMoved(new SKPoint(Size + 36 - 300, Size / 2f));
        tool.OnPointerReleased();

        tool.ExtendRight.Should().Be(0);
        tool.HasExtension.Should().BeFalse();
    }

    [Fact]
    public void SetAspectRatio_ExtendsSymmetrically_NeverShrinks()
    {
        var tool = CreateActive();

        tool.SetAspectRatio(2, 1); // 1000x1000 -> 2000x1000

        tool.ExtendLeft.Should().Be(500);
        tool.ExtendRight.Should().Be(500);
        tool.ExtendTop.Should().Be(0);
        tool.ExtendBottom.Should().Be(0);
    }

    [Fact]
    public void Deactivating_ResetsExtension()
    {
        var tool = CreateActive();
        tool.SetExtension(10, 20, 30, 40);

        tool.IsActive = false;

        tool.HasExtension.Should().BeFalse();
    }

    [Fact]
    public void FitMargin_IsSeventyTwo()
    {
        CreateActive().FitMargin.Should().Be(72f);
    }

    [Fact]
    public void SetTargetSize_SplitsExtensionSymmetrically_OddPixelGoesRightAndBottom()
    {
        var tool = CreateActive();

        tool.SetTargetSize(2049, 1001);

        tool.ExtendLeft.Should().Be(524);
        tool.ExtendRight.Should().Be(525);
        tool.ExtendTop.Should().Be(0);
        tool.ExtendBottom.Should().Be(1);
    }

    [Fact]
    public void SetTargetSize_BelowImage_ClampsAndRaisesShrinkAttempted()
    {
        var tool = CreateActive();
        var shrinkRaised = 0;
        tool.ShrinkAttempted += (_, _) => shrinkRaised++;

        tool.SetTargetSize(800, 1200);

        tool.ExtendLeft.Should().Be(0);
        tool.ExtendRight.Should().Be(0);
        tool.ExtendTop.Should().Be(100);
        tool.ExtendBottom.Should().Be(100);
        shrinkRaised.Should().Be(1);
    }

    [Fact]
    public void InwardDrag_RaisesShrinkAttemptedOncePerGesture_AndFlagsBlocked()
    {
        var tool = CreateActive();
        var shrinkRaised = 0;
        tool.ShrinkAttempted += (_, _) => shrinkRaised++;

        tool.OnPointerPressed(new SKPoint(Size + 36, Size / 2f));
        tool.OnPointerMoved(new SKPoint(Size + 36 - 10, Size / 2f));
        tool.IsShrinkBlocked.Should().BeTrue();
        tool.OnPointerMoved(new SKPoint(Size + 36 - 20, Size / 2f));
        tool.OnPointerMoved(new SKPoint(Size + 36 + 20, Size / 2f)); // back outward
        tool.IsShrinkBlocked.Should().BeFalse();
        tool.OnPointerReleased();

        shrinkRaised.Should().Be(1);
        tool.ExtendRight.Should().Be(20);
        tool.IsShrinkBlocked.Should().BeFalse();
    }
}
