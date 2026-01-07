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
    private float _zoomLevel = 1f;
    private float _panX;
    private float _panY;
    private bool _isFitMode = true;
    private int _imageDpi = 72;

    private const float MinZoom = 0.1f;
    private const float MaxZoom = 10f;
    private const float ZoomStep = 0.1f;

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
    /// Gets or sets the current zoom level (1.0 = 100%).
    /// </summary>
    public float ZoomLevel
    {
        get => _zoomLevel;
        set
        {
            _zoomLevel = Math.Clamp(value, MinZoom, MaxZoom);
            _isFitMode = false;
            OnZoomChanged();
        }
    }

    /// <summary>
    /// Gets the zoom level as a percentage (0-1000).
    /// </summary>
    public int ZoomPercentage => (int)Math.Round(_zoomLevel * 100);

    /// <summary>
    /// Gets or sets the horizontal pan offset.
    /// </summary>
    public float PanX
    {
        get => _panX;
        set => _panX = value;
    }

    /// <summary>
    /// Gets or sets the vertical pan offset.
    /// </summary>
    public float PanY
    {
        get => _panY;
        set => _panY = value;
    }

    /// <summary>
    /// Gets or sets whether the image should fit to the canvas.
    /// </summary>
    public bool IsFitMode
    {
        get => _isFitMode;
        set
        {
            _isFitMode = value;
            if (value)
            {
                _panX = 0;
                _panY = 0;
            }
            OnZoomChanged();
        }
    }

    /// <summary>
    /// Gets the image DPI (dots per inch).
    /// </summary>
    public int ImageDpi => _imageDpi;

    /// <summary>
    /// Gets the file size in bytes.
    /// </summary>
    public long FileSizeBytes { get; private set; }

    /// <summary>
    /// Event raised when the image is modified.
    /// </summary>
    public event EventHandler? ImageChanged;

    /// <summary>
    /// Event raised when zoom level changes.
    /// </summary>
    public event EventHandler? ZoomChanged;

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

            // Get file size
            var fileInfo = new FileInfo(filePath);
            FileSizeBytes = fileInfo.Length;

            using var stream = File.OpenRead(filePath);
            
            // Try to get DPI from codec
            using var codec = SKCodec.Create(stream);
            if (codec is not null)
            {
                // Reset stream position
                stream.Position = 0;
            }

            _originalBitmap = SKBitmap.Decode(stream);

            if (_originalBitmap is null)
                return false;

            // Try to extract DPI (default to 72 if not available)
            _imageDpi = 72; // SkiaSharp doesn't directly expose DPI, would need EXIF parsing

            _workingBitmap = _originalBitmap.Copy();
            CurrentImagePath = filePath;
            
            // Reset zoom and pan
            ResetZoom();
            
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

    /// <summary>
    /// Renders the current image with zoom and pan support.
    /// </summary>
    public SKRect RenderWithZoom(SKCanvas canvas, float canvasWidth, float canvasHeight, SKColor backgroundColor)
    {
        canvas.Clear(backgroundColor);

        if (_workingBitmap is null)
            return SKRect.Empty;

        SKRect imageRect;

        if (_isFitMode)
        {
            imageRect = CalculateFitRect(canvasWidth, canvasHeight);
            // Update zoom level to reflect fit
            var fitScale = imageRect.Width / _workingBitmap.Width;
            _zoomLevel = fitScale;
        }
        else
        {
            // Calculate zoomed size
            var zoomedWidth = _workingBitmap.Width * _zoomLevel;
            var zoomedHeight = _workingBitmap.Height * _zoomLevel;

            // Center with pan offset
            var x = (canvasWidth - zoomedWidth) / 2f + _panX;
            var y = (canvasHeight - zoomedHeight) / 2f + _panY;

            imageRect = new SKRect(x, y, x + zoomedWidth, y + zoomedHeight);
        }

        canvas.DrawBitmap(_workingBitmap, imageRect);

        // Update crop tool with current image bounds and render overlay
        CropTool.SetImageBounds(imageRect);
        CropTool.Render(canvas, new SKRect(0, 0, canvasWidth, canvasHeight));

        return imageRect;
    }

    /// <summary>
    /// Zooms in by one step.
    /// </summary>
    public void ZoomIn()
    {
        ZoomLevel = _zoomLevel + ZoomStep;
    }

    /// <summary>
    /// Zooms out by one step.
    /// </summary>
    public void ZoomOut()
    {
        ZoomLevel = _zoomLevel - ZoomStep;
    }

    /// <summary>
    /// Sets zoom to fit the image in the canvas.
    /// </summary>
    public void ZoomToFit()
    {
        IsFitMode = true;
    }

    /// <summary>
    /// Sets zoom to 100% (actual size).
    /// </summary>
    public void ZoomToActual()
    {
        ZoomLevel = 1f;
        _panX = 0;
        _panY = 0;
    }

    /// <summary>
    /// Resets zoom and pan to defaults.
    /// </summary>
    public void ResetZoom()
    {
        _zoomLevel = 1f;
        _panX = 0;
        _panY = 0;
        _isFitMode = true;
        OnZoomChanged();
    }

    /// <summary>
    /// Pans the image by the specified delta.
    /// </summary>
    public void Pan(float deltaX, float deltaY)
    {
        if (_isFitMode) return;
        
        _panX += deltaX;
        _panY += deltaY;
    }

    /// <summary>
    /// Rotates the image 90 degrees clockwise.
    /// </summary>
    /// <returns>True if rotation was successful.</returns>
    public bool RotateRight()
    {
        if (_workingBitmap is null)
            return false;

        try
        {
            var rotated = new SKBitmap(_workingBitmap.Height, _workingBitmap.Width);
            using (var canvas = new SKCanvas(rotated))
            {
                canvas.Translate(rotated.Width, 0);
                canvas.RotateDegrees(90);
                canvas.DrawBitmap(_workingBitmap, 0, 0);
            }

            _workingBitmap.Dispose();
            _workingBitmap = rotated;
            OnImageChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Rotates the image 90 degrees counter-clockwise.
    /// </summary>
    /// <returns>True if rotation was successful.</returns>
    public bool RotateLeft()
    {
        if (_workingBitmap is null)
            return false;

        try
        {
            var rotated = new SKBitmap(_workingBitmap.Height, _workingBitmap.Width);
            using (var canvas = new SKCanvas(rotated))
            {
                canvas.Translate(0, rotated.Height);
                canvas.RotateDegrees(-90);
                canvas.DrawBitmap(_workingBitmap, 0, 0);
            }

            _workingBitmap.Dispose();
            _workingBitmap = rotated;
            OnImageChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Rotates the image 180 degrees.
    /// </summary>
    /// <returns>True if rotation was successful.</returns>
    public bool Rotate180()
    {
        if (_workingBitmap is null)
            return false;

        try
        {
            var rotated = new SKBitmap(_workingBitmap.Width, _workingBitmap.Height);
            using (var canvas = new SKCanvas(rotated))
            {
                canvas.Translate(rotated.Width, rotated.Height);
                canvas.RotateDegrees(180);
                canvas.DrawBitmap(_workingBitmap, 0, 0);
            }

            _workingBitmap.Dispose();
            _workingBitmap = rotated;
            OnImageChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Flips the image horizontally (mirror).
    /// </summary>
    /// <returns>True if flip was successful.</returns>
    public bool FlipHorizontal()
    {
        if (_workingBitmap is null)
            return false;

        try
        {
            var flipped = new SKBitmap(_workingBitmap.Width, _workingBitmap.Height);
            using (var canvas = new SKCanvas(flipped))
            {
                canvas.Translate(flipped.Width, 0);
                canvas.Scale(-1, 1);
                canvas.DrawBitmap(_workingBitmap, 0, 0);
            }

            _workingBitmap.Dispose();
            _workingBitmap = flipped;
            OnImageChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Flips the image vertically.
    /// </summary>
    /// <returns>True if flip was successful.</returns>
    public bool FlipVertical()
    {
        if (_workingBitmap is null)
            return false;

        try
        {
            var flipped = new SKBitmap(_workingBitmap.Width, _workingBitmap.Height);
            using (var canvas = new SKCanvas(flipped))
            {
                canvas.Translate(0, flipped.Height);
                canvas.Scale(1, -1);
                canvas.DrawBitmap(_workingBitmap, 0, 0);
            }

            _workingBitmap.Dispose();
            _workingBitmap = flipped;
            OnImageChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected virtual void OnZoomChanged()
    {
        ZoomChanged?.Invoke(this, EventArgs.Empty);
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
