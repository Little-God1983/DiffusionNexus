using Avalonia;

namespace DiffusionNexus.UI.DiffusionCanvas;

/// <summary>
/// The Diffusion Canvas's world-space transform: a uniform zoom plus a screen-space pan.
///
/// This is the single source of truth for where the world is on screen, in the same spirit as the
/// Image Editor's <c>ViewportManager</c>. It deliberately holds no Avalonia visual types and does no
/// rendering, so every behaviour below is unit-testable in <c>DiffusionNexus.Tests</c> — which never
/// initialises an Avalonia platform.
///
/// The mapping is <c>screen = world * Zoom + Pan</c>. "World" units are generation pixels: a bounding
/// box 1024 world units wide produces a 1024 px latent, whatever the zoom happens to be.
/// </summary>
public sealed class CanvasViewport
{
    /// <summary>Furthest zoom-out. Below this the dot grid stops being legible and panning loses the content.</summary>
    public const double MinZoom = 0.05;

    /// <summary>Furthest zoom-in. Matches the Image Editor's ceiling closely enough to feel like one app.</summary>
    public const double MaxZoom = 8.0;

    /// <summary>Screen-space padding left around the content by <see cref="Fit"/>.</summary>
    public const double FitMargin = 48;

    /// <summary>Screen pixels per world unit. Always within [<see cref="MinZoom"/>, <see cref="MaxZoom"/>].</summary>
    public double Zoom { get; private set; } = 1.0;

    /// <summary>Screen-space X of the world origin.</summary>
    public double PanX { get; private set; }

    /// <summary>Screen-space Y of the world origin.</summary>
    public double PanY { get; private set; }

    /// <summary>Raised whenever the transform changes, so the surface can invalidate itself.</summary>
    public event EventHandler? Changed;

    public Point WorldToScreen(Point world) =>
        new(world.X * Zoom + PanX, world.Y * Zoom + PanY);

    public Point ScreenToWorld(Point screen) =>
        new((screen.X - PanX) / Zoom, (screen.Y - PanY) / Zoom);

    public Rect WorldToScreen(Rect world) =>
        new(world.X * Zoom + PanX, world.Y * Zoom + PanY, world.Width * Zoom, world.Height * Zoom);

    public Rect ScreenToWorld(Rect screen) =>
        new((screen.X - PanX) / Zoom, (screen.Y - PanY) / Zoom, screen.Width / Zoom, screen.Height / Zoom);

    /// <summary>Converts a screen-space length to world units (used to size hit-test radii).</summary>
    public double ScreenToWorldLength(double screenLength) => screenLength / Zoom;

    /// <summary>Drags the world by a screen-space delta.</summary>
    public void PanBy(double deltaScreenX, double deltaScreenY)
    {
        if (deltaScreenX == 0 && deltaScreenY == 0)
            return;

        PanX += deltaScreenX;
        PanY += deltaScreenY;
        Raise();
    }

    /// <summary>
    /// Multiplies the zoom by <paramref name="factor"/> while keeping the world point currently under
    /// <paramref name="screenAnchor"/> pinned to that same screen position — the scroll-to-zoom gesture.
    /// </summary>
    public void ZoomAt(Point screenAnchor, double factor)
    {
        if (factor <= 0 || double.IsNaN(factor) || double.IsInfinity(factor))
            return;

        SetZoomAt(screenAnchor, Zoom * factor);
    }

    /// <summary>
    /// Sets an absolute zoom while keeping the world point under <paramref name="screenAnchor"/> fixed.
    /// The requested zoom is clamped; asking for more than the clamp allows still keeps the anchor stable.
    /// </summary>
    public void SetZoomAt(Point screenAnchor, double zoom)
    {
        var clamped = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (double.IsNaN(clamped) || clamped == Zoom)
            return;

        // Solve for the pan that leaves ScreenToWorld(anchor) unchanged across the zoom change.
        var worldAnchor = ScreenToWorld(screenAnchor);
        Zoom = clamped;
        PanX = screenAnchor.X - worldAnchor.X * Zoom;
        PanY = screenAnchor.Y - worldAnchor.Y * Zoom;
        Raise();
    }

    /// <summary>
    /// Frames <paramref name="worldContent"/> inside a viewport of <paramref name="viewportSize"/>,
    /// centred, with <see cref="FitMargin"/> of breathing room. A degenerate content rect (zero width or
    /// height) centres on it at 1:1 rather than dividing by zero.
    /// </summary>
    public void Fit(Rect worldContent, Size viewportSize, double margin = FitMargin)
    {
        if (viewportSize.Width <= 0 || viewportSize.Height <= 0)
            return;

        if (worldContent.Width <= 0 || worldContent.Height <= 0)
        {
            SetZoomAndCenter(1.0, worldContent.Center, viewportSize);
            return;
        }

        var available = new Size(
            Math.Max(1, viewportSize.Width - margin * 2),
            Math.Max(1, viewportSize.Height - margin * 2));

        var zoom = Math.Min(available.Width / worldContent.Width, available.Height / worldContent.Height);
        SetZoomAndCenter(zoom, worldContent.Center, viewportSize);
    }

    /// <summary>Zoom 1:1 (one world unit = one screen pixel), keeping the viewport's centre world point.</summary>
    public void OneToOne(Size viewportSize)
    {
        if (viewportSize.Width <= 0 || viewportSize.Height <= 0)
            return;

        var centreWorld = ScreenToWorld(new Point(viewportSize.Width / 2, viewportSize.Height / 2));
        SetZoomAndCenter(1.0, centreWorld, viewportSize);
    }

    /// <summary>Centres the viewport on a world point without changing the zoom.</summary>
    public void CenterOn(Point world, Size viewportSize) => SetZoomAndCenter(Zoom, world, viewportSize);

    private void SetZoomAndCenter(double zoom, Point worldCenter, Size viewportSize)
    {
        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        PanX = viewportSize.Width / 2 - worldCenter.X * Zoom;
        PanY = viewportSize.Height / 2 - worldCenter.Y * Zoom;
        Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
