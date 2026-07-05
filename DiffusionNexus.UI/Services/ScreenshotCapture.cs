using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Captures a PNG screenshot of an Avalonia window's current visual tree, downscaled
/// if needed to keep the feedback report payload small.
/// </summary>
public static class ScreenshotCapture
{
    private const int MaxDimension = 1600;

    /// <summary>
    /// Renders <paramref name="window"/> to a PNG byte array. If the window is larger
    /// than <see cref="MaxDimension"/> on its longest side, the image is downscaled
    /// (preserving aspect ratio) before encoding.
    /// </summary>
    public static byte[] CaptureWindowPng(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var width = Math.Max(1, (int)window.Bounds.Width);
        var height = Math.Max(1, (int)window.Bounds.Height);
        var pixelSize = new PixelSize(width, height);

        using var fullBitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        fullBitmap.Render(window);

        var largestSide = Math.Max(width, height);
        if (largestSide <= MaxDimension)
        {
            using var stream = new MemoryStream();
            fullBitmap.Save(stream);
            return stream.ToArray();
        }

        var scale = (double)MaxDimension / largestSide;
        var scaledSize = new PixelSize((int)(width * scale), (int)(height * scale));

        using var loadStream = new MemoryStream();
        fullBitmap.Save(loadStream);
        loadStream.Position = 0;

        using var loadedBitmap = new Bitmap(loadStream);
        using var scaledBitmap = loadedBitmap.CreateScaledBitmap(scaledSize);

        using var outStream = new MemoryStream();
        scaledBitmap.Save(outStream);
        return outStream.ToArray();
    }
}
