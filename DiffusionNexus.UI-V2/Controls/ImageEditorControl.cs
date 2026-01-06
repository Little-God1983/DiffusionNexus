using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace DiffusionNexus.UI.Controls;

/// <summary>
/// Avalonia control wrapper for the ImageEditorCore.
/// Provides a canvas-based image display with SkiaSharp rendering.
/// </summary>
public class ImageEditorControl : Control
{
    private readonly ImageEditor.ImageEditorCore _editorCore;
    private Point _lastPanPoint;
    private bool _isPanning;

    /// <summary>
    /// Defines the <see cref="ImagePath"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> ImagePathProperty =
        AvaloniaProperty.Register<ImageEditorControl, string?>(nameof(ImagePath));

    /// <summary>
    /// Defines the <see cref="CanvasBackground"/> property.
    /// </summary>
    public static readonly StyledProperty<Color> CanvasBackgroundProperty =
        AvaloniaProperty.Register<ImageEditorControl, Color>(nameof(CanvasBackground), Color.FromRgb(0x1A, 0x1A, 0x1A));

    /// <summary>
    /// Defines the <see cref="IsCropToolActive"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsCropToolActiveProperty =
        AvaloniaProperty.Register<ImageEditorControl, bool>(nameof(IsCropToolActive));

    /// <summary>
    /// Defines the <see cref="ZoomLevel"/> property.
    /// </summary>
    public static readonly StyledProperty<float> ZoomLevelProperty =
        AvaloniaProperty.Register<ImageEditorControl, float>(nameof(ZoomLevel), 1f);

    /// <summary>
    /// Gets or sets the path to the image to display.
    /// </summary>
    public string? ImagePath
    {
        get => GetValue(ImagePathProperty);
        set => SetValue(ImagePathProperty, value);
    }

    /// <summary>
    /// Gets or sets the background color of the canvas.
    /// </summary>
    public Color CanvasBackground
    {
        get => GetValue(CanvasBackgroundProperty);
        set => SetValue(CanvasBackgroundProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the crop tool is active.
    /// </summary>
    public bool IsCropToolActive
    {
        get => GetValue(IsCropToolActiveProperty);
        set => SetValue(IsCropToolActiveProperty, value);
    }

    /// <summary>
    /// Gets or sets the zoom level (1.0 = 100%).
    /// </summary>
    public float ZoomLevel
    {
        get => GetValue(ZoomLevelProperty);
        set => SetValue(ZoomLevelProperty, value);
    }

    /// <summary>
    /// Gets the zoom percentage.
    /// </summary>
    public int ZoomPercentage => _editorCore.ZoomPercentage;

    /// <summary>
    /// Gets whether fit mode is active.
    /// </summary>
    public bool IsFitMode => _editorCore.IsFitMode;

    /// <summary>
    /// Gets the image DPI.
    /// </summary>
    public int ImageDpi => _editorCore.ImageDpi;

    /// <summary>
    /// Gets the file size in bytes.
    /// </summary>
    public long FileSizeBytes => _editorCore.FileSizeBytes;

    /// <summary>
    /// Gets the underlying editor core for advanced operations.
    /// </summary>
    public ImageEditor.ImageEditorCore EditorCore => _editorCore;

    /// <summary>
    /// Gets whether an image is currently loaded.
    /// </summary>
    public bool HasImage => _editorCore.HasImage;

    /// <summary>
    /// Gets the current image width.
    /// </summary>
    public int ImageWidth => _editorCore.Width;

    /// <summary>
    /// Gets the current image height.
    /// </summary>
    public int ImageHeight => _editorCore.Height;

    /// <summary>
    /// Event raised when the image changes.
    /// </summary>
    public event EventHandler? ImageChanged;

    /// <summary>
    /// Event raised when crop is applied.
    /// </summary>
    public event EventHandler? CropApplied;

    /// <summary>
    /// Event raised when zoom changes.
    /// </summary>
    public event EventHandler? ZoomChanged;

    public ImageEditorControl()
    {
        _editorCore = new ImageEditor.ImageEditorCore();
        _editorCore.ImageChanged += OnEditorCoreImageChanged;
        _editorCore.CropTool.CropRegionChanged += OnCropRegionChanged;
        _editorCore.ZoomChanged += OnEditorCoreZoomChanged;
        ClipToBounds = true;
        Focusable = true;
    }

    static ImageEditorControl()
    {
        AffectsRender<ImageEditorControl>(ImagePathProperty, CanvasBackgroundProperty, IsCropToolActiveProperty, ZoomLevelProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ImagePathProperty)
        {
            var newPath = change.NewValue as string;
            if (!string.IsNullOrEmpty(newPath))
            {
                _editorCore.LoadImage(newPath);
            }
            else
            {
                _editorCore.Clear();
            }
        }
        else if (change.Property == IsCropToolActiveProperty)
        {
            _editorCore.CropTool.IsActive = (bool)change.NewValue!;
            InvalidateVisual();
        }
        else if (change.Property == ZoomLevelProperty)
        {
            _editorCore.ZoomLevel = (float)change.NewValue!;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!_editorCore.HasImage) return;

        var point = e.GetPosition(this);
        var skPoint = new SKPoint((float)point.X, (float)point.Y);
        var props = e.GetCurrentPoint(this).Properties;

        // Middle mouse button for panning
        if (props.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _lastPanPoint = point;
            e.Handled = true;
            return;
        }

        if (_editorCore.CropTool.OnPointerPressed(skPoint))
        {
            e.Handled = true;
            InvalidateVisual();
        }

        Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_editorCore.HasImage) return;

        var point = e.GetPosition(this);
        var skPoint = new SKPoint((float)point.X, (float)point.Y);

        // Handle panning
        if (_isPanning)
        {
            var deltaX = (float)(point.X - _lastPanPoint.X);
            var deltaY = (float)(point.Y - _lastPanPoint.Y);
            _editorCore.Pan(deltaX, deltaY);
            _lastPanPoint = point;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_editorCore.CropTool.OnPointerMoved(skPoint))
        {
            e.Handled = true;
            InvalidateVisual();
        }

        // Update cursor based on handle
        UpdateCursor(skPoint);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isPanning)
        {
            _isPanning = false;
            e.Handled = true;
            return;
        }

        if (_editorCore.CropTool.OnPointerReleased())
        {
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (!_editorCore.HasImage) return;

        // Zoom with mouse wheel
        if (e.Delta.Y > 0)
        {
            _editorCore.ZoomIn();
        }
        else if (e.Delta.Y < 0)
        {
            _editorCore.ZoomOut();
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (!_editorCore.HasImage) return;

        // Apply crop with C or Enter when crop tool is active
        if (_editorCore.CropTool.IsActive && _editorCore.CropTool.HasCropRegion)
        {
            if (e.Key == Key.C || e.Key == Key.Enter)
            {
                ApplyCrop();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _editorCore.CropTool.ClearCropRegion();
                InvalidateVisual();
                e.Handled = true;
            }
        }
    }

    private void UpdateCursor(SKPoint point)
    {
        if (!_editorCore.CropTool.IsActive)
        {
            Cursor = Cursor.Default;
            return;
        }

        var handle = _editorCore.CropTool.GetCursorForPoint(point);
        Cursor = handle switch
        {
            ImageEditor.CropHandle.TopLeft or ImageEditor.CropHandle.BottomRight => new Cursor(StandardCursorType.SizeAll),
            ImageEditor.CropHandle.TopRight or ImageEditor.CropHandle.BottomLeft => new Cursor(StandardCursorType.SizeAll),
            ImageEditor.CropHandle.Top or ImageEditor.CropHandle.Bottom => new Cursor(StandardCursorType.SizeNorthSouth),
            ImageEditor.CropHandle.Left or ImageEditor.CropHandle.Right => new Cursor(StandardCursorType.SizeWestEast),
            ImageEditor.CropHandle.Move => new Cursor(StandardCursorType.SizeAll),
            _ => new Cursor(StandardCursorType.Cross)
        };
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var bgColor = CanvasBackground;
        var skBgColor = new SKColor(bgColor.R, bgColor.G, bgColor.B, bgColor.A);

        context.Custom(new ImageEditorDrawOperation(
            new Rect(0, 0, bounds.Width, bounds.Height),
            _editorCore,
            skBgColor));
    }

    /// <summary>
    /// Loads an image from the specified file path.
    /// </summary>
    public bool LoadImage(string filePath)
    {
        var result = _editorCore.LoadImage(filePath);
        if (result)
        {
            SetCurrentValue(ImagePathProperty, filePath);
        }
        return result;
    }

    /// <summary>
    /// Loads an image from a byte array.
    /// </summary>
    public bool LoadImage(byte[] imageData)
    {
        var result = _editorCore.LoadImage(imageData);
        if (result)
        {
            SetCurrentValue(ImagePathProperty, null);
        }
        return result;
    }

    /// <summary>
    /// Clears the current image.
    /// </summary>
    public void ClearImage()
    {
        _editorCore.Clear();
        SetCurrentValue(ImagePathProperty, null);
    }

    /// <summary>
    /// Resets to the original image.
    /// </summary>
    public void ResetToOriginal()
    {
        _editorCore.ResetToOriginal();
    }

    /// <summary>
    /// Applies the current crop selection.
    /// </summary>
    public bool ApplyCrop()
    {
        var result = _editorCore.ApplyCrop();
        if (result)
        {
            CropApplied?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
        return result;
    }

    /// <summary>
    /// Activates the crop tool.
    /// </summary>
    public void ActivateCropTool()
    {
        IsCropToolActive = true;
    }

    /// <summary>
    /// Deactivates the crop tool.
    /// </summary>
    public void DeactivateCropTool()
    {
        IsCropToolActive = false;
    }

    /// <summary>
    /// Zooms in.
    /// </summary>
    public void ZoomIn()
    {
        _editorCore.ZoomIn();
        InvalidateVisual();
    }

    /// <summary>
    /// Zooms out.
    /// </summary>
    public void ZoomOut()
    {
        _editorCore.ZoomOut();
        InvalidateVisual();
    }

    /// <summary>
    /// Zooms to fit the canvas.
    /// </summary>
    public void ZoomToFit()
    {
        _editorCore.ZoomToFit();
        InvalidateVisual();
    }

    /// <summary>
    /// Zooms to 100%.
    /// </summary>
    public void ZoomToActual()
    {
        _editorCore.ZoomToActual();
        InvalidateVisual();
    }

    /// <summary>
    /// Sets zoom level.
    /// </summary>
    public void SetZoom(float level)
    {
        _editorCore.ZoomLevel = level;
        InvalidateVisual();
    }

    private void OnEditorCoreImageChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
        ImageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCropRegionChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
    }

    private void OnEditorCoreZoomChanged(object? sender, EventArgs e)
    {
        SetCurrentValue(ZoomLevelProperty, _editorCore.ZoomLevel);
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _editorCore.ImageChanged -= OnEditorCoreImageChanged;
        _editorCore.CropTool.CropRegionChanged -= OnCropRegionChanged;
        _editorCore.ZoomChanged -= OnEditorCoreZoomChanged;
        _editorCore.Dispose();
    }

    /// <summary>
    /// Custom draw operation for SkiaSharp rendering.
    /// </summary>
    private sealed class ImageEditorDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly ImageEditor.ImageEditorCore _editorCore;
        private readonly SKColor _backgroundColor;

        public ImageEditorDrawOperation(Rect bounds, ImageEditor.ImageEditorCore editorCore, SKColor backgroundColor)
        {
            _bounds = bounds;
            _editorCore = editorCore;
            _backgroundColor = backgroundColor;
        }

        public Rect Bounds => _bounds;

        public bool HitTest(Point p) => _bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other)
        {
            return other is ImageEditorDrawOperation op &&
                   op._bounds == _bounds &&
                   op._editorCore == _editorCore;
        }

        public void Dispose()
        {
            // Nothing to dispose
        }

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
            if (leaseFeature is null)
                return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            _editorCore.RenderWithZoom(
                canvas,
                (float)_bounds.Width,
                (float)_bounds.Height,
                _backgroundColor);
        }
    }
}
