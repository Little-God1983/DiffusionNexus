using DiffusionNexus.UI.Services;
using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Platform-independent image editor core using SkiaSharp.
/// Supports layer-based editing with compositing.
/// </summary>
public class ImageEditorCore : IDisposable
{
    private SKBitmap? _originalBitmap;
    private SKBitmap? _workingBitmap;
    private SKBitmap? _previewBitmap;
    private LayerStack? _layers;
    private bool _isPreviewActive;
    private bool _disposed;
    private float _zoomLevel = 1f;
    private float _panX;
    private float _panY;
    private bool _isFitMode = true;
    private int _imageDpi = 72;
    private readonly object _bitmapLock = new();
    private bool _isLayerMode;

    private const float MinZoom = 0.1f;
    private const float MaxZoom = 10f;
    private const float ZoomStep = 0.1f;

    /// <summary>
    /// Gets the crop tool instance.
    /// </summary>
    public CropTool CropTool { get; } = new();

    /// <summary>
    /// Gets the drawing tool instance.
    /// </summary>
    public DrawingTool DrawingTool { get; } = new();

    /// <summary>
    /// Gets the shape tool instance.
    /// </summary>
    public ShapeTool ShapeTool { get; } = new();

    /// <summary>
    /// Gets the layer stack for layer-based editing.
    /// </summary>
    public LayerStack? Layers => _layers;

    /// <summary>
    /// Gets or sets whether layer mode is enabled.
    /// </summary>
    public bool IsLayerMode
    {
        get => _isLayerMode;
        set
        {
            if (_isLayerMode != value)
            {
                _isLayerMode = value;
                if (value && _layers == null && _workingBitmap != null)
                {
                    EnableLayerMode();
                }
                LayerModeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Gets the current working bitmap width.
    /// </summary>
    public int Width => _isLayerMode ? (_layers?.Width ?? 0) : (_workingBitmap?.Width ?? 0);

    /// <summary>
    /// Gets the current working bitmap height.
    /// </summary>
    public int Height => _isLayerMode ? (_layers?.Height ?? 0) : (_workingBitmap?.Height ?? 0);

    /// <summary>
    /// Gets whether an image is currently loaded.
    /// </summary>
    public bool HasImage => _isLayerMode ? (_layers?.Count > 0) : (_workingBitmap is not null);

    /// <summary>
    /// Gets whether a preview is currently active.
    /// </summary>
    public bool IsPreviewActive => _isPreviewActive;

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
    /// Event raised when layer mode is toggled.
    /// </summary>
    public event EventHandler? LayerModeChanged;

    /// <summary>
    /// Event raised when layers change (added, removed, reordered).
    /// </summary>
    public event EventHandler? LayersChanged;

    #region Layer Operations

    /// <summary>
    /// Enables layer mode, converting the current working bitmap to a layer stack.
    /// The initial layer is named after the current image file.
    /// </summary>
    public void EnableLayerMode()
    {
        if (_layers != null || _workingBitmap == null) return;

        // Use the filename for the initial layer name
        var layerName = !string.IsNullOrEmpty(CurrentImagePath) 
            ? Path.GetFileNameWithoutExtension(CurrentImagePath) 
            : "Background";

        _layers = new LayerStack(_workingBitmap.Width, _workingBitmap.Height);
        _layers.AddLayerFromBitmap(_workingBitmap, layerName);
        _layers.ContentChanged += OnLayersContentChanged;
        _layers.LayersChanged += OnLayersCollectionChanged;
        _isLayerMode = true;
        OnImageChanged();
        LayerModeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Disables layer mode, flattening all layers to a single bitmap.
    /// </summary>
    public void DisableLayerMode()
    {
        if (_layers == null) return;

        var flattened = _layers.Flatten();
        if (flattened != null)
        {
            _workingBitmap?.Dispose();
            _workingBitmap = flattened;
        }

        _layers.ContentChanged -= OnLayersContentChanged;
        _layers.LayersChanged -= OnLayersCollectionChanged;
        _layers.Dispose();
        _layers = null;
        _isLayerMode = false;
        OnImageChanged();
        LayerModeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Flattens all layers into a single layer while keeping layer mode active.
    /// </summary>
    public void FlattenAllLayers()
    {
        if (_layers == null || _layers.Count == 0) return;

        var flattened = _layers.Flatten();
        if (flattened == null) return;

        // Get the name of the bottom layer or use "Flattened"
        var layerName = _layers.Count > 0 ? _layers[0].Name : "Flattened";
        if (_layers.Count > 1)
        {
            layerName = "Flattened";
        }

        // Clear all layers
        _layers.ContentChanged -= OnLayersContentChanged;
        _layers.LayersChanged -= OnLayersCollectionChanged;
        _layers.Dispose();

        // Create new layer stack with the flattened image
        _layers = new LayerStack(flattened.Width, flattened.Height);
        _layers.AddLayerFromBitmap(flattened, layerName);
        _layers.ContentChanged += OnLayersContentChanged;
        _layers.LayersChanged += OnLayersCollectionChanged;

        // Clean up
        flattened.Dispose();

        OnImageChanged();
        LayersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adds a new empty layer at the top of the stack.
    /// </summary>
    /// <param name="name">Optional layer name.</param>
    /// <returns>The newly created layer, or null if not in layer mode.</returns>
    public Layer? AddLayer(string? name = null)
    {
        if (!_isLayerMode || _layers == null) return null;
        return _layers.AddLayer(name);
    }

    /// <summary>
    /// Adds a layer from a bitmap.
    /// </summary>
    /// <param name="bitmap">Source bitmap.</param>
    /// <param name="name">Optional layer name.</param>
    /// <returns>The newly created layer, or null if not in layer mode.</returns>
    public Layer? AddLayerFromBitmap(SKBitmap bitmap, string? name = null)
    {
        if (!_isLayerMode || _layers == null) return null;
        return _layers.AddLayerFromBitmap(bitmap, name);
    }

    /// <summary>
    /// Removes a layer from the stack.
    /// </summary>
    /// <param name="layer">Layer to remove.</param>
    /// <returns>True if removed successfully.</returns>
    public bool RemoveLayer(Layer layer)
    {
        if (!_isLayerMode || _layers == null) return false;
        return _layers.RemoveLayer(layer);
    }

    /// <summary>
    /// Duplicates a layer.
    /// </summary>
    /// <param name="layer">Layer to duplicate.</param>
    /// <returns>The duplicated layer, or null if failed.</returns>
    public Layer? DuplicateLayer(Layer layer)
    {
        if (!_isLayerMode || _layers == null) return null;
        return _layers.DuplicateLayer(layer);
    }

    /// <summary>
    /// Moves a layer up in the stack.
    /// </summary>
    /// <param name="layer">Layer to move.</param>
    /// <returns>True if moved successfully.</returns>
    public bool MoveLayerUp(Layer layer)
    {
        if (!_isLayerMode || _layers == null) return false;
        return _layers.MoveLayerUp(layer);
    }

    /// <summary>
    /// Moves a layer down in the stack.
    /// </summary>
    /// <param name="layer">Layer to move.</param>
    /// <returns>True if moved successfully.</returns>
    public bool MoveLayerDown(Layer layer)
    {
        if (!_isLayerMode || _layers == null) return false;
        return _layers.MoveLayerDown(layer);
    }

    /// <summary>
    /// Merges the specified layer with the layer below it.
    /// </summary>
    /// <param name="layer">Layer to merge down.</param>
    /// <returns>True if merged successfully.</returns>
    public bool MergeLayerDown(Layer layer)
    {
        if (!_isLayerMode || _layers == null) return false;
        return _layers.MergeDown(layer);
    }

    /// <summary>
    /// Merges all visible layers.
    /// </summary>
    public void MergeVisibleLayers()
    {
        if (!_isLayerMode || _layers == null) return;
        _layers.MergeVisible();
    }

    /// <summary>
    /// Flattens all layers to a single bitmap.
    /// </summary>
    /// <returns>The flattened bitmap, or null if not in layer mode.</returns>
    public SKBitmap? FlattenLayers()
    {
        if (!_isLayerMode || _layers == null) return null;
        return _layers.Flatten();
    }

    /// <summary>
    /// Gets or sets the active layer for editing.
    /// </summary>
    public Layer? ActiveLayer
    {
        get => _layers?.ActiveLayer;
        set
        {
            if (_layers != null)
            {
                _layers.ActiveLayer = value;
            }
        }
    }

    private void OnLayersContentChanged(object? sender, EventArgs e)
    {
        OnImageChanged();
    }

    private void OnLayersCollectionChanged(object? sender, EventArgs e)
    {
        LayersChanged?.Invoke(this, EventArgs.Empty);
        OnImageChanged();
    }

    #endregion Layer Operations

    /// <summary>
    /// Loads an image from the specified file path.
    /// Automatically enables layer mode with the image as the first layer.
    /// </summary>
    /// <param name="filePath">Path to the image file.</param>
    /// <returns>True if the image was loaded successfully.</returns>
    public bool LoadImage(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            // Clear preview without raising event since we'll raise it after loading
            ClearPreview(raiseEvent: false);
            
            // Dispose existing layers if any
            if (_layers != null)
            {
                _layers.ContentChanged -= OnLayersContentChanged;
                _layers.LayersChanged -= OnLayersCollectionChanged;
                _layers.Dispose();
                _layers = null;
            }
            
            _originalBitmap?.Dispose();
            _workingBitmap?.Dispose();

            // Get file size
            var fileInfo = new FileInfo(filePath);
            FileSizeBytes = fileInfo.Length;

            using var stream = File.OpenRead(filePath);
            using var codec = SKCodec.Create(stream);
            if (codec is not null)
                stream.Position = 0;

            _originalBitmap = SKBitmap.Decode(stream);
            if (_originalBitmap is null)
                return false;

            // Try to extract DPI (default to 72 if not available)
            _imageDpi = 72; // SkiaSharp doesn't directly expose DPI, would need EXIF parsing

            _workingBitmap = _originalBitmap.Copy();
            CurrentImagePath = filePath;
            ResetZoom();
            
            // Auto-enable layer mode with the image as the first layer
            EnableLayerMode();
            
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
    /// Automatically enables layer mode with the image as the first layer.
    /// </summary>
    /// <param name="imageData">The image data as bytes.</param>
    /// <returns>True if the image was loaded successfully.</returns>
    public bool LoadImage(byte[] imageData)
    {
        if (imageData is null || imageData.Length == 0)
            return false;

        try
        {
            // Clear preview without raising event since we'll raise it after loading
            ClearPreview(raiseEvent: false);
            
            // Dispose existing layers if any
            if (_layers != null)
            {
                _layers.ContentChanged -= OnLayersContentChanged;
                _layers.LayersChanged -= OnLayersCollectionChanged;
                _layers.Dispose();
                _layers = null;
            }
            
            _originalBitmap?.Dispose();
            _workingBitmap?.Dispose();

            _originalBitmap = SKBitmap.Decode(imageData);
            if (_originalBitmap is null)
                return false;

            _workingBitmap = _originalBitmap.Copy();
            CurrentImagePath = null;
            
            // Auto-enable layer mode with the image as the first layer
            EnableLayerMode();
            
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
        if (canvas is null)
            return;
            
        SKBitmap? bitmap;
        lock (_bitmapLock)
        {
            bitmap = _isPreviewActive && _previewBitmap is not null ? _previewBitmap : _workingBitmap;
            if (bitmap is null)
                return;
            
            canvas.DrawBitmap(bitmap, destRect);
        }
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

        lock (_bitmapLock)
        {
            var bitmapToRender = _isPreviewActive && _previewBitmap is not null ? _previewBitmap : _workingBitmap;
            if (bitmapToRender is null)
                return SKRect.Empty;

            var imageRect = CalculateFitRectInternal(bitmapToRender, canvasWidth, canvasHeight);
            canvas.DrawBitmap(bitmapToRender, imageRect);

            // Update crop tool with current image bounds and render overlay
            CropTool.SetImageBounds(imageRect);
            CropTool.Render(canvas, new SKRect(0, 0, canvasWidth, canvasHeight));

            // Update drawing tool with current image bounds and render overlay
            DrawingTool.SetImageBounds(imageRect);
            DrawingTool.Render(canvas);

            return imageRect;
        }
    }

    /// <summary>
    /// Calculates the rectangle to fit the image within the given bounds while maintaining aspect ratio.
    /// </summary>
    public SKRect CalculateFitRect(float containerWidth, float containerHeight)
    {
        lock (_bitmapLock)
        {
            if (_workingBitmap is null)
                return SKRect.Empty;

            return CalculateFitRectInternal(_workingBitmap, containerWidth, containerHeight);
        }
    }

    /// <summary>
    /// Internal method to calculate fit rect without locking (caller must hold lock).
    /// </summary>
    private static SKRect CalculateFitRectInternal(SKBitmap bitmap, float containerWidth, float containerHeight)
    {
        var imageWidth = (float)bitmap.Width;
        var imageHeight = (float)bitmap.Height;

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
    /// When in layer mode, recreates the layer stack from the original bitmap.
    /// </summary>
    public void ResetToOriginal()
    {
        if (_originalBitmap is null)
            return;

        ClearPreview();

        // If in layer mode, dispose the current layer stack
        if (_isLayerMode && _layers != null)
        {
            _layers.ContentChanged -= OnLayersContentChanged;
            _layers.LayersChanged -= OnLayersCollectionChanged;
            _layers.Dispose();
            _layers = null;
        }

        // Reset working bitmap from original
        _workingBitmap?.Dispose();
        _workingBitmap = _originalBitmap.Copy();

        // If was in layer mode, recreate the layer stack from the reset working bitmap
        if (_isLayerMode)
        {
            var layerName = !string.IsNullOrEmpty(CurrentImagePath) 
                ? Path.GetFileNameWithoutExtension(CurrentImagePath) 
                : "Background";

            _layers = new LayerStack(_workingBitmap.Width, _workingBitmap.Height);
            _layers.AddLayerFromBitmap(_workingBitmap, layerName);
            _layers.ContentChanged += OnLayersContentChanged;
            _layers.LayersChanged += OnLayersCollectionChanged;
            LayersChanged?.Invoke(this, EventArgs.Empty);
        }

        OnImageChanged();
    }

    /// <summary>
    /// Clears the current image.
    /// </summary>
    public void Clear()
    {
        ClearPreview();
        _originalBitmap?.Dispose();
        _workingBitmap?.Dispose();
        _originalBitmap = null;
        _workingBitmap = null;
        CurrentImagePath = null;
        OnImageChanged();
    }

    /// <summary>
    /// Gets the working bitmap for external rendering.
    /// Returns preview bitmap if preview is active.
    /// </summary>
    public SKBitmap? GetWorkingBitmap()
    {
        lock (_bitmapLock)
        {
            return _isPreviewActive && _previewBitmap is not null ? _previewBitmap : _workingBitmap;
        }
    }

    /// <summary>
    /// Gets the actual working bitmap without preview.
    /// </summary>
    public SKBitmap? GetActualWorkingBitmap()
    {
        lock (_bitmapLock)
        {
            return _workingBitmap;
        }
    }

    /// <summary>
    /// Crops the image to the specified rectangle in image coordinates.
    /// When in layer mode, crops all layers.
    /// </summary>
    /// <param name="cropRect">The crop rectangle in image pixel coordinates.</param>
    /// <returns>True if the crop was successful.</returns>
    public bool Crop(SKRectI cropRect)
    {
        // Get the current image dimensions
        int currentWidth, currentHeight;
        if (_isLayerMode && _layers != null && _layers.Count > 0)
        {
            currentWidth = _layers.Width;
            currentHeight = _layers.Height;
        }
        else if (_workingBitmap is not null)
        {
            currentWidth = _workingBitmap.Width;
            currentHeight = _workingBitmap.Height;
        }
        else
        {
            return false;
        }

        // Validate crop rectangle
        if (cropRect.Width <= 0 || cropRect.Height <= 0)
            return false;

        // Clamp to image bounds
        var clampedRect = new SKRectI(
            Math.Clamp(cropRect.Left, 0, currentWidth),
            Math.Clamp(cropRect.Top, 0, currentHeight),
            Math.Clamp(cropRect.Right, 0, currentWidth),
            Math.Clamp(cropRect.Bottom, 0, currentHeight));

        if (clampedRect.Width <= 0 || clampedRect.Height <= 0)
            return false;

        try
        {
            if (_isLayerMode && _layers != null)
            {
                // Crop all layers
                _layers.CropAll(clampedRect);
            }
            
            // Also crop the working bitmap if it exists
            if (_workingBitmap is not null)
            {
                var croppedBitmap = new SKBitmap(clampedRect.Width, clampedRect.Height);
                using (var canvas = new SKCanvas(croppedBitmap))
                {
                    var srcRect = new SKRect(clampedRect.Left, clampedRect.Top, clampedRect.Right, clampedRect.Bottom);
                    var destRect = new SKRect(0, 0, clampedRect.Width, clampedRect.Height);
                    canvas.DrawBitmap(_workingBitmap, srcRect, destRect);
                }
                _workingBitmap.Dispose();
                _workingBitmap = croppedBitmap;
            }

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
        // Get current dimensions
        int width, height;
        if (_isLayerMode && _layers != null && _layers.Count > 0)
        {
            width = _layers.Width;
            height = _layers.Height;
        }
        else if (_workingBitmap is not null)
        {
            width = _workingBitmap.Width;
            height = _workingBitmap.Height;
        }
        else
        {
            return false;
        }

        if (!CropTool.HasCropRegion)
            return false;

        var cropRect = CropTool.GetImageCropRect(width, height);
        return Crop(cropRect);
    }



    /// <summary>
    /// Saves the current working image to a file.
    /// If in layer mode, flattens all layers before saving.
    /// </summary>
    /// <param name="filePath">The file path to save to.</param>
    /// <param name="format">The image format (default: PNG).</param>
    /// <param name="quality">Quality for lossy formats (0-100).</param>
    /// <returns>True if saved successfully.</returns>
    public bool SaveImage(string filePath, SKEncodedImageFormat format = SKEncodedImageFormat.Png, int quality = 95)
    {
        FileLogger.LogEntry($"filePath={filePath}, format={format}, quality={quality}");
        
        // Get the bitmap to save - flatten layers if in layer mode
        SKBitmap? bitmapToSave = null;
        bool needsDispose = false;
        
        if (_isLayerMode && _layers != null && _layers.Count > 0)
        {
            FileLogger.Log("Layer mode active, flattening layers for save...");
            bitmapToSave = _layers.Flatten();
            needsDispose = true; // We own this flattened bitmap
        }
        else
        {
            bitmapToSave = _workingBitmap;
        }
        
        if (bitmapToSave is null)
        {
            FileLogger.LogWarning("No bitmap to save (working bitmap is null and no layers)");
            FileLogger.LogExit("false");
            return false;
        }
        
        if (string.IsNullOrWhiteSpace(filePath))
        {
            FileLogger.LogWarning("filePath is null or whitespace, cannot save");
            if (needsDispose) bitmapToSave.Dispose();
            FileLogger.LogExit("false");
            return false;
        }

        FileLogger.Log($"Bitmap to save size: {bitmapToSave.Width}x{bitmapToSave.Height}");

        try
        {
            // Ensure the directory exists
            var directory = Path.GetDirectoryName(filePath);
            FileLogger.Log($"Directory: {directory ?? "(null)"}");
            
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                FileLogger.Log($"Creating directory: {directory}");
                Directory.CreateDirectory(directory);
            }

            FileLogger.Log("Creating SKImage from bitmap...");
            using var image = SKImage.FromBitmap(bitmapToSave);
            if (image is null)
            {
                FileLogger.LogError("SKImage.FromBitmap returned null");
                if (needsDispose) bitmapToSave.Dispose();
                FileLogger.LogExit("false");
                return false;
            }
            
            FileLogger.Log($"Encoding image as {format}...");
            using var data = image.Encode(format, quality);
            
            
            if (data is null)
            {
                FileLogger.LogError("image.Encode returned null");
                if (needsDispose) bitmapToSave.Dispose();
                FileLogger.LogExit("false");
                return false;
            }

            FileLogger.Log($"Encoded data size: {data.Size} bytes");
            FileLogger.Log($"Opening file stream for writing: {filePath}");
            
            // Use FileMode.Create to truncate existing file or create new
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            FileLogger.Log("Saving data to stream...");
            data.SaveTo(stream);
            
            // Clean up flattened bitmap if we created one
            if (needsDispose) bitmapToSave.Dispose();
            
            FileLogger.Log("Save completed successfully");
            FileLogger.LogExit("true");
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.LogError($"Exception during save to {filePath}", ex);
            FileLogger.LogExit("false");
            return false;
        }
    }

    /// <summary>
    /// Saves the current working image, overwriting the original file.
    /// </summary>
    /// <returns>True if saved successfully.</returns>
    public bool SaveOverwrite()
    {
        FileLogger.LogEntry($"CurrentImagePath={CurrentImagePath ?? "(null)"}");
        
        if (string.IsNullOrWhiteSpace(CurrentImagePath))
        {
            FileLogger.LogWarning("CurrentImagePath is null or whitespace");
            FileLogger.LogExit("false");
            return false;
        }

        var format = GetFormatFromExtension(CurrentImagePath);
        FileLogger.Log($"Detected format: {format}");
        var result = SaveImage(CurrentImagePath, format);
        FileLogger.LogExit(result.ToString());
        return result;
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
    /// Supports both single-bitmap and layer modes.
    /// </summary>
    public SKRect RenderWithZoom(SKCanvas canvas, float canvasWidth, float canvasHeight, SKColor backgroundColor)
    {
        canvas.Clear(backgroundColor);

        lock (_bitmapLock)
        {
            // Determine what to render
            int imageWidth, imageHeight;
            if (_isLayerMode && _layers != null && _layers.Count > 0)
            {
                imageWidth = _layers.Width;
                imageHeight = _layers.Height;
            }
            else
            {
                var bitmapToRender = _isPreviewActive && _previewBitmap is not null ? _previewBitmap : _workingBitmap;
                if (bitmapToRender is null)
                    return SKRect.Empty;
                imageWidth = bitmapToRender.Width;
                imageHeight = bitmapToRender.Height;
            }

            SKRect imageRect;

            if (_isFitMode)
            {
                imageRect = CalculateFitRectInternal(imageWidth, imageHeight, canvasWidth, canvasHeight);
                // Update zoom level to reflect fit
                var fitScale = imageRect.Width / imageWidth;
                _zoomLevel = fitScale;
            }
            else
            {
                // Calculate zoomed size
                var zoomedWidth = imageWidth * _zoomLevel;
                var zoomedHeight = imageHeight * _zoomLevel;

                // Center with pan offset
                var x = (canvasWidth - zoomedWidth) / 2f + _panX;
                var y = (canvasHeight - zoomedHeight) / 2f + _panY;

                imageRect = new SKRect(x, y, x + zoomedWidth, y + zoomedHeight);
            }

            // Render based on mode
            if (_isLayerMode && _layers != null)
            {
                LayerCompositor.CompositeToCanvas(canvas, _layers, imageRect);
            }
            else
            {
                var bitmapToRender = _isPreviewActive && _previewBitmap is not null ? _previewBitmap : _workingBitmap;
                if (bitmapToRender != null)
                {
                    canvas.DrawBitmap(bitmapToRender, imageRect);
                }
            }

            // Update crop tool with current image bounds and render overlay
            CropTool.SetImageBounds(imageRect);
            CropTool.Render(canvas, new SKRect(0, 0, canvasWidth, canvasHeight));

            // Update drawing tool with current image bounds and render overlay
            DrawingTool.SetImageBounds(imageRect);
            DrawingTool.Render(canvas);

            // Update shape tool with current image bounds and render overlay
            ShapeTool.SetImageBounds(imageRect);
            ShapeTool.Render(canvas);

            return imageRect;
        }
    }

    /// <summary>
    /// Internal method to calculate fit rect from dimensions.
    /// </summary>
    private static SKRect CalculateFitRectInternal(int imageWidth, int imageHeight, float containerWidth, float containerHeight)
    {
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
    /// Increases the zoom level to zoom in.
    /// </summary>
    public void ZoomIn() => ZoomLevel += ZoomStep;

    /// <summary>
    /// Decreases the zoom level to zoom out.
    /// </summary>
    public void ZoomOut() => ZoomLevel -= ZoomStep;

    /// <summary>
    /// Sets the zoom level to fit the image within the canvas.
    /// </summary>
    public void ZoomToFit() => IsFitMode = true;

    /// <summary>
    /// Sets fit mode with a pre-calculated zoom level.
    /// Used when the caller knows the canvas dimensions and can calculate the fit zoom.
    /// </summary>
    /// <param name="fitZoom">The calculated zoom level for fit mode.</param>
    public void SetFitModeWithZoom(float fitZoom)
    {
        _zoomLevel = Math.Clamp(fitZoom, MinZoom, MaxZoom);
        _isFitMode = true;
        _panX = 0;
        _panY = 0;
        OnZoomChanged();
    }

    /// <summary>
    /// Resets the zoom level to 100% and pans to the original position.
    /// </summary>
    public void ZoomToActual()
    {
        ZoomLevel = 1f;
        _panX = 0;
        _panY = 0;
    }

    /// <summary>
    /// Resets the zoom level, pan offsets, and fit mode to their initial states.
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
    /// Pans the image by the specified delta values.
    /// </summary>
    /// <param name="deltaX">The delta value for the X axis.</param>
    /// <param name="deltaY">The delta value for the Y axis.</param>
    public void Pan(float deltaX, float deltaY)
    {
        if (_isFitMode) return;
        _panX += deltaX;
        _panY += deltaY;
    }

    #region Transform Operations

    /// <summary>
    /// Rotates the image 90 degrees clockwise.
    /// When in layer mode, rotates all layers.
    /// </summary>
    public bool RotateRight()
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (targetBitmap is null) return false;
        try
        {
            var rotated = new SKBitmap(targetBitmap.Height, targetBitmap.Width);
            using (var canvas = new SKCanvas(rotated))
            {
                canvas.Translate(rotated.Width, 0);
                canvas.RotateDegrees(90);
                canvas.DrawBitmap(targetBitmap, 0, 0);
            }
            
            if (_isLayerMode && _layers != null)
            {
                // Rotate all layers
                _layers.TransformAll(layer => RotateBitmapRight(layer.Bitmap));
            }
            else
            {
                _workingBitmap?.Dispose();
                _workingBitmap = rotated;
            }
            
            OnImageChanged();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Rotates the image 90 degrees counter-clockwise.
    /// When in layer mode, rotates all layers.
    /// </summary>
    public bool RotateLeft()
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (targetBitmap is null) return false;
        try
        {
            var rotated = new SKBitmap(targetBitmap.Height, targetBitmap.Width);
            using (var canvas = new SKCanvas(rotated))
            {
                canvas.Translate(0, rotated.Height);
                canvas.RotateDegrees(-90);
                canvas.DrawBitmap(targetBitmap, 0, 0);
            }
            
            if (_isLayerMode && _layers != null)
            {
                // Rotate all layers
                _layers.TransformAll(layer => RotateBitmapLeft(layer.Bitmap));
            }
            else
            {
                _workingBitmap?.Dispose();
                _workingBitmap = rotated;
            }
            
            OnImageChanged();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Rotates the image 180 degrees.
    /// When in layer mode, rotates all layers.
    /// </summary>
    public bool Rotate180()
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (targetBitmap is null) return false;
        try
        {
            var rotated = new SKBitmap(targetBitmap.Width, targetBitmap.Height);
            using (var canvas = new SKCanvas(rotated))
            {
                canvas.Translate(rotated.Width, rotated.Height);
                canvas.RotateDegrees(180);
                canvas.DrawBitmap(targetBitmap, 0, 0);
            }
            
            if (_isLayerMode && _layers != null)
            {
                // Rotate all layers
                _layers.TransformAll(layer => RotateBitmap180(layer.Bitmap));
            }
            else
            {
                _workingBitmap?.Dispose();
                _workingBitmap = rotated;
            }
            
            OnImageChanged();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Flips the image horizontally (mirror).
    /// When in layer mode, flips all layers.
    /// </summary>
    public bool FlipHorizontal()
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (targetBitmap is null) return false;
        try
        {
            var flipped = new SKBitmap(targetBitmap.Width, targetBitmap.Height);
            using (var canvas = new SKCanvas(flipped))
            {
                canvas.Translate(flipped.Width, 0);
                canvas.Scale(-1, 1);
                canvas.DrawBitmap(targetBitmap, 0, 0);
            }
            
            if (_isLayerMode && _layers != null)
            {
                // Flip all layers
                _layers.TransformAll(layer => FlipBitmapHorizontal(layer.Bitmap));
            }
            else
            {
                _workingBitmap?.Dispose();
                _workingBitmap = flipped;
            }
            
            OnImageChanged();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Flips the image vertically.
    /// When in layer mode, flips all layers.
    /// </summary>
    public bool FlipVertical()
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (targetBitmap is null) return false;
        try
        {
            var flipped = new SKBitmap(targetBitmap.Width, targetBitmap.Height);
            using (var canvas = new SKCanvas(flipped))
            {
                canvas.Translate(0, flipped.Height);
                canvas.Scale(1, -1);
                canvas.DrawBitmap(targetBitmap, 0, 0);
            }
            
            if (_isLayerMode && _layers != null)
            {
                // Flip all layers
                _layers.TransformAll(layer => FlipBitmapVertical(layer.Bitmap));
            }
            else
            {
                _workingBitmap?.Dispose();
                _workingBitmap = flipped;
            }
            
            OnImageChanged();
            return true;
        }
        catch { return false; }
    }

    // Helper methods for bitmap transformations
    private static SKBitmap? RotateBitmapRight(SKBitmap? source)
    {
        if (source is null) return null;
        var rotated = new SKBitmap(source.Height, source.Width);
        using var canvas = new SKCanvas(rotated);
        canvas.Translate(rotated.Width, 0);
        canvas.RotateDegrees(90);
        canvas.DrawBitmap(source, 0, 0);
        return rotated;
    }

    private static SKBitmap? RotateBitmapLeft(SKBitmap? source)
    {
        if (source is null) return null;
        var rotated = new SKBitmap(source.Height, source.Width);
        using var canvas = new SKCanvas(rotated);
        canvas.Translate(0, rotated.Height);
        canvas.RotateDegrees(-90);
        canvas.DrawBitmap(source, 0, 0);
        return rotated;
    }

    private static SKBitmap? RotateBitmap180(SKBitmap? source)
    {
        if (source is null) return null;
        var rotated = new SKBitmap(source.Width, source.Height);
        using var canvas = new SKCanvas(rotated);
        canvas.Translate(rotated.Width, rotated.Height);
        canvas.RotateDegrees(180);
        canvas.DrawBitmap(source, 0, 0);
        return rotated;
    }

    private static SKBitmap? FlipBitmapHorizontal(SKBitmap? source)
    {
        if (source is null) return null;
        var flipped = new SKBitmap(source.Width, source.Height);
        using var canvas = new SKCanvas(flipped);
        canvas.Translate(flipped.Width, 0);
        canvas.Scale(-1, 1);
        canvas.DrawBitmap(source, 0, 0);
        return flipped;
    }

    private static SKBitmap? FlipBitmapVertical(SKBitmap? source)
    {
        if (source is null) return null;
        var flipped = new SKBitmap(source.Width, source.Height);
        using var canvas = new SKCanvas(flipped);
        canvas.Translate(0, flipped.Height);
        canvas.Scale(1, -1);
        canvas.DrawBitmap(source, 0, 0);
        return flipped;
    }

    #endregion

    #region Color Balance

    /// <summary>
    /// Gets the bitmap to apply operations to (active layer bitmap when in layer mode, otherwise working bitmap).
    /// </summary>
    private SKBitmap? GetOperationTargetBitmap()
    {
        if (_isLayerMode && _layers?.ActiveLayer?.Bitmap != null)
        {
            return _layers.ActiveLayer.Bitmap;
        }
        return _workingBitmap;
    }

    /// <summary>
    /// Replaces the operation target bitmap with a new one.
    /// </summary>
    private void SetOperationTargetBitmap(SKBitmap newBitmap)
    {
        if (_isLayerMode && _layers?.ActiveLayer != null)
        {
            // When in layer mode, we need to replace the active layer's bitmap
            // The Layer class doesn't expose a bitmap setter, so we use the internal method
            _layers.ActiveLayer.ReplaceBitmap(newBitmap);
        }
        else
        {
            _workingBitmap?.Dispose();
            _workingBitmap = newBitmap;
        }
    }

    /// <summary>
    /// Applies color balance adjustments to the image.
    /// When in layer mode, applies to the active layer.
    /// </summary>
    public bool ApplyColorBalance(ColorBalanceSettings settings)
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (targetBitmap is null || settings is null || !settings.HasAdjustments)
            return false;

        try
        {
            var width = targetBitmap.Width;
            var height = targetBitmap.Height;
            var result = new SKBitmap(width, height);

            var srcPixels = targetBitmap.Pixels;
            var dstPixels = new SKColor[srcPixels.Length];

            var shadowsCR = settings.ShadowsCyanRed / 100f;
            var shadowsMG = settings.ShadowsMagentaGreen / 100f;
            var shadowsYB = settings.ShadowsYellowBlue / 100f;
            var midtonesCR = settings.MidtonesCyanRed / 100f;
            var midtonesMG = settings.MidtonesMagentaGreen / 100f;
            var midtonesYB = settings.MidtonesYellowBlue / 100f;
            var highlightsCR = settings.HighlightsCyanRed / 100f;
            var highlightsMG = settings.HighlightsMagentaGreen / 100f;
            var highlightsYB = settings.HighlightsYellowBlue / 100f;

            for (var i = 0; i < srcPixels.Length; i++)
            {
                var pixel = srcPixels[i];
                var r = pixel.Red / 255f;
                var g = pixel.Green / 255f;
                var b = pixel.Blue / 255f;

                var lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;

                var shadowWeight = 1f - Math.Clamp(lum * 2f, 0f, 1f);
                var highlightWeight = Math.Clamp((lum - 0.5f) * 2f, 0f, 1f);
                var midtoneWeight = 1f - shadowWeight - highlightWeight;

                var rAdjust = shadowsCR * shadowWeight + midtonesCR * midtoneWeight + highlightsCR * highlightWeight;
                var gAdjust = shadowsMG * shadowWeight + midtonesMG * midtoneWeight + highlightsMG * highlightWeight;
                var bAdjust = shadowsYB * shadowWeight + midtonesYB * midtoneWeight + highlightsYB * highlightWeight;

                r += rAdjust * 0.5f;
                g += gAdjust * 0.5f - rAdjust * 0.15f;
                b += bAdjust * 0.5f - rAdjust * 0.15f;

                if (bAdjust < 0) { r -= bAdjust * 0.3f; g -= bAdjust * 0.3f; }
                if (gAdjust < 0) { r -= gAdjust * 0.3f; b -= gAdjust * 0.3f; }

                if (settings.PreserveLuminosity)
                {
                    var newLum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    if (newLum > 0.001f)
                    {
                        var lumRatio = lum / newLum;
                        r *= lumRatio; g *= lumRatio; b *= lumRatio;
                    }
                }

                var newR = (byte)Math.Clamp((int)(r * 255f), 0, 255);
                var newG = (byte)Math.Clamp((int)(g * 255f), 0, 255);
                var newB = (byte)Math.Clamp((int)(b * 255f), 0, 255);

                dstPixels[i] = new SKColor(newR, newG, newB, pixel.Alpha);
            }

            result.Pixels = dstPixels;
            SetOperationTargetBitmap(result);
            OnImageChanged();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Sets a preview bitmap to display instead of the working bitmap.
    /// </summary>
    public bool SetColorBalancePreview(ColorBalanceSettings settings)
    {
        if (settings is null)
            return false;

        lock (_bitmapLock)
        {
            var targetBitmap = GetOperationTargetBitmap();
            if (targetBitmap is null)
                return false;

            // Dispose old preview
            var oldPreview = _previewBitmap;
            _previewBitmap = null;

            if (!settings.HasAdjustments)
            {
                _isPreviewActive = false;
                oldPreview?.Dispose();
                OnImageChanged();
                return true;
            }

            // Create new preview from target bitmap (not swapping)
            var newPreview = CreateColorBalancePreview(targetBitmap, settings);
            
            
            // Only after new preview is ready, dispose old and assign new
            _previewBitmap = newPreview;
            _isPreviewActive = _previewBitmap is not null;
            oldPreview?.Dispose();
        }

        OnImageChanged();
        return _isPreviewActive;
    }

    /// <summary>
    /// Creates a color balance preview bitmap from the source bitmap.
    /// Does not modify any class fields.
    /// </summary>
    private static SKBitmap? CreateColorBalancePreview(SKBitmap source, ColorBalanceSettings settings)
    {
        if (source is null || settings is null || !settings.HasAdjustments)
            return null;

        try
        {
            var width = source.Width;
            var height = source.Height;
            var result = new SKBitmap(width, height);

            var srcPixels = source.Pixels;
            var dstPixels = new SKColor[srcPixels.Length];

            var shadowsCR = settings.ShadowsCyanRed / 100f;
            var shadowsMG = settings.ShadowsMagentaGreen / 100f;
            var shadowsYB = settings.ShadowsYellowBlue / 100f;
            var midtonesCR = settings.MidtonesCyanRed / 100f;
            var midtonesMG = settings.MidtonesMagentaGreen / 100f;
            var midtonesYB = settings.MidtonesYellowBlue / 100f;
            var highlightsCR = settings.HighlightsCyanRed / 100f;
            var highlightsMG = settings.HighlightsMagentaGreen / 100f;
            var highlightsYB = settings.HighlightsYellowBlue / 100f;

            for (var i = 0; i < srcPixels.Length; i++)
            {
                var pixel = srcPixels[i];
                var r = pixel.Red / 255f;
                var g = pixel.Green / 255f;
                var b = pixel.Blue / 255f;

                var lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;

                var shadowWeight = 1f - Math.Clamp(lum * 2f, 0f, 1f);
                var highlightWeight = Math.Clamp((lum - 0.5f) * 2f, 0f, 1f);
                var midtoneWeight = 1f - shadowWeight - highlightWeight;

                var rAdjust = shadowsCR * shadowWeight + midtonesCR * midtoneWeight + highlightsCR * highlightWeight;
                var gAdjust = shadowsMG * shadowWeight + midtonesMG * midtoneWeight + highlightsMG * highlightWeight;
                var bAdjust = shadowsYB * shadowWeight + midtonesYB * midtoneWeight + highlightsYB * highlightWeight;

                r += rAdjust * 0.5f;
                g += gAdjust * 0.5f - rAdjust * 0.15f;
                b += bAdjust * 0.5f - rAdjust * 0.15f;

                if (bAdjust < 0) { r -= bAdjust * 0.3f; g -= bAdjust * 0.3f; }
                if (gAdjust < 0) { r -= gAdjust * 0.3f; b -= gAdjust * 0.3f; }

                if (settings.PreserveLuminosity)
                {
                    var newLum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    if (newLum > 0.001f)
                    {
                        var lumRatio = lum / newLum;
                        r *= lumRatio; g *= lumRatio; b *= lumRatio;
                    }
                }

                var newR = (byte)Math.Clamp((int)(r * 255f), 0, 255);
                var newG = (byte)Math.Clamp((int)(g * 255f), 0, 255);
                var newB = (byte)Math.Clamp((int)(b * 255f), 0, 255);

                dstPixels[i] = new SKColor(newR, newG, newB, pixel.Alpha);
            }

            result.Pixels = dstPixels;
            return result;
        }
        catch
        {
            return null;
        }
    }

    #endregion Color Balance

    #region Preview Management

    /// <summary>
    /// Clears the current preview and restores normal display.
    /// </summary>
    /// <param name="raiseEvent">Whether to raise the ImageChanged event (default: true).</param>
    public void ClearPreview(bool raiseEvent = true)
    {
        SKBitmap? oldPreview;
        bool shouldRaiseEvent;
        
        lock (_bitmapLock)
        {
            oldPreview = _previewBitmap;
            _previewBitmap = null;
            _isPreviewActive = false;
            shouldRaiseEvent = raiseEvent && _workingBitmap is not null;
        }
        
        oldPreview?.Dispose();
        
        if (shouldRaiseEvent)
        {
            OnImageChanged();
        }
    }

    /// <summary>
    /// Applies the current preview to the working bitmap.
    /// </summary>
    public bool ApplyPreview()
    {
        SKBitmap? oldWorking;
        
        lock (_bitmapLock)
        {
            if (!_isPreviewActive || _previewBitmap is null)
                return false;

            oldWorking = _workingBitmap;
            _workingBitmap = _previewBitmap;
            _previewBitmap = null;
            _isPreviewActive = false;
        }
        
        oldWorking?.Dispose();
        OnImageChanged();
        return true;
    }

    #endregion Preview Management

    #region Brightness and Contrast

    /// <summary>
    /// Applies brightness and contrast adjustments to the image.
    /// When in layer mode, applies to the active layer.
    /// </summary>
    public bool ApplyBrightnessContrast(BrightnessContrastSettings settings)
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (targetBitmap is null || settings is null || !settings.HasAdjustments)
            return false;

        try
        {
            var result = CreateBrightnessContrastPreview(targetBitmap, settings);
            if (result is null)
                return false;

            SetOperationTargetBitmap(result);
            OnImageChanged();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Sets a preview bitmap with brightness/contrast adjustments.
    /// </summary>
    public bool SetBrightnessContrastPreview(BrightnessContrastSettings settings)
    {
        if (settings is null)
            return false;

        lock (_bitmapLock)
        {
            var targetBitmap = GetOperationTargetBitmap();
            if (targetBitmap is null)
                return false;

            // Dispose old preview
            var oldPreview = _previewBitmap;
            _previewBitmap = null;

            if (!settings.HasAdjustments)
            {
                _isPreviewActive = false;
                oldPreview?.Dispose();
                OnImageChanged();
                return true;
            }

            // Create new preview from target bitmap
            var newPreview = CreateBrightnessContrastPreview(targetBitmap, settings);
            
            // Only after new preview is ready, dispose old and assign new
            _previewBitmap = newPreview;
            _isPreviewActive = _previewBitmap is not null;
            oldPreview?.Dispose();
        }

        OnImageChanged();
        return _isPreviewActive;
    }

    /// <summary>
    /// Creates a brightness/contrast preview bitmap from the source bitmap.
    /// Does not modify any class fields.
    /// </summary>
    private static SKBitmap? CreateBrightnessContrastPreview(SKBitmap source, BrightnessContrastSettings settings)
    {
        if (source is null || settings is null || !settings.HasAdjustments)
            return null;

        try
        {
            var width = source.Width;
            var height = source.Height;
            var result = new SKBitmap(width, height);

            var srcPixels = source.Pixels;
            var dstPixels = new SKColor[srcPixels.Length];

            // Normalize brightness (-100 to +100) to a factor
            // Brightness: add/subtract from pixel values
            var brightnessFactor = settings.Brightness / 100f;
            
            // Normalize contrast (-100 to +100) to a factor
            // Contrast: 0 = gray, 1 = normal, >1 = more contrast
            // Map -100..+100 to 0..2 (with 0 = no change)
            var contrastFactor = (settings.Contrast + 100f) / 100f;

            for (var i = 0; i < srcPixels.Length; i++)
            {
                var pixel = srcPixels[i];
                
                // Convert to 0-1 range
                var r = pixel.Red / 255f;
                var g = pixel.Green / 255f;
                var b = pixel.Blue / 255f;

                // Apply brightness (additive)
                r += brightnessFactor;
                g += brightnessFactor;
                b += brightnessFactor;

                // Apply contrast (multiply around 0.5 midpoint)
                r = (r - 0.5f) * contrastFactor + 0.5f;
                g = (g - 0.5f) * contrastFactor + 0.5f;
                b = (b - 0.5f) * contrastFactor + 0.5f;

                // Clamp and convert back to byte
                var newR = (byte)Math.Clamp((int)(r * 255f), 0, 255);
                var newG = (byte)Math.Clamp((int)(g * 255f), 0, 255);
                var newB = (byte)Math.Clamp((int)(b * 255f), 0, 255);

                dstPixels[i] = new SKColor(newR, newG, newB, pixel.Alpha);
            }

            result.Pixels = dstPixels;
            return result;
        }
        catch
        {
            return null;
        }
    }

    #endregion Brightness and Contrast


    #region Background Removal

    /// <summary>
    /// Applies an alpha mask to the current image for background removal.
    /// When in layer mode, applies to the active layer.
    /// </summary>
    /// <param name="maskData">Grayscale mask data where 255 = foreground, 0 = background.</param>
    /// <param name="width">Width of the mask in pixels.</param>
    /// <param name="height">Height of the mask in pixels.</param>
    /// <returns>True if the mask was applied successfully.</returns>
    public bool ApplyBackgroundMask(byte[] maskData, int width, int height)
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (maskData is null || targetBitmap is null)
            return false;

        if (width != targetBitmap.Width || height != targetBitmap.Height)
            return false;

        if (maskData.Length != width * height)
            return false;

        try
        {
            lock (_bitmapLock)
            {
                var pixels = targetBitmap.Pixels;
                var newPixels = new SKColor[pixels.Length];

                for (var i = 0; i < pixels.Length; i++)
                {
                    var pixel = pixels[i];
                    var maskValue = maskData[i];

                    // Apply mask as alpha channel
                    newPixels[i] = new SKColor(pixel.Red, pixel.Green, pixel.Blue, maskValue);
                }

                // Create new bitmap with the masked pixels
                var result = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                result.Pixels = newPixels;
                SetOperationTargetBitmap(result);
            }

            OnImageChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Applies the background removal mask as layers, creating:
    /// 1. Subject layer (top) - foreground with background transparent
    /// 2. Background layer (middle) - background with subject transparent (inverted mask)
    /// 3. Original layer (bottom) - the existing layer is kept as the original
    /// Automatically enables layer mode if not already enabled.
    /// </summary>
    /// <param name="maskData">Grayscale mask data where 255 = foreground, 0 = background.</param>
    /// <param name="width">Width of the mask in pixels.</param>
    /// <param name="height">Height of the mask in pixels.</param>
    /// <returns>True if the layers were created successfully.</returns>
    public bool ApplyBackgroundMaskWithLayers(byte[] maskData, int width, int height)
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (maskData is null || targetBitmap is null)
            return false;

        if (width != targetBitmap.Width || height != targetBitmap.Height)
            return false;

        if (maskData.Length != width * height)
            return false;

        try
        {
            lock (_bitmapLock)
            {
                var pixels = targetBitmap.Pixels;

                // Create subject bitmap (foreground with background transparent)
                var subjectPixels = new SKColor[pixels.Length];
                for (var i = 0; i < pixels.Length; i++)
                {
                    var pixel = pixels[i];
                    var maskValue = maskData[i];
                    subjectPixels[i] = new SKColor(pixel.Red, pixel.Green, pixel.Blue, maskValue);
                }
                var subjectBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                subjectBitmap.Pixels = subjectPixels;

                // Create background bitmap (background with subject transparent - inverted mask)
                var backgroundPixels = new SKColor[pixels.Length];
                for (var i = 0; i < pixels.Length; i++)
                {
                    var pixel = pixels[i];
                    var invertedMaskValue = (byte)(255 - maskData[i]);
                    backgroundPixels[i] = new SKColor(pixel.Red, pixel.Green, pixel.Blue, invertedMaskValue);
                }
                var backgroundBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                backgroundBitmap.Pixels = backgroundPixels;

                if (_layers != null)
                {
                    // Already in layer mode - the active layer IS the original, just rename it
                    // and add Subject and Background layers above it
                    var activeLayer = _layers.ActiveLayer;
                    if (activeLayer != null)
                    {
                        activeLayer.Name = "Original";
                    }
                    
                    // Add Background and Subject layers on top
                    _layers.AddLayerFromBitmap(backgroundBitmap, "Background");
                    _layers.AddLayerFromBitmap(subjectBitmap, "Subject");
                }
                else
                {
                    // Not in layer mode - enable it with the 3-layer structure
                    // Create original bitmap (unchanged copy) only when starting fresh
                    var originalBitmap = targetBitmap.Copy();
                    
                    _layers = new LayerStack(width, height);
                    _layers.AddLayerFromBitmap(originalBitmap, "Original");
                    _layers.AddLayerFromBitmap(backgroundBitmap, "Background");
                    _layers.AddLayerFromBitmap(subjectBitmap, "Subject");
                    _layers.ContentChanged += OnLayersContentChanged;
                    _layers.LayersChanged += OnLayersCollectionChanged;
                    _isLayerMode = true;
                    LayerModeChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            OnImageChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets a preview with background removed using the provided mask.
    /// Does not modify the working bitmap until ApplyPreview is called.
    /// </summary>
    /// <param name="maskData">Grayscale mask data where 255 = foreground, 0 = background.</param>
    /// <param name="width">Width of the mask in pixels.</param>
    /// <param name="height">Height of the mask in pixels.</param>
    /// <returns>True if the preview was set successfully.</returns>
    public bool SetBackgroundRemovalPreview(byte[] maskData, int width, int height)
    {
        if (maskData is null)
            return false;

        lock (_bitmapLock)
        {
            var targetBitmap = GetOperationTargetBitmap();
            if (targetBitmap is null)
                return false;

            if (width != targetBitmap.Width || height != targetBitmap.Height)
                return false;

            if (maskData.Length != width * height)
                return false;

            // Dispose old preview
            var oldPreview = _previewBitmap;
            _previewBitmap = null;

            try
            {
                // Create new preview with alpha applied
                var newPreview = new SKBitmap(targetBitmap.Width, targetBitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                var pixels = targetBitmap.Pixels;
                var newPixels = new SKColor[pixels.Length];

                for (var i = 0; i < pixels.Length; i++)
                {
                    var pixel = pixels[i];
                    var maskValue = maskData[i];
                    newPixels[i] = new SKColor(pixel.Red, pixel.Green, pixel.Blue, maskValue);
                }

                newPreview.Pixels = newPixels;

                _previewBitmap = newPreview;
                _isPreviewActive = true;
                oldPreview?.Dispose();
            }
            catch
            {
                oldPreview?.Dispose();
                return false;
            }
        }

        OnImageChanged();
        return true;
    }

    /// <summary>
    /// Gets the raw RGBA pixel data from the current target bitmap (active layer or working bitmap).
    /// Used for passing to background removal service.
    /// </summary>
    /// <returns>Tuple of (imageData, width, height) or null if no image is loaded.</returns>
    public (byte[] Data, int Width, int Height)? GetWorkingBitmapData()
    {
        lock (_bitmapLock)
        {
            var targetBitmap = GetOperationTargetBitmap();
            if (targetBitmap is null)
                return null;

            var width = targetBitmap.Width;
            var height = targetBitmap.Height;
            var pixels = targetBitmap.Pixels;
            var data = new byte[width * height * 4]; // RGBA

            for (var i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                var offset = i * 4;
                data[offset] = pixel.Red;
                data[offset + 1] = pixel.Green;
                data[offset + 2] = pixel.Blue;
                data[offset + 3] = pixel.Alpha;
            }

            return (data, width, height);
        }
    }

    #endregion Background Removal

    #region Background Fill



    /// <summary>
    /// Fills transparent areas of the image with the specified solid color.
    /// Uses alpha compositing to blend the fill color behind the existing image.
    /// When in layer mode, applies to the active layer.
    /// </summary>
    /// <param name="settings">The background fill settings containing the fill color.</param>
    /// <returns>True if the fill was applied successfully.</returns>
    public bool ApplyBackgroundFill(BackgroundFillSettings settings)
    {
        var targetBitmap = GetOperationTargetBitmap();
        if (targetBitmap is null || settings is null)
            return false;

        try
        {
            var result = CreateBackgroundFillBitmap(targetBitmap, settings);
            if (result is null)
                return false;

            lock (_bitmapLock)
            {
                SetOperationTargetBitmap(result);
            }

            OnImageChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets a preview with the background filled using the specified color.
    /// Does not modify the working bitmap until ApplyPreview is called.
    /// </summary>
    /// <param name="settings">The background fill settings containing the fill color.</param>
    /// <returns>True if the preview was set successfully.</returns>
    public bool SetBackgroundFillPreview(BackgroundFillSettings settings)
    {
        if (settings is null)
            return false;

        lock (_bitmapLock)
        {
            var targetBitmap = GetOperationTargetBitmap();
            if (targetBitmap is null)
                return false;

            // Dispose old preview
            var oldPreview = _previewBitmap;
            _previewBitmap = null;


            try
            {
                // Create new preview with background filled
                var newPreview = CreateBackgroundFillBitmap(targetBitmap, settings);

                _previewBitmap = newPreview;
                _isPreviewActive = _previewBitmap is not null;
                oldPreview?.Dispose();
            }
            catch
            {
                oldPreview?.Dispose();
                return false;
            }
        }

        OnImageChanged();
        return _isPreviewActive;
    }

    /// <summary>
    /// Creates a new bitmap with transparent areas filled with the specified color.
    /// Uses alpha compositing: result = foreground * alpha + background * (1 - alpha)
    /// </summary>
    private static SKBitmap? CreateBackgroundFillBitmap(SKBitmap source, BackgroundFillSettings settings)
    {
        if (source is null || settings is null)
            return null;

        try
        {
            var width = source.Width;
            var height = source.Height;
            
            // Create result bitmap without alpha (opaque)
            var result = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);

            var srcPixels = source.Pixels;
            var dstPixels = new SKColor[srcPixels.Length];

            var fillR = settings.Red;
            var fillG = settings.Green;
            var fillB = settings.Blue;

            for (var i = 0; i < srcPixels.Length; i++)
            {
                var pixel = srcPixels[i];
                var alpha = pixel.Alpha / 255f;

                // Alpha compositing: foreground * alpha + background * (1 - alpha)
                var r = (byte)Math.Clamp((int)(pixel.Red * alpha + fillR * (1f - alpha)), 0, 255);
                var g = (byte)Math.Clamp((int)(pixel.Green * alpha + fillG * (1f - alpha)), 0, 255);
                var b = (byte)Math.Clamp((int)(pixel.Blue * alpha + fillB * (1f - alpha)), 0, 255);

                dstPixels[i] = new SKColor(r, g, b, 255);
            }

            result.Pixels = dstPixels;
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if the current working image has any transparency.
    /// </summary>
    /// <returns>True if the image contains transparent pixels.</returns>
    public bool HasTransparency()
    {
        lock (_bitmapLock)
        {
            if (_workingBitmap is null)
                return false;

            var pixels = _workingBitmap.Pixels;
            for (var i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].Alpha < 255)
                    return true;
            }

            return false;
        }
    }

    #endregion Background Fill

    #region Drawing

    /// <summary>
    /// Applies a drawing stroke to the working bitmap or active layer.
    /// </summary>
    /// <param name="normalizedPoints">Points in normalized coordinates (0-1).</param>
    /// <param name="color">The stroke color.</param>
    /// <param name="brushSize">The brush size in pixels relative to display size.</param>
    /// <param name="brushShape">The brush shape.</param>
    /// <returns>True if the stroke was applied successfully.</returns>
    public bool ApplyStroke(IReadOnlyList<SKPoint> normalizedPoints, SKColor color, float brushSize, BrushShape brushShape)
    {
        if (normalizedPoints is null || normalizedPoints.Count == 0)
            return false;

        lock (_bitmapLock)
        {
            // Get the target bitmap (active layer in layer mode, or working bitmap)
            SKBitmap? targetBitmap;
            Layer? targetLayer = null;

            if (_isLayerMode && _layers?.ActiveLayer != null)
            {
                targetLayer = _layers.ActiveLayer;
                if (!targetLayer.CanEdit)
                    return false;
                targetBitmap = targetLayer.Bitmap;
            }
            else
            {
                targetBitmap = _workingBitmap;
            }

            if (targetBitmap is null)
                return false;

            try
            {
                var width = targetBitmap.Width;
                var height = targetBitmap.Height;

                // Convert normalized points to image pixel coordinates
                var imagePoints = normalizedPoints
                    .Select(p => new SKPoint(p.X * width, p.Y * height))
                    .ToList();

                // Scale brush size from display coordinates to image coordinates
                var scaledBrushSize = brushSize * width;

                using var canvas = new SKCanvas(targetBitmap);
                using var paint = new SKPaint
                {
                    Color = color,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = scaledBrushSize
                };

                if (brushShape == BrushShape.Round)
                {
                    paint.StrokeCap = SKStrokeCap.Round;
                    paint.StrokeJoin = SKStrokeJoin.Round;
                }
                else
                {
                    paint.StrokeCap = SKStrokeCap.Square;
                    paint.StrokeJoin = SKStrokeJoin.Miter;
                }

                if (imagePoints.Count == 1)
                {
                    var point = imagePoints[0];
                    paint.Style = SKPaintStyle.Fill;
                    if (brushShape == BrushShape.Round)
                    {
                        canvas.DrawCircle(point, scaledBrushSize / 2, paint);
                    }
                    else
                    {
                        var halfSize = scaledBrushSize / 2;
                        canvas.DrawRect(point.X - halfSize, point.Y - halfSize, scaledBrushSize, scaledBrushSize, paint);
                    }
                }
                else if (imagePoints.Count == 2)
                {
                    canvas.DrawLine(imagePoints[0], imagePoints[1], paint);
                }
                else
                {
                    using var path = new SKPath();
                    path.MoveTo(imagePoints[0]);
                    for (var i = 1; i < imagePoints.Count; i++)
                    {
                        path.LineTo(imagePoints[i]);
                    }
                    canvas.DrawPath(path, paint);
                }

                canvas.Flush();

                // Notify layer of content change
                targetLayer?.NotifyContentChanged();
            }
            catch
            {
                return false;
            }
        }

        OnImageChanged();
        return true;
    }

    #endregion Drawing

    #region Shape Drawing

    /// <summary>
    /// Applies a shape to the working bitmap or active layer.
    /// </summary>
    /// <param name="shapeData">The shape data to apply.</param>
    /// <returns>True if the shape was applied successfully.</returns>
    public bool ApplyShape(ShapeData shapeData)
    {
        if (shapeData is null)
            return false;

        lock (_bitmapLock)
        {
            // Get the target bitmap (active layer in layer mode, or working bitmap)
            SKBitmap? targetBitmap;
            Layer? targetLayer = null;

            if (_isLayerMode && _layers?.ActiveLayer != null)
            {
                targetLayer = _layers.ActiveLayer;
                if (!targetLayer.CanEdit)
                    return false;
                targetBitmap = targetLayer.Bitmap;
            }
            else
            {
                targetBitmap = _workingBitmap;
            }

            if (targetBitmap is null)
                return false;

            try
            {
                var width = targetBitmap.Width;
                var height = targetBitmap.Height;

                // Convert normalized coordinates to image coordinates
                var start = new SKPoint(
                    shapeData.NormalizedStart.X * width,
                    shapeData.NormalizedStart.Y * height);
                var end = new SKPoint(
                    shapeData.NormalizedEnd.X * width,
                    shapeData.NormalizedEnd.Y * height);

                // Scale stroke width from normalized to image coordinates
                var scaledStrokeWidth = shapeData.StrokeWidth * width;
                var scaledArrowHeadSize = shapeData.ArrowHeadSize;

                using var canvas = new SKCanvas(targetBitmap);

                ShapeTool.RenderShape(
                    canvas,
                    start,
                    end,
                    shapeData.ShapeType,
                    shapeData.FillMode,
                    shapeData.StrokeColor,
                    shapeData.FillColor,
                    scaledStrokeWidth,
                    scaledArrowHeadSize);

                canvas.Flush();

                // Notify layer of content change
                targetLayer?.NotifyContentChanged();
            }
            catch
            {
                return false;
            }
        }

        OnImageChanged();
        return true;
    }

    #endregion Shape Drawing

    #region Save with Layers

    /// <summary>
    /// Saves the image, flattening layers if in layer mode.
    /// </summary>
    /// <param name="filePath">Output file path.</param>
    /// <param name="format">Image format.</param>
    /// <param name="quality">Quality for lossy formats.</param>
    /// <returns>True if saved successfully.</returns>
    public bool SaveImageFlattened(string filePath, SKEncodedImageFormat format = SKEncodedImageFormat.Png, int quality = 95)
    {
        SKBitmap? bitmapToSave;

        lock (_bitmapLock)
        {
            if (_isLayerMode && _layers != null)
            {
                bitmapToSave = _layers.Flatten();
            }
            else
            {
                bitmapToSave = _workingBitmap?.Copy();
            }
        }

        if (bitmapToSave == null)
            return false;

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var image = SKImage.FromBitmap(bitmapToSave);
            if (image is null)
                return false;

            using var data = image.Encode(format, quality);
            if (data is null)
                return false;

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            data.SaveTo(stream);

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            bitmapToSave.Dispose();
        }
    }

    /// <summary>
    /// Saves layers to a multi-page TIFF file.
    /// </summary>
    /// <param name="filePath">Output file path.</param>
    /// <returns>True if saved successfully.</returns>
    public bool SaveLayeredTiff(string filePath)
    {
        if (!_isLayerMode || _layers == null)
            return false;

        return TiffExporter.SaveLayeredTiff(_layers, filePath);
    }

    /// <summary>
    /// Loads a layered TIFF file.
    /// </summary>
    /// <param name="filePath">TIFF file path.</param>
    /// <returns>True if loaded successfully.</returns>
    public bool LoadLayeredTiff(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        var loadedLayers = TiffExporter.LoadLayeredTiff(filePath);
        if (loadedLayers == null || loadedLayers.Count == 0)
            return false;

        ClearPreview(raiseEvent: false);

        // Dispose existing layers
        if (_layers != null)
        {
            _layers.ContentChanged -= OnLayersContentChanged;
            _layers.LayersChanged -= OnLayersCollectionChanged;
            _layers.Dispose();
        }

        // Dispose old bitmaps
        _originalBitmap?.Dispose();
        _workingBitmap?.Dispose();

        // Store a flattened copy as the original for Reset support
        var flattened = loadedLayers.Flatten();
        _originalBitmap = flattened;
        _workingBitmap = flattened?.Copy();

        // Get file size
        var fileInfo = new FileInfo(filePath);
        FileSizeBytes = fileInfo.Length;

        _layers = loadedLayers;
        _layers.ContentChanged += OnLayersContentChanged;
        _layers.LayersChanged += OnLayersCollectionChanged;
        _isLayerMode = true;
        CurrentImagePath = filePath;

        ResetZoom();
        OnImageChanged();
        LayerModeChanged?.Invoke(this, EventArgs.Empty);
        LayersChanged?.Invoke(this, EventArgs.Empty);

        return true;
    }

    #endregion Save with Layers

    private void OnZoomChanged() => ZoomChanged?.Invoke(this, EventArgs.Empty);
    private void OnImageChanged() => ImageChanged?.Invoke(this, EventArgs.Empty);

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
            _previewBitmap?.Dispose();
            if (_layers != null)
            {
                _layers.ContentChanged -= OnLayersContentChanged;
                _layers.LayersChanged -= OnLayersCollectionChanged;
                _layers.Dispose();
            }
            _originalBitmap = null;
            _workingBitmap = null;
            _previewBitmap = null;
            _layers = null;
        }
        _disposed = true;
    }
}
