using Avalonia;

namespace DiffusionNexus.UI.DiffusionCanvas;

/// <summary>
/// Which accepted result sits under a world-space point. Kept out of the surface so the z-order rule is
/// unit-testable without an Avalonia platform.
/// </summary>
public static class CanvasRasterHitTest
{
    /// <summary>
    /// The topmost raster containing <paramref name="world"/>, or null when the point is over empty canvas.
    /// Rasters are in z-order (last = top), so the last hit wins — the one the user actually sees there.
    /// </summary>
    public static ICanvasRaster? TopmostAt(IEnumerable<ICanvasRaster> rasters, Point world)
    {
        ArgumentNullException.ThrowIfNull(rasters);

        ICanvasRaster? hit = null;
        foreach (var raster in rasters)
        {
            var rect = raster.WorldRect;
            if (rect.Width > 0 && rect.Height > 0 && rect.Contains(world))
                hit = raster;
        }

        return hit;
    }
}
