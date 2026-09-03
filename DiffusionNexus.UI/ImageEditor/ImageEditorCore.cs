using DiffusionNexus.UI.ImageEditor.Services;
using DiffusionNexus.UI.Services;
using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Platform-independent image editor core using SkiaSharp.
/// Supports layer-based editing with compositing.
/// </summary>
public partial class ImageEditorCore : IDisposable
{
    private EditorServices? _services;
    private SKBitmap? _originalBitmap;
    private SKBitmap? _workingBitmap;
    private SKBitmap? _previewBitmap;
    private SKBitmap? _inpaintBaseBitmap;
    private long _inpaintBaseVersion;
    private bool _isPreviewActive;
    private bool _disposed;
    private int _imageDpi = 72;

    /// <summary>
    /// Guards every bitmap and layer that <see cref="RenderWithZoom"/> can reach.
    /// <para>
    /// Avalonia executes the canvas' <c>ICustomDrawOperation.Render</c> — and therefore
    /// <see cref="RenderWithZoom"/> — on the compositor's <b>render thread</b>, while loading,
    /// clearing, cropping, transforming and every layer edit run on the UI thread. Any code that
    /// frees a bitmap, replaces one, or mutates the layer collection must hold this lock, or Skia
    /// dereferences a freed <c>sk_bitmap_t</c> and the process dies with an access violation
    /// (surfacing as <c>ExecutionEngineException</c>) that no <c>catch</c> can intercept.
    /// </para>
    /// Hold it only for the mutation itself: decoding, encoding and file I/O belong outside, and
    /// bitmaps detached under the lock can safely be disposed after releasing it.
    /// </summary>
    private readonly object _bitmapLock = new();

    /// <summary>
    /// The render lock described on <c>_bitmapLock</c>, exposed so tests can hold it to simulate
    /// an in-flight repaint. Production code must never take this from outside the editor core.
    /// </summary>
    internal object RenderLock => _bitmapLock;

    private SKRect _lastImageRect;

    // Layer state - delegated to LayerManager when services are wired
    private LayerStack? _layers => _services?.Layers.Stack;
    private bool _isLayerMode => _services?.Layers.IsLayerMode ?? false;

    // Viewport state - delegated to ViewportManager when services are wired
    private float _zoomLevel
    {
        get => _services?.Viewport.ZoomLevel ?? 1f;
        set { if (_services is not null) _services.Viewport.ZoomLevel = value; }
    }
    private float _panX
    {
        get => _services?.Viewport.PanX ?? 0f;
        set { if (_services is not null) _services.Viewport.PanX = value; }
    }
    private float _panY
    {
        get => _services?.Viewport.PanY ?? 0f;
        set { if (_services is not null) _services.Viewport.PanY = value; }
    }
    private bool _isFitMode
    {
        get => _services?.Viewport.IsFitMode ?? true;
        set { if (_services is not null) _services.Viewport.IsFitMode = value; }
    }

    /// <summary>
    /// Wires the editor to the service graph. Must be called once before use.
    /// </summary>
    public void SetServices(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (_services is not null)
            throw new InvalidOperationException("Services have already been wired. SetServices must only be called once.");

        _services = services;

        // Subscribe to service events so EditorCore stays reactive
        _services.Layers.ContentChanged += OnLayersContentChanged;
        _services.Layers.LayersChanged += OnLayersCollectionChanged;
        _services.Layers.LayerModeChanged += OnLayerModeChanged;
        _services.Viewport.Changed += OnViewportChanged;
    }

    private void OnLayerModeChanged(object? sender, EventArgs e)
    {
        LayerModeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnViewportChanged(object? sender, EventArgs e)
    {
        OnZoomChanged();
    }

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
    /// Gets the text tool instance.
    /// </summary>
    public TextTool TextTool { get; } = new();

    /// <summary>
    /// Gets the outpaint tool instance.
    /// </summary>
    public OutpaintTool OutpaintTool { get; } = new();

    /// <summary>
    /// Gets the canvas extend tool instance (grow the canvas without generating content).
    /// </summary>
    public CanvasExtendTool CanvasExtendTool { get; } = new();

    /// <summary>
    /// Commits any in-progress tool operations (placed text, placed shape, active drawing stroke).
    /// Call before saving or exporting to ensure all pending work is captured.
    /// </summary>
    public void CommitPendingOperations()
    {
        if (TextTool.HasPlacedText)
            TextTool.CommitPlacedText();

        if (ShapeTool.HasPlacedShape)
            ShapeTool.CommitPlacedShape();

        if (DrawingTool.IsDrawing)
            DrawingTool.OnPointerReleased();
    }

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
                if (_services is not null) _services.Layers.IsLayerMode = value;
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
    /// Optional unified logger for diagnostics (image / TIFF load and save). Set by the view
    /// wiring so failures surface in the unified log console instead of failing silently.
    /// </summary>
    public Domain.Services.UnifiedLogging.IUnifiedLogger? Logger { get; set; }

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
            _zoomLevel = value;
            OnZoomChanged();
        }
    }

    /// <summary>
    /// Gets the zoom level as a percentage (0-1000).
    /// </summary>
    public int ZoomPercentage => _services?.Viewport.ZoomPercentage ?? (int)Math.Round(_zoomLevel * 100);

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
        if (_layers != null || _workingBitmap == null || _services is null) return;

        var layerName = !string.IsNullOrEmpty(CurrentImagePath)
            ? Path.GetFileNameWithoutExtension(CurrentImagePath)
            : "Background";

        lock (_bitmapLock)
        {
            _services.Layers.EnableLayerMode(_workingBitmap, layerName);
        }

        OnImageChanged();
    }

    /// <summary>
    /// Disables layer mode, flattening all layers to a single bitmap.
    /// </summary>
    public void DisableLayerMode()
    {
        if (_layers == null || _services is null) return;

        SKBitmap? replaced = null;

        lock (_bitmapLock)
        {
            var flattened = _services.Layers.DisableLayerMode();
            if (flattened != null)
            {
                replaced = _workingBitmap;
                _workingBitmap = flattened;
            }
        }

        replaced?.Dispose();
        OnImageChanged();
    }

    /// <summary>
    /// Flattens all layers into a single layer while keeping layer mode active.
    /// </summary>
    public void FlattenAllLayers()
    {
        if (_services is null) return;

        lock (_bitmapLock)
        {
            _services.Layers.FlattenAllLayers();
        }

        OnImageChanged();
    }

    /// <summary>
    /// Adds a new empty layer at the top of the stack.
    /// </summary>
    /// <param name="name">Optional layer name.</param>
    /// <returns>The newly created layer, or null if not in layer mode.</returns>
    public Layer? AddLayer(string? name = null)
    {
        lock (_bitmapLock)
        {
            return _services?.Layers.AddLayer(name);
        }
    }

    /// <summary>
    /// Adds a layer from a bitmap.
    /// </summary>
    /// <param name="bitmap">Source bitmap.</param>
    /// <param name="name">Optional layer name.</param>
    /// <returns>The newly created layer, or null if not in layer mode.</returns>
    public Layer? AddLayerFromBitmap(SKBitmap bitmap, string? name = null)
    {
        lock (_bitmapLock)
        {
            return _services?.Layers.AddLayerFromBitmap(bitmap, name);
        }
    }

    /// <summary>
    /// Expands the layer canvas and offsets all existing layer content.
    /// </summary>
    public void ResizeLayerCanvas(int newWidth, int newHeight, int offsetX, int offsetY)
    {
        lock (_bitmapLock)
        {
            _services?.Layers.ResizeCanvas(newWidth, newHeight, offsetX, offsetY);
        }

        OnImageChanged();
    }

    /// <summary>
    /// Removes a layer from the stack.
    /// </summary>
    /// <param name="layer">Layer to remove.</param>
    /// <returns>True if removed successfully.</returns>
    public bool RemoveLayer(Layer layer)
    {
        lock (_bitmapLock)
        {
            return _services?.Layers.RemoveLayer(layer) ?? false;
        }
    }

    /// <summary>
    /// Duplicates a layer.
    /// </summary>
    /// <param name="layer">Layer to duplicate.</param>
    /// <returns>The duplicated layer, or null if failed.</returns>
    public Layer? DuplicateLayer(Layer layer)
    {
        lock (_bitmapLock)
        {
            return _services?.Layers.DuplicateLayer(layer);
        }
    }

    /// <summary>
    /// Moves a layer up in the stack.
    /// </summary>
    /// <param name="layer">Layer to move.</param>
    /// <returns>True if moved successfully.</returns>
    public bool MoveLayerUp(Layer layer)
    {
        lock (_bitmapLock)
        {
            return _services?.Layers.MoveLayerUp(layer) ?? false;
        }
    }

    /// <summary>
    /// Moves a layer down in the stack.
    /// </summary>
    /// <param name="layer">Layer to move.</param>
    /// <returns>True if moved successfully.</returns>
    public bool MoveLayerDown(Layer layer)
    {
        lock (_bitmapLock)
        {
            return _services?.Layers.MoveLayerDown(layer) ?? false;
        }
    }

    /// <summary>
    /// Merges the specified layer with the layer below it.
    /// </summary>
    /// <param name="layer">Layer to merge down.</param>
    /// <returns>True if merged successfully.</returns>
    public bool MergeLayerDown(Layer layer)
    {
        lock (_bitmapLock)
        {
            return _services?.Layers.MergeLayerDown(layer) ?? false;
        }
    }

    /// <summary>
    /// Merges all visible layers.
    /// </summary>
    public void MergeVisibleLayers()
    {
        lock (_bitmapLock)
        {
            _services?.Layers.MergeVisibleLayers();
        }
    }

    /// <summary>
    /// Gets or sets the active layer for editing.
    /// </summary>
    public Layer? ActiveLayer
    {
        get => _services?.Layers.ActiveLayer;
        set
        {
            if (_services is not null)
            {
                _services.Layers.ActiveLayer = value;
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
    /// Publishes a freshly decoded image: discards the current layer stack and swaps in the new
    /// original/working bitmaps while holding the render lock, then frees the replaced bitmaps
    /// once the render thread can no longer be inside a draw that references them.
    /// </summary>
    /// <param name="original">The newly decoded source bitmap. Ownership transfers to the editor.</param>
    /// <param name="working">The editable copy of <paramref name="original"/>. Ownership transfers to the editor.</param>
    private void SwapLoadedBitmaps(SKBitmap original, SKBitmap working)
    {
        SKBitmap? replacedOriginal;
        SKBitmap? replacedWorking;

        lock (_bitmapLock)
        {
            // Tear down existing layer stack without flattening (we're discarding it)
            if (_isLayerMode && _services is not null)
            {
                _services.Layers.Reset();
            }

            replacedOriginal = _originalBitmap;
            replacedWorking = _workingBitmap;
            _originalBitmap = original;
            _workingBitmap = working;
        }

        replacedOriginal?.Dispose();
        replacedWorking?.Dispose();
    }

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

            // Get file size
            var fileInfo = new FileInfo(filePath);

            // Decode before touching editor state: it is the slow part, it must not stall the
            // render thread, and a failed decode then leaves the current image intact.
            using var stream = File.OpenRead(filePath);
            var decoded = SKBitmap.Decode(stream);
            if (decoded is null)
            {
                Logger?.Error(Domain.Services.UnifiedLogging.LogCategory.General, "ImageEditorCore",
                    $"SKBitmap.Decode returned null (unsupported or corrupt format — note: Skia cannot decode TIFF) for {filePath}");
                return false;
            }

            var working = decoded.Copy();
            SwapLoadedBitmaps(decoded, working);

            FileSizeBytes = fileInfo.Length;
            CurrentImagePath = filePath;
            ResetZoom();
            
            // Auto-enable layer mode with the image as the first layer
            EnableLayerMode();

            // Clear stale inpaint base; capture is deferred to first use
            ClearInpaintBase();
            
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

            // Decode before touching editor state — see LoadImage(string).
            var decoded = SKBitmap.Decode(imageData);
            if (decoded is null)
                return false;

            var working = decoded.Copy();
            SwapLoadedBitmaps(decoded, working);

            CurrentImagePath = null;

            // Auto-enable layer mode with the image as the first layer
            EnableLayerMode();

            // Clear stale inpaint base; capture is deferred to first use
            ClearInpaintBase();
            
            OnImageChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Calculates the rectangle to fit the image within the given bounds while maintaining aspect ratio.
    /// </summary>
    public SKRect CalculateFitRect(float containerWidth, float containerHeight)
    {
        lock (_bitmapLock)
        {
            int imageWidth, imageHeight;
            if (_isLayerMode && _layers != null && _layers.Count > 0)
            {
                imageWidth = _layers.Width;
                imageHeight = _layers.Height;
            }
            else if (_workingBitmap is not null)
            {
                imageWidth = _workingBitmap.Width;
                imageHeight = _workingBitmap.Height;
            }
            else
            {
                return SKRect.Empty;
            }

            return FitImageRect(imageWidth, imageHeight, containerWidth, containerHeight, out _);
        }
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
        ClearInpaintBase();

        var wasLayerMode = _isLayerMode;
        var restored = _originalBitmap.Copy();

        SKBitmap? replacedWorking;
        SKBitmap? flattened = null;

        lock (_bitmapLock)
        {
            // If in layer mode, disable it through LayerManager
            if (wasLayerMode && _services is not null)
            {
                flattened = _services.Layers.DisableLayerMode();
            }

            // Reset working bitmap from original
            replacedWorking = _workingBitmap;
            _workingBitmap = restored;
        }

        flattened?.Dispose();
        replacedWorking?.Dispose();

        // If was in layer mode, recreate via LayerManager
        if (wasLayerMode && _services is not null)
        {
            var layerName = !string.IsNullOrEmpty(CurrentImagePath)
                ? Path.GetFileNameWithoutExtension(CurrentImagePath)
                : "Background";

            lock (_bitmapLock)
            {
                _services.Layers.EnableLayerMode(restored, layerName);
            }
        }

        OnImageChanged();
    }

    /// <summary>
    /// Clears the current image.
    /// </summary>
    public void Clear()
    {
        ClearPreview();
        ClearInpaintBase();

        SKBitmap? flattened = null;
        SKBitmap? original;
        SKBitmap? working;

        lock (_bitmapLock)
        {
            // Disable layer mode if active
            if (_isLayerMode && _services is not null)
            {
                flattened = _services.Layers.DisableLayerMode();
            }

            original = _originalBitmap;
            working = _workingBitmap;
            _originalBitmap = null;
            _workingBitmap = null;
        }

        flattened?.Dispose();
        original?.Dispose();
        working?.Dispose();

        CurrentImagePath = null;
        OnImageChanged();
    }

    /// <summary>
    /// Crops the image to the specified rectangle in image coordinates.
    /// When in layer mode, crops all layers.
    /// </summary>
    /// <param name="cropRect">The crop rectangle in image pixel coordinates.</param>
    /// <returns>True if the crop was successful.</returns>
    private bool Crop(SKRectI cropRect)
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
            SKBitmap? replacedWorking = null;

            lock (_bitmapLock)
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
                    replacedWorking = _workingBitmap;
                    _workingBitmap = croppedBitmap;
                }
            }

            replacedWorking?.Dispose();

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
    /// Grows the canvas by the <see cref="CanvasExtendTool"/>'s current extension. Every
    /// layer and the working bitmap are resized; existing content keeps its position
    /// relative to the new top-left offset; new pixels are transparent. Resets the tool.
    /// </summary>
    /// <returns>True when the canvas was extended.</returns>
    public bool ApplyCanvasExtend()
    {
        var tool = CanvasExtendTool;
        if (!HasImage || !tool.HasExtension)
            return false;

        var offsetX = tool.ExtendLeft;
        var offsetY = tool.ExtendTop;
        var newWidth = Width + tool.ExtendLeft + tool.ExtendRight;
        var newHeight = Height + tool.ExtendTop + tool.ExtendBottom;
        FileLogger.Log($"Canvas extend: {Width}x{Height} -> {newWidth}x{newHeight} (offset {offsetX},{offsetY})");

        SKBitmap? replacedWorking = null;
        try
        {
            lock (_bitmapLock)
            {
                if (_isLayerMode && _layers != null)
                {
                    _services?.Layers.ResizeCanvas(newWidth, newHeight, offsetX, offsetY);
                }

                if (_workingBitmap is not null)
                {
                    var grown = new SKBitmap(newWidth, newHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                    grown.Erase(SKColors.Transparent);
                    using (var canvas = new SKCanvas(grown))
                    {
                        canvas.DrawBitmap(_workingBitmap, offsetX, offsetY);
                    }
                    replacedWorking = _workingBitmap;
                    _workingBitmap = grown;
                }
            }
        }
        catch (OutOfMemoryException ex)
        {
            FileLogger.LogError($"Canvas extend to {newWidth}x{newHeight} ran out of memory", ex);
            return false;
        }

        replacedWorking?.Dispose();
        tool.Reset();
        OnImageChanged();
        return true;
    }

    /// <summary>
    /// Saves the current working image to a file.
    /// If in layer mode, flattens all layers before saving.
    /// Delegates file I/O to DocumentService.
    /// </summary>
    /// <param name="filePath">The file path to save to.</param>
    /// <param name="format">
    /// The encoder to use. Pass <c>null</c> (the default) to derive it from the file extension,
    /// falling back to PNG for an unknown or missing one. An explicit format always wins: callers
    /// like "Export as JPEG" mean it regardless of what the user typed in the save dialog.
    /// </param>
    /// <param name="quality">Quality for lossy formats (0-100).</param>
    /// <returns>True if saved successfully.</returns>
    public bool SaveImage(string filePath, SKEncodedImageFormat? format = null, int quality = 95)
    {
        FileLogger.LogEntry($"filePath={filePath}, format={format?.ToString() ?? "(from extension)"}, quality={quality}");
        
        // Get the bitmap to save - flatten layers if in layer mode
        SKBitmap? bitmapToSave = null;
        bool needsDispose = false;
        
        if (_isLayerMode && _layers != null && _layers.Count > 0)
        {
            FileLogger.Log("Layer mode active, flattening layers for save...");
            bitmapToSave = _services?.Layers.Flatten();
            needsDispose = true;
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

        // Determine format from extension only when the caller did not ask for a specific one
        var resolvedFormat = format
            ?? _services?.Document.GetFormatFromExtension(filePath)
            ?? SKEncodedImageFormat.Png;

        FileLogger.Log($"Bitmap to save size: {bitmapToSave.Width}x{bitmapToSave.Height}, encoding as {resolvedFormat}");

        try
        {
            var result = _services!.Document.Save(bitmapToSave, filePath, resolvedFormat, quality);

            if (needsDispose) bitmapToSave.Dispose();
            FileLogger.Log(result ? "Save completed successfully" : "Save failed");
            FileLogger.LogExit(result.ToString());
            return result;
        }
        catch (Exception ex)
        {
            FileLogger.LogError($"Exception during save to {filePath}", ex);
            if (needsDispose) bitmapToSave.Dispose();
            FileLogger.LogExit("false");
            return false;
        }
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
                // Fit honours an active extension tool's frame so it never runs off screen.
                imageRect = FitImageRect(imageWidth, imageHeight, canvasWidth, canvasHeight, out var fitScale);
                // Write through SetFitModeWithZoom, NOT the _zoomLevel setter: that setter goes to
                // Viewport.ZoomLevel, which clears IsFitMode, so fit mode used to switch itself off
                // on the very first render after load. Compare against the CLAMPED value: a fit
                // scale below MinZoom (huge image, small canvas) would otherwise differ from the
                // stored zoom on every frame and re-fire Changed each render.
                var viewport = _services?.Viewport;
                if (viewport is not null)
                {
                    var clampedFit = Math.Clamp(fitScale, viewport.MinZoom, viewport.MaxZoom);
                    if (Math.Abs(viewport.ZoomLevel - clampedFit) > 0.0001f)
                        viewport.SetFitModeWithZoom(clampedFit);
                }
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
            if (_isPreviewActive && _previewBitmap is not null)
            {
                // Preview takes priority over layer compositing (e.g., color balance live preview)
                canvas.DrawBitmap(_previewBitmap, imageRect);
            }
            else if (_isLayerMode && _layers != null)
            {
                LayerCompositor.CompositeToCanvas(canvas, _layers, imageRect);
            }
            else
            {
                if (_workingBitmap != null)
                {
                    canvas.DrawBitmap(_workingBitmap, imageRect);
                }
            }

            // Update crop tool with current image bounds and render overlay
            CropTool.SetImageBounds(imageRect);
            CropTool.ImagePixelWidth = imageWidth;
            CropTool.ImagePixelHeight = imageHeight;
            CropTool.Render(canvas, new SKRect(0, 0, canvasWidth, canvasHeight));

            // Update drawing tool with current image bounds and render overlay.
            // ImagePixelWidth lets the tool size the brush in image pixels (zoom-independent).
            DrawingTool.SetImageBounds(imageRect);
            DrawingTool.ImagePixelWidth = imageWidth;
            DrawingTool.Render(canvas);

            // Update shape tool with current image bounds and render overlay
            ShapeTool.SetImageBounds(imageRect);
            ShapeTool.ImagePixelWidth = imageWidth;
            ShapeTool.Render(canvas);

            // Update text tool with current image bounds and render overlay
            TextTool.SetImageBounds(imageRect);
            TextTool.ImagePixelWidth = imageWidth;
            TextTool.ImagePixelHeight = imageHeight;
            TextTool.Render(canvas);

            // Update outpaint tool with current image bounds and render overlay
            OutpaintTool.SetImageBounds(imageRect);
            OutpaintTool.ImagePixelWidth = imageWidth;
            OutpaintTool.ImagePixelHeight = imageHeight;
            OutpaintTool.Render(canvas, new SKRect(0, 0, canvasWidth, canvasHeight));

            // Update canvas extend tool with current image bounds and render overlay
            CanvasExtendTool.SetImageBounds(imageRect);
            CanvasExtendTool.ImagePixelWidth = imageWidth;
            CanvasExtendTool.ImagePixelHeight = imageHeight;
            CanvasExtendTool.Render(canvas, new SKRect(0, 0, canvasWidth, canvasHeight));

            _lastImageRect = imageRect;
            return imageRect;
        }
    }

    /// <summary>
    /// Fits the virtual canvas (image plus the extension on each side) into the container
    /// shrunk by <paramref name="margin"/> on every side, centred, and returns the rectangle
    /// the <b>image</b> occupies inside that frame plus the scale (image px → screen px).
    /// With zero extension and zero margin it equals the plain image fit.
    /// </summary>
    internal static (SKRect ImageRect, float Scale) CalculateFitRectWithExtension(
        int imageWidth, int imageHeight,
        int extendLeft, int extendTop, int extendRight, int extendBottom,
        float margin, float containerWidth, float containerHeight)
    {
        var virtualWidth = imageWidth + extendLeft + extendRight;
        var virtualHeight = imageHeight + extendTop + extendBottom;
        var availableWidth = Math.Max(1f, containerWidth - 2f * margin);
        var availableHeight = Math.Max(1f, containerHeight - 2f * margin);

        var scale = Math.Min(availableWidth / virtualWidth, availableHeight / virtualHeight);

        var frameWidth = virtualWidth * scale;
        var frameHeight = virtualHeight * scale;
        var frameX = (containerWidth - frameWidth) / 2f;
        var frameY = (containerHeight - frameHeight) / 2f;

        var x = frameX + extendLeft * scale;
        var y = frameY + extendTop * scale;
        return (new SKRect(x, y, x + imageWidth * scale, y + imageHeight * scale), scale);
    }

    /// <summary>The extension tool whose frame the viewport must keep on screen, if any.</summary>
    private CanvasExtensionTool? ActiveExtensionTool =>
        CanvasExtendTool.IsActive ? CanvasExtendTool
        : OutpaintTool.IsActive ? OutpaintTool
        : null;

    /// <summary>
    /// Fit rectangle for the image, honouring the active extension tool's frame and margin.
    /// </summary>
    private SKRect FitImageRect(int imageWidth, int imageHeight, float containerWidth, float containerHeight, out float scale)
    {
        var tool = ActiveExtensionTool;
        if (tool is null)
        {
            var plain = CalculateFitRectInternal(imageWidth, imageHeight, containerWidth, containerHeight);
            scale = plain.Width / imageWidth;
            return plain;
        }

        var (rect, s) = CalculateFitRectWithExtension(
            imageWidth, imageHeight,
            tool.ExtendLeft, tool.ExtendTop, tool.ExtendRight, tool.ExtendBottom,
            tool.FitMargin, containerWidth, containerHeight);
        scale = s;
        return rect;
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
    public void ZoomIn() => _services?.Viewport.ZoomIn();

    /// <summary>
    /// Decreases the zoom level to zoom out.
    /// </summary>
    public void ZoomOut() => _services?.Viewport.ZoomOut();

    /// <summary>
    /// Sets the zoom level to fit the image within the canvas.
    /// </summary>
    public void ZoomToFit() => _services?.Viewport.ZoomToFit();

    /// <summary>
    /// Sets fit mode with a pre-calculated zoom level.
    /// Used when the caller knows the canvas dimensions and can calculate the fit zoom.
    /// </summary>
    /// <param name="fitZoom">The calculated zoom level for fit mode.</param>
    public void SetFitModeWithZoom(float fitZoom)
    {
        _services?.Viewport.SetFitModeWithZoom(fitZoom);
    }

    /// <summary>
    /// Resets the zoom level to 100% and pans to the original position.
    /// </summary>
    public void ZoomToActual()
    {
        _services?.Viewport.ZoomToActual();
    }

    /// <summary>
    /// Resets the zoom level, pan offsets, and fit mode to their initial states.
    /// </summary>
    public void ResetZoom()
    {
        _services?.Viewport.Reset();
    }

    /// <summary>
    /// Gets the screen rectangle of the image from the last render pass.
    /// Used by tools that need screen-to-image coordinate mapping.
    /// </summary>
    public SKRect GetCurrentImageRect() => _lastImageRect;

    /// <summary>
    /// Samples the pixel color at a display-space point by mapping it to image coordinates.
    /// Returns null if the point is outside the image bounds.
    /// </summary>
    public SKColor? PickColorAtPoint(SKPoint displayPoint)
    {
        var imageRect = _lastImageRect;
        if (imageRect.IsEmpty || !imageRect.Contains(displayPoint))
            return null;

        int imageWidth, imageHeight;
        if (_isLayerMode && _layers is not null)
        {
            imageWidth = _layers.Width;
            imageHeight = _layers.Height;
        }
        else if (_workingBitmap is not null)
        {
            imageWidth = _workingBitmap.Width;
            imageHeight = _workingBitmap.Height;
        }
        else
        {
            return null;
        }

        // Map display-space point to image-space pixel coordinates
        var pixelX = (int)((displayPoint.X - imageRect.Left) / imageRect.Width * imageWidth);
        var pixelY = (int)((displayPoint.Y - imageRect.Top) / imageRect.Height * imageHeight);

        pixelX = Math.Clamp(pixelX, 0, imageWidth - 1);
        pixelY = Math.Clamp(pixelY, 0, imageHeight - 1);

        // Sample from flattened layers or working bitmap
        if (_isLayerMode && _layers is not null)
        {
            using var flattened = LayerCompositor.CreateFlattenedBitmap(_layers);
            return flattened.GetPixel(pixelX, pixelY);
        }

        return _workingBitmap?.GetPixel(pixelX, pixelY);
    }

    /// <summary>
    /// Pans the image by the specified delta values.
    /// </summary>
    /// <param name="deltaX">The delta value for the X axis.</param>
    /// <param name="deltaY">The delta value for the Y axis.</param>
    public void Pan(float deltaX, float deltaY)
    {
        _services?.Viewport.Pan(deltaX, deltaY);
    }

    #region Shared Operation Helpers

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
        SKBitmap? replaced = null;

        // Re-entrant: several callers already hold the render lock around a wider operation.
        lock (_bitmapLock)
        {
            if (_isLayerMode && _layers?.ActiveLayer != null)
            {
                // When in layer mode, we need to replace the active layer's bitmap
                // The Layer class doesn't expose a bitmap setter, so we use the internal method
                _layers.ActiveLayer.ReplaceBitmap(newBitmap);
            }
            else
            {
                replaced = _workingBitmap;
                _workingBitmap = newBitmap;
            }
        }

        replaced?.Dispose();
    }

    #endregion Shared Operation Helpers

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

    #endregion Preview Management

    #region Drawing

    /// <summary>
    /// Applies a drawing stroke to the working bitmap or active layer.
    /// </summary>
    /// <param name="normalizedPoints">Points in normalized coordinates (0-1).</param>
    /// <param name="color">The stroke color.</param>
    /// <param name="brushSize">The brush size normalized to image width (0-1); multiplied by the bitmap width to get image pixels.</param>
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
                if (!targetLayer.CanEdit || targetLayer.IsInpaintMask)
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
                if (!targetLayer.CanEdit || targetLayer.IsInpaintMask)
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

                // Apply rotation around the shape center if needed
                if (Math.Abs(shapeData.RotationDegrees) > 0.01f)
                {
                    var center = new SKPoint((start.X + end.X) / 2f, (start.Y + end.Y) / 2f);
                    canvas.Save();
                    canvas.RotateDegrees(shapeData.RotationDegrees, center.X, center.Y);
                }

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

                if (Math.Abs(shapeData.RotationDegrees) > 0.01f)
                {
                    canvas.Restore();
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

    #endregion Shape Drawing

    #region Text Drawing

    /// <summary>
    /// Applies a text element as a new layer on the image.
    /// Each text element creates its own layer for later editing.
    /// </summary>
    /// <param name="textData">The text element data to apply.</param>
    /// <returns>True if the text was applied successfully.</returns>
    public bool ApplyText(TextElementData textData)
    {
        if (textData is null || string.IsNullOrWhiteSpace(textData.Text))
            return false;

        lock (_bitmapLock)
        {
            if (!_isLayerMode || _layers == null || _services is null)
                return false;

            try
            {
                var width = _layers.Width;
                var height = _layers.Height;

                // Create a new transparent layer for the text
                var textLayer = _services.Layers.AddLayer($"Text: {textData.Text[..Math.Min(textData.Text.Length, 20)]}");
                if (textLayer?.Bitmap is null) return false;

                // Convert normalized coordinates to image pixel coordinates
                var topLeft = new SKPoint(
                    textData.NormalizedTopLeft.X * width,
                    textData.NormalizedTopLeft.Y * height);
                var bottomRight = new SKPoint(
                    textData.NormalizedBottomRight.X * width,
                    textData.NormalizedBottomRight.Y * height);
                var rect = new SKRect(
                    Math.Min(topLeft.X, bottomRight.X),
                    Math.Min(topLeft.Y, bottomRight.Y),
                    Math.Max(topLeft.X, bottomRight.X),
                    Math.Max(topLeft.Y, bottomRight.Y));

                // Scale font size and outline width from normalized to image coordinates
                var scaledFontSize = textData.FontSize * width;
                var scaledOutlineWidth = textData.OutlineWidth * width;

                using var canvas = new SKCanvas(textLayer.Bitmap);

                // Apply rotation around the center of the text rect
                if (Math.Abs(textData.RotationDegrees) > 0.01f)
                {
                    var centerX = (rect.Left + rect.Right) / 2f;
                    var centerY = (rect.Top + rect.Bottom) / 2f;
                    canvas.RotateDegrees(textData.RotationDegrees, centerX, centerY);
                }

                TextTool.RenderText(
                    canvas,
                    textData.Text,
                    rect,
                    scaledFontSize,
                    textData.FontFamily,
                    textData.IsBold,
                    textData.IsItalic,
                    textData.TextColor,
                    textData.OutlineColor,
                    scaledOutlineWidth);

                canvas.Flush();
                textLayer.NotifyContentChanged();
            }
            catch
            {
                return false;
            }
        }

        OnImageChanged();
        return true;
    }

    #endregion Text Drawing

    #region Save with Layers

    /// <summary>
    /// Saves layers to a multi-page TIFF file.
    /// </summary>
    /// <param name="filePath">Output file path.</param>
    /// <returns>True if saved successfully.</returns>
    public bool SaveLayeredTiff(string filePath)
    {
        if (!_isLayerMode || _layers == null)
            return false;

        return TiffExporter.SaveLayeredTiff(_layers, filePath, Logger);
    }

    /// <summary>
    /// Loads a layered TIFF file.
    /// </summary>
    /// <param name="filePath">TIFF file path.</param>
    /// <returns>True if loaded successfully.</returns>
    public bool LoadLayeredTiff(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || _services is null)
            return false;

        var loadedLayers = TiffExporter.LoadLayeredTiff(filePath, Logger);
        if (loadedLayers == null || loadedLayers.Count == 0)
            return false;

        ClearPreview(raiseEvent: false);

        var firstLayer = loadedLayers[0];
        if (firstLayer.Bitmap is null)
        {
            loadedLayers.Dispose();
            return false;
        }

        // Store a flattened copy as the original for Reset support
        var flattened = loadedLayers.Flatten();

        SKBitmap? previousFlattened = null;
        SKBitmap? replacedOriginal;
        SKBitmap? replacedWorking;

        lock (_bitmapLock)
        {
            // Disable existing layer mode if active
            if (_isLayerMode)
            {
                previousFlattened = _services.Layers.DisableLayerMode();
            }

            replacedOriginal = _originalBitmap;
            replacedWorking = _workingBitmap;
            _originalBitmap = flattened;
            _workingBitmap = flattened?.Copy();

            // Initialize layer mode from the loaded stack via LayerManager
            // We enable with the first layer, then add the rest
            _services.Layers.EnableLayerMode(firstLayer.Bitmap.Copy(), firstLayer.Name);

            for (var i = 1; i < loadedLayers.Count; i++)
            {
                var layer = loadedLayers[i];
                if (layer.Bitmap is null) continue;
                _services.Layers.AddLayerFromBitmap(layer.Bitmap.Copy(), layer.Name);
            }
        }

        previousFlattened?.Dispose();
        replacedOriginal?.Dispose();
        replacedWorking?.Dispose();

        // Get file size
        var fileInfo = new FileInfo(filePath);
        FileSizeBytes = fileInfo.Length;

        // Dispose the original loaded stack (we've copied the bitmaps)
        loadedLayers.Dispose();

        CurrentImagePath = filePath;

        ResetZoom();
        OnImageChanged();
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
            SKBitmap? original;
            SKBitmap? working;
            SKBitmap? preview;
            SKBitmap? inpaintBase;

            lock (_bitmapLock)
            {
                original = _originalBitmap;
                working = _workingBitmap;
                preview = _previewBitmap;
                inpaintBase = _inpaintBaseBitmap;

                _originalBitmap = null;
                _workingBitmap = null;
                _previewBitmap = null;
                _inpaintBaseBitmap = null;
                _isPreviewActive = false;

                // Unsubscribe all service event handlers
                if (_services is not null)
                {
                    _services.Layers.ContentChanged -= OnLayersContentChanged;
                    _services.Layers.LayersChanged -= OnLayersCollectionChanged;
                    _services.Layers.LayerModeChanged -= OnLayerModeChanged;
                    _services.Viewport.Changed -= OnViewportChanged;
                    _services.Dispose();
                }
            }

            original?.Dispose();
            working?.Dispose();
            preview?.Dispose();
            inpaintBase?.Dispose();
        }
        _disposed = true;
    }
}
