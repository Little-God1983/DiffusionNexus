using Avalonia.Media.Imaging;

namespace DiffusionNexus.UI.DiffusionCanvas;

/// <summary>
/// An immutable copy of a raster's geometry and layer state, taken on the UI thread so region
/// compositing can run on the thread pool without reading live view-model state that the user may be
/// changing (hiding a layer, dragging its opacity) mid-run.
/// </summary>
public sealed record CanvasRasterSnapshot(
    double CanvasX,
    double CanvasY,
    int Width,
    int Height,
    string? ImagePath,
    bool IsVisible,
    double Opacity) : ICanvasRaster
{
    /// <summary>Always null: compositing decodes from <see cref="ImagePath"/>, never from a UI bitmap.</summary>
    public Bitmap? FrameImage => null;

    /// <summary>Copies <paramref name="raster"/>'s current values.</summary>
    public static CanvasRasterSnapshot Of(ICanvasRaster raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        return new(raster.CanvasX, raster.CanvasY, raster.Width, raster.Height,
            raster.ImagePath, raster.IsVisible, raster.Opacity);
    }
}
