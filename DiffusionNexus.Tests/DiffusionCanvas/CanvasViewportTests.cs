using Avalonia;
using DiffusionNexus.UI.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>
/// The canvas viewport is pure maths on purpose — it holds no Avalonia visual types, so the whole
/// pan/zoom/fit contract is testable here without an Avalonia platform.
/// </summary>
public class CanvasViewportTests
{
    [Fact]
    public void ScreenAndWorldRoundTrip()
    {
        var viewport = new CanvasViewport();
        viewport.ZoomAt(new Point(0, 0), 2.5);
        viewport.PanBy(37, -19);

        var world = new Point(1234.5, -678.25);

        var roundTripped = viewport.ScreenToWorld(viewport.WorldToScreen(world));

        roundTripped.X.Should().BeApproximately(world.X, 1e-9);
        roundTripped.Y.Should().BeApproximately(world.Y, 1e-9);
    }

    [Fact]
    public void ZoomAt_KeepsTheAnchoredWorldPointUnderTheCursor()
    {
        var viewport = new CanvasViewport();
        var anchor = new Point(400, 300);
        var worldUnderCursorBefore = viewport.ScreenToWorld(anchor);

        viewport.ZoomAt(anchor, 1.2);
        viewport.ZoomAt(anchor, 1.2);
        viewport.ZoomAt(anchor, 1.2);

        var worldUnderCursorAfter = viewport.ScreenToWorld(anchor);

        viewport.Zoom.Should().BeApproximately(1.728, 1e-9, "three 1.2x steps compound");
        worldUnderCursorAfter.X.Should().BeApproximately(worldUnderCursorBefore.X, 1e-9);
        worldUnderCursorAfter.Y.Should().BeApproximately(worldUnderCursorBefore.Y, 1e-9);
    }

    [Fact]
    public void ZoomAt_ClampsButStillHoldsTheAnchorSteady()
    {
        var viewport = new CanvasViewport();
        var anchor = new Point(120, 80);
        var worldBefore = viewport.ScreenToWorld(anchor);

        viewport.ZoomAt(anchor, 10_000);

        viewport.Zoom.Should().Be(CanvasViewport.MaxZoom);
        viewport.ScreenToWorld(anchor).X.Should().BeApproximately(worldBefore.X, 1e-9);
        viewport.ScreenToWorld(anchor).Y.Should().BeApproximately(worldBefore.Y, 1e-9);
    }

    [Fact]
    public void ZoomAt_ClampsAtTheFloorToo()
    {
        var viewport = new CanvasViewport();

        viewport.ZoomAt(new Point(10, 10), 0.000001);

        viewport.Zoom.Should().Be(CanvasViewport.MinZoom);
    }

    [Fact]
    public void ZoomAt_IgnoresNonsenseFactors()
    {
        var viewport = new CanvasViewport();

        viewport.ZoomAt(new Point(10, 10), 0);
        viewport.ZoomAt(new Point(10, 10), -2);
        viewport.ZoomAt(new Point(10, 10), double.NaN);

        viewport.Zoom.Should().Be(1.0);
    }

    [Fact]
    public void Fit_CentresTheContentAndLeavesAMargin()
    {
        var viewport = new CanvasViewport();
        var content = new Rect(500, 500, 1024, 512);
        var size = new Size(800, 600);

        viewport.Fit(content, size);

        // Width is the binding constraint: (800 - 2*48) / 1024.
        viewport.Zoom.Should().BeApproximately((800 - 96) / 1024.0, 1e-9);

        var centreOnScreen = viewport.WorldToScreen(content.Center);
        centreOnScreen.X.Should().BeApproximately(400, 1e-9);
        centreOnScreen.Y.Should().BeApproximately(300, 1e-9);
    }

    [Fact]
    public void Fit_PutsTheWholeContentInsideTheViewport()
    {
        var viewport = new CanvasViewport();
        var content = new Rect(-2000, 1000, 3000, 4000);
        var size = new Size(1000, 700);

        viewport.Fit(content, size);

        var onScreen = viewport.WorldToScreen(content);
        onScreen.X.Should().BeGreaterThanOrEqualTo(0);
        onScreen.Y.Should().BeGreaterThanOrEqualTo(0);
        onScreen.Right.Should().BeLessThanOrEqualTo(size.Width);
        onScreen.Bottom.Should().BeLessThanOrEqualTo(size.Height);
    }

    [Fact]
    public void Fit_OnADegenerateRectCentresAtOneToOneInsteadOfDividingByZero()
    {
        var viewport = new CanvasViewport();

        viewport.Fit(new Rect(300, 400, 0, 0), new Size(800, 600));

        viewport.Zoom.Should().Be(1.0);
        var centre = viewport.WorldToScreen(new Point(300, 400));
        centre.X.Should().BeApproximately(400, 1e-9);
        centre.Y.Should().BeApproximately(300, 1e-9);
    }

    [Fact]
    public void Fit_IgnoresAnUnmeasuredViewport()
    {
        var viewport = new CanvasViewport();
        viewport.PanBy(11, 13);

        viewport.Fit(new Rect(0, 0, 100, 100), new Size(0, 0));

        viewport.Zoom.Should().Be(1.0);
        viewport.PanX.Should().Be(11);
        viewport.PanY.Should().Be(13);
    }

    [Fact]
    public void OneToOne_ResetsZoomAndKeepsTheViewportCentre()
    {
        var viewport = new CanvasViewport();
        var size = new Size(900, 500);
        viewport.Fit(new Rect(0, 0, 4096, 4096), size);
        var centreWorldBefore = viewport.ScreenToWorld(new Point(450, 250));

        viewport.OneToOne(size);

        viewport.Zoom.Should().Be(1.0);
        var centreWorldAfter = viewport.ScreenToWorld(new Point(450, 250));
        centreWorldAfter.X.Should().BeApproximately(centreWorldBefore.X, 1e-9);
        centreWorldAfter.Y.Should().BeApproximately(centreWorldBefore.Y, 1e-9);
    }

    [Fact]
    public void PanBy_MovesTheWorldByTheScreenDelta()
    {
        var viewport = new CanvasViewport();
        viewport.SetZoomAt(new Point(0, 0), 2);

        viewport.PanBy(60, -40);

        // At 2x, 60 screen pixels is 30 world units.
        viewport.ScreenToWorld(new Point(0, 0)).X.Should().BeApproximately(-30, 1e-9);
        viewport.ScreenToWorld(new Point(0, 0)).Y.Should().BeApproximately(20, 1e-9);
    }

    [Fact]
    public void ScreenToWorldLength_TracksTheZoom()
    {
        var viewport = new CanvasViewport();
        viewport.SetZoomAt(new Point(0, 0), 4);

        viewport.ScreenToWorldLength(8).Should().BeApproximately(2, 1e-9);
    }

    [Fact]
    public void Changed_FiresOnEveryTransformChange()
    {
        var viewport = new CanvasViewport();
        var raised = 0;
        viewport.Changed += (_, _) => raised++;

        viewport.PanBy(1, 0);
        viewport.ZoomAt(new Point(0, 0), 1.5);
        viewport.Fit(new Rect(0, 0, 10, 10), new Size(100, 100));
        viewport.OneToOne(new Size(100, 100));

        raised.Should().Be(4);
    }

    [Fact]
    public void Changed_DoesNotFireForANoOpPan()
    {
        var viewport = new CanvasViewport();
        var raised = 0;
        viewport.Changed += (_, _) => raised++;

        viewport.PanBy(0, 0);

        raised.Should().Be(0);
    }
}
