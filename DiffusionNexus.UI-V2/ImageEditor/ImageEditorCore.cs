using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Platform-independent image editor core using SkiaSharp.
/// Can be reused in any .NET project (WPF, MAUI, Avalonia, etc.)
/// </summary>
public class ImageEditorCore : IDisposable
{
    private SKBitmap? _originalBitmap;
    private SKBitmap? _workingBitmap;
    private bool _disposed;

    /// <summary>
    /// Gets the crop tool instance.
    /// </summary>
    public CropTool CropTool { get; } = new();

    /// <summary>
    /// Gets the current working bitmap width.
    /// </summary>
    public int Width => _workingBitmap?.Width ?? 0;

    /// <summary>
    /// Gets the current working bitmap height.
    /// </summary>
    public int Height => _workingBitmap?.Height ?? 0;

    /// <summary>
    /// Gets whether an image is currently loaded.
    /// </summary>
    public bool HasImage => _workingBitmap is not null;

    /// <summary>
    /// Gets the current image path.
    /// </summary>
    public string? CurrentImagePath { get; private set; }

    /// <summary>
    /// Event raised when the image is modified.
    /// </summary>
    public event EventHandler? ImageChanged;

    /// <summary>
    /// Loads an image from the specified file path.
    /// </summary>
    /// <param name="filePath">Path to the image file.</param>
    /// <returns>True if the image was loaded successfully.</returns>
    public bool LoadImage(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            _originalBitmap?.Dispose();
            _workingBitmap?.Dispose();

            using var stream = File.OpenRead(filePath);
            _originalBitmap = SKBitmap.Decode(stream);

            if (_originalBitmap is null)
                return false;

            _workingBitmap = _originalBitmap.Copy();
            CurrentImagePath = filePath;
            OnImageChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Loads an image from a byte array.
    /// </summary>
    /// <param name="imageData">The image data as bytes.</param>
    /// <returns>True if the image was loaded successfully.</returns>
    public bool LoadImage(byte[] imageData)
    {
        if (imageData is null || imageData.Length == 0)
            return false;

        try
        {
            _originalBitmap?.Dispose();
            _workingBitmap?.Dispose();

            _originalBitmap = SKBitmap.Decode(imageData);

            if (_originalBitmap is null)
                return false;

            _workingBitmap = _originalBitmap.Copy();
            CurrentImagePath = null;
            OnImageChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Renders the current image to an SKCanvas at the specified position and scale.
    /// </summary>
    /// <param name="canvas">The canvas to render to.</param>
    /// <param name="destRect">Destination rectangle for rendering.</param>
    public void Render(SKCanvas canvas, SKRect destRect)
    {
        if (_workingBitmap is null || canvas is null)
            return;

        canvas.DrawBitmap(_workingBitmap, destRect);
    }

    /// <summary>
    /// Renders the current image centered within the given bounds, maintaining aspect ratio.
    /// </summary>
    /// <param name="canvas">The canvas to render to.</param>
    /// <param name="canvasWidth">Available canvas width.</param>
    /// <param name="canvasHeight">Available canvas height.</param>
    /// <param name="backgroundColor">Background color to clear with.</param>
    /// <returns>The actual rectangle where the image was rendered.</returns>
    public SKRect RenderCentered(SKCanvas canvas, float canvasWidth, float canvasHeight, SKColor backgroundColor)
    {
        canvas.Clear(backgroundColor);

        if (_workingBitmap is null)
            return SKRect.Empty;

        var imageRect = CalculateFitRect(canvasWidth, canvasHeight);
        canvas.DrawBitmap(_workingBitmap, imageRect);

        // Update crop tool with current image bounds and render overlay
        CropTool.SetImageBounds(imageRect);
        CropTool.Render(canvas, new SKRect(0, 0, canvasWidth, canvasHeight));

        return imageRect;
    }

    /// <summary>
    /// Calculates the rectangle to fit the image within the given bounds while maintaining aspect ratio.
    /// </summary>
    public SKRect CalculateFitRect(float containerWidth, float containerHeight)
    {
        if (_workingBitmap is null)
            return SKRect.Empty;

        var imageWidth = (float)_workingBitmap.Width;
        var imageHeight = (float)_workingBitmap.Height;

        // Calculate scale to fit
        var scaleX = containerWidth / imageWidth;
        var scaleY = containerHeight / imageHeight;
        var scale = Math.Min(scaleX, scaleY);

        var scaledWidth = imageWidth * scale;
        var scaledHeight = imageHeight * scale;

        // Center the image
        var x = (containerWidth - scaledWidth) / 2f;
        var y = (containerHeight - scaledHeight) / 2f;

        return new SKRect(x, y, x + scaledWidth, y + scaledHeight);
    }

    /// <summary>
    /// Resets to the original loaded image, discarding all edits.
    /// </summary>
    public void ResetToOriginal()
    {
        if (_originalBitmap is null)
            return;

        _workingBitmap?.Dispose();
        _workingBitmap = _originalBitmap.Copy();
        OnImageChanged();
    }

    /// <summary>
    /// Clears the current image.
    /// </summary>
    public void Clear()
    {
        _originalBitmap?.Dispose();
        _workingBitmap?.Dispose();
        _originalBitmap = null;
        _workingBitmap = null;
        CurrentImagePath = null;
        OnImageChanged();
    }

    /// <summary>
    /// Gets the working bitmap for external rendering (read-only access recommended).
    /// </summary>
    public SKBitmap? GetWorkingBitmap() => _workingBitmap;

    /// <summary>
    /// Crops the image to the specified rectangle in image coordinates.
    /// </summary>
    /// <param name="cropRect">The crop rectangle in image pixel coordinates.</param>
    /// <returns>True if the crop was successful.</returns>
    public bool Crop(SKRectI cropRect)
    {
        if (_workingBitmap is null)
            return false;

        // Validate crop rectangle
        if (cropRect.Width <= 0 || cropRect.Height <= 0)
            return false;

        // Clamp to image bounds
        var clampedRect = new SKRectI(
            Math.Clamp(cropRect.Left, 0, _workingBitmap.Width),
            Math.Clamp(cropRect.Top, 0, _workingBitmap.Height),
            Math.Clamp(cropRect.Right, 0, _workingBitmap.Width),
            Math.Clamp(cropRect.Bottom, 0, _workingBitmap.Height));

        if (clampedRect.Width <= 0 || clampedRect.Height <= 0)
            return false;

        try
        {
            // Create new bitmap with cropped dimensions
            var croppedBitmap = new SKBitmap(clampedRect.Width, clampedRect.Height);

            using (var canvas = new SKCanvas(croppedBitmap))
            {
                var srcRect = new SKRect(clampedRect.Left, clampedRect.Top, clampedRect.Right, clampedRect.Bottom);
                var destRect = new SKRect(0, 0, clampedRect.Width, clampedRect.Height);
                canvas.DrawBitmap(_workingBitmap, srcRect, destRect);
            }

            // Replace working bitmap
            _workingBitmap.Dispose();
            _workingBitmap = croppedBitmap;

            // Clear crop tool state
            CropTool.ClearCropRegion();

            OnImageChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Applies the current crop selection from the crop tool.
    /// </summary>
    /// <returns>True if the crop was successful.</returns>
    public bool ApplyCrop()
    {
        if (!CropTool.HasCropRegion || _workingBitmap is null)
            return false;

        var cropRect = CropTool.GetImageCropRect(_workingBitmap.Width, _workingBitmap.Height);
        return Crop(cropRect);
    }

    /// <summary>
    /// Saves the current working image to a file.
    /// </summary>
    /// <param name="filePath">The file path to save to.</param>
    /// <param name="format">The image format (default: PNG).</param>
    /// <param name="quality">Quality for lossy formats (0-100).</param>
    /// <returns>True if saved successfully.</returns>
    public bool SaveImage(string filePath, SKEncodedImageFormat format = SKEncodedImageFormat.Png, int quality = 95)
    {
        if (_workingBitmap is null || string.IsNullOrWhiteSpace(filePath))
            return false;

        try
        {
            using var image = SKImage.FromBitmap(_workingBitmap);
            using var data = image.Encode(format, quality);
            
            if (data is null)
                return false;

            using var stream = File.OpenWrite(filePath);
            data.SaveTo(stream);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Saves the current working image, overwriting the original file.
    /// </summary>
    /// <returns>True if saved successfully.</returns>
    public bool SaveOverwrite()
    {
        if (string.IsNullOrWhiteSpace(CurrentImagePath))
            return false;

        var format = GetFormatFromExtension(CurrentImagePath);
        return SaveImage(CurrentImagePath, format);
    }

    /// <summary>
    /// Saves the current working image as a new file with auto-generated name.
    /// </summary>
    /// <returns>The new file path if saved successfully, null otherwise.</returns>
    public string? SaveAsNew()
    {
        if (_workingBitmap is null || string.IsNullOrWhiteSpace(CurrentImagePath))
            return null;

        var directory = Path.GetDirectoryName(CurrentImagePath);
        var fileName = Path.GetFileNameWithoutExtension(CurrentImagePath);
        var extension = Path.GetExtension(CurrentImagePath);

        if (string.IsNullOrEmpty(directory))
            return null;

        // Generate unique filename with suffix
        var newPath = GenerateUniqueFilePath(directory, fileName, extension);
        var format = GetFormatFromExtension(newPath);

        if (SaveImage(newPath, format))
        {
            return newPath;
        }

        return null;
    }

    private static string GenerateUniqueFilePath(string directory, string baseName, string extension)
    {
        var counter = 1;
        string newPath;

        do
        {
            var suffix = $"_edited_{counter:D3}";
            newPath = Path.Combine(directory, $"{baseName}{suffix}{extension}");
            counter++;
        }
        while (File.Exists(newPath) && counter < 1000);

        return newPath;
    }

    private static SKEncodedImageFormat GetFormatFromExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            ".bmp" => SKEncodedImageFormat.Bmp,
            ".gif" => SKEncodedImageFormat.Gif,
            _ => SKEncodedImageFormat.Png
        };
    }

    protected virtual void OnImageChanged()
    {
        ImageChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _originalBitmap?.Dispose();
            _workingBitmap?.Dispose();
            _originalBitmap = null;
            _workingBitmap = null;
        }

        _disposed = true;
    }
}
