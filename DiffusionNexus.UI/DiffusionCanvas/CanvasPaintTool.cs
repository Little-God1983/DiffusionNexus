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

/// <summary>The brush's sizing rule, shared by the surface's Shift+wheel and the canvas's [ / ] keys.</summary>
public static class CanvasBrush
{
    /// <summary>
    /// One resize step: a quarter of the size, and at least one pixel so small brushes still move. The
    /// caller clamps.
    /// </summary>
    public static double Step(double size, bool grow) => grow
        ? Math.Max(size + 1, size * 1.25)
        : Math.Min(size - 1, size / 1.25);
}
