namespace DiffusionNexus.UI.DiffusionCanvas;

/// <summary>What a left drag on the canvas does while the inpaint mask is selected (#595).</summary>
public enum CanvasPaintTool
{
    /// <summary>No paint tool: a left drag moves or resizes the box.</summary>
    None,

    /// <summary>A left drag paints the mask: the area the next run may repaint.</summary>
    Brush,

    /// <summary>A left drag removes painted mask.</summary>
    Eraser,
}

/// <summary>The brush's size rules, shared by the surface's Shift+wheel, the canvas's [ / ] keys and the slider.</summary>
public static class CanvasBrush
{
    /// <summary>Smallest brush, in world pixels.</summary>
    public const double MinSize = 4;

    /// <summary>Largest brush, in world pixels.</summary>
    public const double MaxSize = 512;

    /// <summary>A size rounded to a whole pixel and held within <see cref="MinSize"/>..<see cref="MaxSize"/>.</summary>
    public static double Clamp(double size) => Math.Clamp(Math.Round(size), MinSize, MaxSize);

    /// <summary>
    /// One resize step: a quarter of the size, and at least one pixel so small brushes still move, already
    /// clamped. Every writer clamps: a two-way binding does not push a host's clamp back when the clamped
    /// value equals what the host already holds.
    /// </summary>
    public static double Step(double size, bool grow) => Clamp(grow
        ? Math.Max(size + 1, size * 1.25)
        : Math.Min(size - 1, size / 1.25));
}
