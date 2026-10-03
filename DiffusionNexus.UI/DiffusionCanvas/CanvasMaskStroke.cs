using Avalonia;

namespace DiffusionNexus.UI.DiffusionCanvas;

/// <summary>
/// One drag of the mask brush or eraser, in world coordinates (#595).
/// </summary>
/// <remarks>
/// The mask is kept as strokes rather than pixels because the canvas is unbounded and zoomable: strokes
/// cost almost nothing to hold, stay sharp at every zoom, and rasterise to exactly the box's region at
/// Generate. Immutable, so a snapshot handed to the render thread or a background rasterise cannot change
/// under it.
/// </remarks>
public sealed class CanvasMaskStroke
{
    /// <param name="points">The pointer's path in world coordinates. Must hold at least one point.</param>
    /// <param name="size">Brush diameter in world pixels.</param>
    /// <param name="isErase">True for the eraser, which removes painted area instead of adding it.</param>
    public CanvasMaskStroke(IReadOnlyList<Point> points, double size, bool isErase)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            throw new ArgumentException("A stroke needs at least one point.", nameof(points));
        if (!(size > 0) || double.IsInfinity(size))
            throw new ArgumentOutOfRangeException(nameof(size), size, "A stroke needs a positive, finite size.");

        Points = points.ToArray();
        Size = size;
        IsErase = isErase;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in Points)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        var half = size / 2;
        Bounds = new Rect(minX - half, minY - half, maxX - minX + size, maxY - minY + size);
    }

    /// <summary>The pointer's path in world coordinates.</summary>
    public IReadOnlyList<Point> Points { get; }

    /// <summary>Brush diameter in world pixels.</summary>
    public double Size { get; }

    /// <summary>True for an eraser stroke.</summary>
    public bool IsErase { get; }

    /// <summary>Everything the stroke can touch: its points' bounding box grown by half the brush.</summary>
    public Rect Bounds { get; }
}
