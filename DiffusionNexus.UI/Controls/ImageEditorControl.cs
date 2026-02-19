using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using DiffusionNexus.UI.ImageEditor.Services;
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
    private bool _suppressImagePathLoad;
    private bool _isDetachedFromTree;

    // Inpaint brush state
    private bool _isInpaintingToolActive;
    private float _inpaintBrushSize = 40f;
    private bool _isInpaintPainting;
    private SKPoint _inpaintLastPoint;
    private readonly List<SKPoint> _inpaintStrokePoints = [];
    private SKPoint _inpaintCursorPosition;
    private bool _hasInpaintCursorPosition;

    // Text tool state
    private bool _isTextToolActive;

    // Outpaint tool state
    private bool _isOutpaintToolActive;

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
    /// Defines the <see cref="IsDrawingToolActive"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsDrawingToolActiveProperty =
        AvaloniaProperty.Register<ImageEditorControl, bool>(nameof(IsDrawingToolActive));

    /// <summary>
    /// Defines the <see cref="SelectedShapeType"/> property.
    /// </summary>
    public static readonly StyledProperty<ImageEditor.ShapeType> SelectedShapeTypeProperty =
        AvaloniaProperty.Register<ImageEditorControl, ImageEditor.ShapeType>(nameof(SelectedShapeType));

    /// <summary>
    /// Defines the <see cref="ShapeFillMode"/> property.
    /// </summary>
    public static readonly StyledProperty<ImageEditor.ShapeFillMode> ShapeFillModeProperty =
        AvaloniaProperty.Register<ImageEditorControl, ImageEditor.ShapeFillMode>(nameof(ShapeFillMode));

    /// <summary>
    /// Defines the <see cref="ShapeStrokeColor"/> property.
    /// </summary>
    public static readonly StyledProperty<Color> ShapeStrokeColorProperty =
        AvaloniaProperty.Register<ImageEditorControl, Color>(nameof(ShapeStrokeColor), Colors.White);

    /// <summary>
    /// Defines the <see cref="ShapeFillColor"/> property.
    /// </summary>
    public static readonly StyledProperty<Color> ShapeFillColorProperty =
        AvaloniaProperty.Register<ImageEditorControl, Color>(nameof(ShapeFillColor), Colors.White);

    /// <summary>
    /// Defines the <see cref="ShapeStrokeWidth"/> property.
    /// </summary>
    public static readonly StyledProperty<float> ShapeStrokeWidthProperty =
        AvaloniaProperty.Register<ImageEditorControl, float>(nameof(ShapeStrokeWidth), 3f);

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
    /// Gets or sets whether the drawing tool is active.
    /// </summary>
    public bool IsDrawingToolActive
    {
        get => GetValue(IsDrawingToolActiveProperty);
        set => SetValue(IsDrawingToolActiveProperty, value);
    }

    /// <summary>
    /// Gets or sets the selected shape type.
    /// </summary>
    public ImageEditor.ShapeType SelectedShapeType
    {
        get => GetValue(SelectedShapeTypeProperty);
        set => SetValue(SelectedShapeTypeProperty, value);
    }

    /// <summary>
    /// Gets or sets the shape fill mode.
    /// </summary>
    public ImageEditor.ShapeFillMode ShapeFillMode
    {
        get => GetValue(ShapeFillModeProperty);
        set => SetValue(ShapeFillModeProperty, value);
    }

    /// <summary>
    /// Gets or sets the shape stroke color.
    /// </summary>
    public Color ShapeStrokeColor
    {
        get => GetValue(ShapeStrokeColorProperty);
        set => SetValue(ShapeStrokeColorProperty, value);
    }

    /// <summary>
    /// Gets or sets the shape fill color.
    /// </summary>
    public Color ShapeFillColor
    {
        get => GetValue(ShapeFillColorProperty);
        set => SetValue(ShapeFillColorProperty, value);
    }

    /// <summary>
    /// Gets or sets the shape stroke width.
    /// </summary>
    public float ShapeStrokeWidth
    {
        get => GetValue(ShapeStrokeWidthProperty);
        set => SetValue(ShapeStrokeWidthProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the inpainting tool is active.
    /// </summary>
    public bool IsInpaintingToolActive
    {
        get => _isInpaintingToolActive;
        set
        {
            _isInpaintingToolActive = value;
            if (!value)
            {
                _isInpaintPainting = false;
                _inpaintStrokePoints.Clear();
                _hasInpaintCursorPosition = false;
                Cursor = Cursor.Default;
            }
            else
            {
                Cursor = new Cursor(StandardCursorType.None);
            }
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Gets or sets whether the text tool is active.
    /// </summary>
    public bool IsTextToolActive
    {
        get => _isTextToolActive;
        set
        {
            _isTextToolActive = value;
            _editorCore.TextTool.IsActive = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Gets or sets whether the outpaint tool is active.
    /// </summary>
    public bool IsOutpaintToolActive
    {
        get => _isOutpaintToolActive;
        set
        {
            _isOutpaintToolActive = value;
            _editorCore.OutpaintTool.IsActive = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Gets or sets the inpainting brush size in display pixels.
    /// </summary>
    public float InpaintBrushSize
    {
        get => _inpaintBrushSize;
        set => _inpaintBrushSize = value;
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
    /// Wires the shared EditorServices into the underlying editor core.
    /// Must be called once after the ViewModel is available.
    /// </summary>
    public void SetEditorServices(EditorServices services)
    {
        _editorCore.SetServices(services);
    }

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

    /// <summary>
    /// Event raised when the placed-shape state changes (placed or cleared).
    /// </summary>
    public event EventHandler? PlacedShapeStateChanged;

    /// <summary>
    /// Event raised when the placed-text state changes (placed or cleared).
    /// </summary>
    public event EventHandler? PlacedTextStateChanged;

    /// <summary>
    /// Event raised when the inpaint mask is created or modified.
    /// The view should sync layers after this event.
    /// </summary>
    public event EventHandler? InpaintMaskChanged;

    /// <summary>
    /// Event raised when the inpaint brush size is changed via Shift+wheel.
    /// </summary>
    public event EventHandler<float>? InpaintBrushSizeChanged;

    /// <summary>
    /// Event raised when the user presses Ctrl+Enter while the inpaint tool is active.
    /// </summary>
    public event EventHandler? InpaintGenerateRequested;

    public ImageEditorControl()
    {
        _editorCore = new ImageEditor.ImageEditorCore();
        ClipToBounds = true;
        Focusable = true;
    }

    static ImageEditorControl()
    {
        AffectsRender<ImageEditorControl>(
            ImagePathProperty, 
            CanvasBackgroundProperty, 
            IsCropToolActiveProperty, 
            IsDrawingToolActiveProperty, 
            SelectedShapeTypeProperty,
            ShapeFillModeProperty,
            ShapeStrokeColorProperty,
            ShapeFillColorProperty,
            ShapeStrokeWidthProperty,
            ZoomLevelProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ImagePathProperty)
        {
            if (_suppressImagePathLoad) return;

            var newPath = change.NewValue as string;
            if (!string.IsNullOrEmpty(newPath))
            {
                // Skip redundant reload when binding restores the same path
                // after a tab-switch reattach — the bitmap is still in memory.
                if (_editorCore.HasImage &&
                    string.Equals(newPath, _editorCore.CurrentImagePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _editorCore.LoadImage(newPath);
            }
            else if (!_isDetachedFromTree)
            {
                // Only clear when the control is in the visual tree.
                // During tab switches the binding deactivates and pushes null;
                // clearing here would destroy the loaded image.
                _editorCore.Clear();
            }
        }
        else if (change.Property == IsCropToolActiveProperty)
        {
            _editorCore.CropTool.IsActive = (bool)change.NewValue!;
            InvalidateVisual();
        }
        else if (change.Property == IsDrawingToolActiveProperty)
        {
            var isActive = (bool)change.NewValue!;
            _editorCore.DrawingTool.IsActive = isActive && SelectedShapeType == ImageEditor.ShapeType.Freehand;
            _editorCore.ShapeTool.IsActive = isActive && SelectedShapeType != ImageEditor.ShapeType.Freehand;
            InvalidateVisual();
        }
        else if (change.Property == SelectedShapeTypeProperty)
        {
            var shapeType = (ImageEditor.ShapeType)change.NewValue!;
            _editorCore.ShapeTool.ShapeType = shapeType;
            // Update tool active states based on shape type
            if (IsDrawingToolActive)
            {
                _editorCore.DrawingTool.IsActive = shapeType == ImageEditor.ShapeType.Freehand;
                _editorCore.ShapeTool.IsActive = shapeType != ImageEditor.ShapeType.Freehand;
            }
            InvalidateVisual();
        }
        else if (change.Property == ShapeFillModeProperty)
        {
            _editorCore.ShapeTool.FillMode = (ImageEditor.ShapeFillMode)change.NewValue!;
            InvalidateVisual();
        }
        else if (change.Property == ShapeStrokeColorProperty)
        {
            var color = (Color)change.NewValue!;
            _editorCore.ShapeTool.StrokeColor = new SKColor(color.R, color.G, color.B, color.A);
            InvalidateVisual();
        }
        else if (change.Property == ShapeFillColorProperty)
        {
            var color = (Color)change.NewValue!;
            _editorCore.ShapeTool.FillColor = new SKColor(color.R, color.G, color.B, color.A);
            InvalidateVisual();
        }
        else if (change.Property == ShapeStrokeWidthProperty)
        {
            _editorCore.ShapeTool.StrokeWidth = (float)change.NewValue!;
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

        // Shape tool takes priority when active
        if (_editorCore.ShapeTool.IsActive && props.IsLeftButtonPressed)
        {
            // Track Ctrl key for constraining proportions (square/circle)
            _editorCore.ShapeTool.ConstrainProportions = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            
            if (_editorCore.ShapeTool.OnPointerPressed(skPoint))
            {
                e.Handled = true;
                InvalidateVisual();
                Focus();
                return;
            }
        }

        // Text tool takes priority when active
        if (_editorCore.TextTool.IsActive && props.IsLeftButtonPressed)
        {
            if (_editorCore.TextTool.OnPointerPressed(skPoint))
            {
                e.Handled = true;
                InvalidateVisual();
                Focus();
                return;
            }
        }

        // Inpaint brush takes priority when active
        if (_isInpaintingToolActive && props.IsLeftButtonPressed)
        {
            var imageRect = _editorCore.GetCurrentImageRect();
            if (imageRect.Contains(skPoint))
            {
                _isInpaintPainting = true;
                _inpaintLastPoint = skPoint;
                _inpaintStrokePoints.Clear();
                _inpaintStrokePoints.Add(skPoint);
                e.Handled = true;
                InvalidateVisual();
                Focus();
                return;
            }
        }

        // Drawing tool takes priority when active
        if (_editorCore.DrawingTool.IsActive && props.IsLeftButtonPressed)
        {
            if (_editorCore.DrawingTool.OnPointerPressed(skPoint))
            {
                e.Handled = true;
                InvalidateVisual();
                Focus();
                return;
            }
        }

        // Outpaint tool takes priority when active
        if (_isOutpaintToolActive && props.IsLeftButtonPressed)
        {
            if (_editorCore.OutpaintTool.OnPointerPressed(skPoint))
            {
                e.Handled = true;
                InvalidateVisual();
                Focus();
                return;
            }
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

        // Shape tool takes priority when active
        if (_editorCore.ShapeTool.IsActive)
        {
            // Track Ctrl key for constraining proportions (square/circle)
            _editorCore.ShapeTool.ConstrainProportions = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            
            if (_editorCore.ShapeTool.OnPointerMoved(skPoint))
            {
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }

        // Text tool pointer tracking
        if (_editorCore.TextTool.IsActive)
        {
            if (_editorCore.TextTool.OnPointerMoved(skPoint))
            {
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }

        // Inpaint brush pointer tracking
        if (_isInpaintingToolActive)
        {
            _inpaintCursorPosition = skPoint;
            _hasInpaintCursorPosition = true;

            if (_isInpaintPainting)
            {
                _inpaintStrokePoints.Add(skPoint);
                _inpaintLastPoint = skPoint;
                e.Handled = true;
                InvalidateVisual();
                return;
            }
            else
            {
                InvalidateVisual();
            }
        }

        // Drawing tool takes priority when active
        if (_editorCore.DrawingTool.IsActive)
        {
            if (_editorCore.DrawingTool.OnPointerMoved(skPoint))
            {
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }

        // Outpaint tool pointer tracking
        if (_isOutpaintToolActive)
        {
            if (_editorCore.OutpaintTool.OnPointerMoved(skPoint))
            {
                e.Handled = true;
                InvalidateVisual();
                return;
            }
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

        // Shape tool takes priority when active
        if (_editorCore.ShapeTool.IsActive)
        {
            if (_editorCore.ShapeTool.OnPointerReleased())
            {
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }

        // Text tool takes priority when active
        if (_editorCore.TextTool.IsActive)
        {
            if (_editorCore.TextTool.OnPointerReleased())
            {
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }

        // Inpaint brush release
        if (_isInpaintingToolActive && _isInpaintPainting)
        {
            _isInpaintPainting = false;

            if (_inpaintStrokePoints.Count > 0)
            {
                var imageRect = _editorCore.GetCurrentImageRect();
                if (imageRect.Width > 0 && imageRect.Height > 0)
                {
                    var normalizedPoints = _inpaintStrokePoints
                        .Select(p => new SKPoint(
                            (p.X - imageRect.Left) / imageRect.Width,
                            (p.Y - imageRect.Top) / imageRect.Height))
                        .ToList();

                    var scaledBrushSize = _inpaintBrushSize / imageRect.Width;
                    _editorCore.ApplyInpaintStroke(normalizedPoints, scaledBrushSize);
                    InpaintMaskChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            _inpaintStrokePoints.Clear();
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        // Drawing tool takes priority when active
        if (_editorCore.DrawingTool.IsActive)
        {
            if (_editorCore.DrawingTool.OnPointerReleased())
            {
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }

        // Outpaint tool release
        if (_isOutpaintToolActive)
        {
            if (_editorCore.OutpaintTool.OnPointerReleased())
            {
                e.Handled = true;
                InvalidateVisual();
                return;
            }
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

        // Shift+wheel adjusts inpaint brush size when the tool is active
        if (_isInpaintingToolActive && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var step = e.Delta.Y > 0 ? 5f : -5f;
            _inpaintBrushSize = Math.Clamp(_inpaintBrushSize + step, 1f, 200f);
            InpaintBrushSizeChanged?.Invoke(this, _inpaintBrushSize);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

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

        // Handle Shift key for straight line drawing
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            _editorCore.DrawingTool.IsShiftHeld = true;
            InvalidateVisual();
        }

        // Handle Ctrl key for constraining shape proportions (square/circle)
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
        {
            _editorCore.ShapeTool.ConstrainProportions = true;
            InvalidateVisual();
        }

        // Ctrl+Enter triggers inpainting generation
        if (_isInpaintingToolActive && e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            InpaintGenerateRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        // Text tool: Enter commits, Escape cancels the placed text
        if (_editorCore.TextTool.IsActive && _editorCore.TextTool.HasPlacedText)
        {
            if (e.Key == Key.Enter)
            {
                _editorCore.TextTool.CommitPlacedText();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            else if (e.Key == Key.Escape)
            {
                _editorCore.TextTool.CancelPlacedText();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        // Shape tool: Enter commits, Escape cancels the placed shape
        if (_editorCore.ShapeTool.IsActive && _editorCore.ShapeTool.HasPlacedShape)
        {
            if (e.Key == Key.Enter)
            {
                _editorCore.ShapeTool.CommitPlacedShape();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            else if (e.Key == Key.Escape)
            {
                _editorCore.ShapeTool.CancelPlacedShape();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

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

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        // Handle Shift key release for straight line drawing
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            _editorCore.DrawingTool.IsShiftHeld = false;
            InvalidateVisual();
        }

        // Handle Ctrl key release for shape constraint
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
        {
            _editorCore.ShapeTool.ConstrainProportions = false;
            InvalidateVisual();
        }
    }

    private void UpdateCursor(SKPoint point)
    {
        // Inpaint tool uses a custom rendered brush cursor — hide the system cursor
        if (_isInpaintingToolActive)
        {
            Cursor = new Cursor(StandardCursorType.None);
            return;
        }

        // Drawing tool cursor
        if (_editorCore.DrawingTool.IsActive)
        {
            Cursor = new Cursor(StandardCursorType.Cross);
            return;
        }

        // Text tool cursors
        if (_editorCore.TextTool.IsActive)
        {
            if (_editorCore.TextTool.HasPlacedText)
            {
                var textHandle = _editorCore.TextTool.HitTestHandle(point);
                Cursor = textHandle switch
                {
                    ImageEditor.TextManipulationHandle.Body => new Cursor(StandardCursorType.SizeAll),
                    ImageEditor.TextManipulationHandle.TopLeft or ImageEditor.TextManipulationHandle.BottomRight => new Cursor(StandardCursorType.SizeAll),
                    ImageEditor.TextManipulationHandle.TopRight or ImageEditor.TextManipulationHandle.BottomLeft => new Cursor(StandardCursorType.SizeAll),
                    ImageEditor.TextManipulationHandle.Rotate => new Cursor(StandardCursorType.Hand),
                    ImageEditor.TextManipulationHandle.Delete => new Cursor(StandardCursorType.Hand),
                    _ => new Cursor(StandardCursorType.Cross)
                };
            }
            else
            {
                Cursor = new Cursor(StandardCursorType.Cross);
            }
            return;
        }

        // Shape tool cursors
        if (_editorCore.ShapeTool.IsActive)
        {
            if (_editorCore.ShapeTool.HasPlacedShape)
            {
                var shapeHandle = _editorCore.ShapeTool.HitTestHandle(point);
                Cursor = shapeHandle switch
                {
                    ImageEditor.ShapeManipulationHandle.Body => new Cursor(StandardCursorType.SizeAll),
                    ImageEditor.ShapeManipulationHandle.TopLeft or ImageEditor.ShapeManipulationHandle.BottomRight => new Cursor(StandardCursorType.SizeAll),
                    ImageEditor.ShapeManipulationHandle.TopRight or ImageEditor.ShapeManipulationHandle.BottomLeft => new Cursor(StandardCursorType.SizeAll),
                    ImageEditor.ShapeManipulationHandle.Rotate => new Cursor(StandardCursorType.Hand),
                    ImageEditor.ShapeManipulationHandle.Delete => new Cursor(StandardCursorType.Hand),
                    _ => new Cursor(StandardCursorType.Cross)
                };
            }
            else
            {
                Cursor = new Cursor(StandardCursorType.Cross);
            }
            return;
        }

        // Outpaint tool cursors
        if (_isOutpaintToolActive)
        {
            var outpaintHandle = _editorCore.OutpaintTool.GetCursorForPoint(point);
            Cursor = outpaintHandle switch
            {
                ImageEditor.OutpaintHandle.Top or ImageEditor.OutpaintHandle.Bottom => new Cursor(StandardCursorType.SizeNorthSouth),
                ImageEditor.OutpaintHandle.Left or ImageEditor.OutpaintHandle.Right => new Cursor(StandardCursorType.SizeWestEast),
                _ => Cursor.Default
            };
            return;
        }

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

        var inpaintOverlay = _isInpaintingToolActive
            ? new InpaintOverlayState(
                _hasInpaintCursorPosition ? _inpaintCursorPosition : null,
                _inpaintBrushSize,
                _isInpaintPainting,
                _isInpaintPainting ? [.. _inpaintStrokePoints] : null)
            : null;

        context.Custom(new ImageEditorDrawOperation(
            new Rect(0, 0, bounds.Width, bounds.Height),
            _editorCore,
            skBgColor,
            inpaintOverlay));
    }

    /// <summary>
    /// Loads an image from the specified file path.
    /// </summary>
    public bool LoadImage(string filePath)
    {
        var result = _editorCore.LoadImage(filePath);
        if (result)
        {
            _suppressImagePathLoad = true;
            SetCurrentValue(ImagePathProperty, filePath);
            _suppressImagePathLoad = false;
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
            _suppressImagePathLoad = true;
            SetCurrentValue(ImagePathProperty, null);
            _suppressImagePathLoad = false;
        }
        return result;
    }

    /// <summary>
    /// Loads a TIFF file as layers, preserving multi-page/layered structure.
    /// </summary>
    public bool LoadLayeredTiff(string filePath)
    {
        var result = _editorCore.LoadLayeredTiff(filePath);
        if (result)
        {
            _suppressImagePathLoad = true;
            SetCurrentValue(ImagePathProperty, filePath);
            _suppressImagePathLoad = false;
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
    /// Commits the currently placed shape to the image.
    /// </summary>
    /// <returns>True if a shape was committed.</returns>
    public bool CommitPlacedShape()
    {
        var result = _editorCore.ShapeTool.CommitPlacedShape();
        if (result) InvalidateVisual();
        return result;
    }

    /// <summary>
    /// Cancels the currently placed shape without applying it.
    /// </summary>
    public void CancelPlacedShape()
    {
        _editorCore.ShapeTool.CancelPlacedShape();
        InvalidateVisual();
    }

    /// <summary>
    /// Gets whether there is a placed shape that can be committed or cancelled.
    /// </summary>
    public bool HasPlacedShape => _editorCore.ShapeTool.HasPlacedShape;

    /// <summary>
    /// Gets whether there is a placed text element that can be committed or cancelled.
    /// </summary>
    public bool HasPlacedText => _editorCore.TextTool.HasPlacedText;

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
        // Calculate the fit zoom level based on current bounds before switching to fit mode
        var bounds = Bounds;
        if (bounds.Width > 0 && bounds.Height > 0 && _editorCore.HasImage)
        {
            var fitRect = _editorCore.CalculateFitRect((float)bounds.Width, (float)bounds.Height);
            if (fitRect.Width > 0)
            {
                var fitZoom = fitRect.Width / _editorCore.Width;
                // Set the zoom level directly so the event carries the correct value
                _editorCore.SetFitModeWithZoom(fitZoom);
            }
            else
            {
                _editorCore.ZoomToFit();
            }
        }
        else
        {
            _editorCore.ZoomToFit();
        }
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

    private void OnDrawingChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
    }

    private void OnStrokeCompleted(object? sender, ImageEditor.DrawingStrokeEventArgs e)
    {
        // Apply the completed stroke to the image
        _editorCore.ApplyStroke(e.Points, e.Color, e.BrushSize, e.BrushShape);
    }

    private void OnShapeChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
    }

    private void OnShapeCompleted(object? sender, ImageEditor.ShapeCompletedEventArgs e)
    {
        // Apply the committed shape to the image
        _editorCore.ApplyShape(e.Shape);
    }

    private void OnPlacedShapeStateChanged(object? sender, EventArgs e)
    {
        PlacedShapeStateChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
    }

    private void OnTextCompleted(object? sender, ImageEditor.TextCompletedEventArgs e)
    {
        // Apply the committed text to the image as a new layer
        _editorCore.ApplyText(e.TextElement);
    }

    private void OnPlacedTextStateChanged(object? sender, EventArgs e)
    {
        PlacedTextStateChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void OnEditorCoreZoomChanged(object? sender, EventArgs e)
    {
        SetCurrentValue(ZoomLevelProperty, _editorCore.ZoomLevel);
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// Event raised when the outpaint region changes.
    /// </summary>
    public event EventHandler? OutpaintRegionChanged;

    private void OnOutpaintRegionChanged(object? sender, EventArgs e)
    {
        OutpaintRegionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isDetachedFromTree = false;
        _editorCore.ImageChanged += OnEditorCoreImageChanged;
        _editorCore.CropTool.CropRegionChanged += OnCropRegionChanged;
        _editorCore.DrawingTool.DrawingChanged += OnDrawingChanged;
        _editorCore.DrawingTool.StrokeCompleted += OnStrokeCompleted;
        _editorCore.ShapeTool.ShapeChanged += OnShapeChanged;
        _editorCore.ShapeTool.ShapeCompleted += OnShapeCompleted;
        _editorCore.ShapeTool.PlacedShapeStateChanged += OnPlacedShapeStateChanged;
        _editorCore.TextTool.TextChanged += OnTextChanged;
        _editorCore.TextTool.TextCompleted += OnTextCompleted;
        _editorCore.TextTool.PlacedTextStateChanged += OnPlacedTextStateChanged;
        _editorCore.ZoomChanged += OnEditorCoreZoomChanged;
        _editorCore.OutpaintTool.RegionChanged += OnOutpaintRegionChanged;
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isDetachedFromTree = true;
        base.OnDetachedFromVisualTree(e);
        _editorCore.ImageChanged -= OnEditorCoreImageChanged;
        _editorCore.CropTool.CropRegionChanged -= OnCropRegionChanged;
        _editorCore.DrawingTool.DrawingChanged -= OnDrawingChanged;
        _editorCore.DrawingTool.StrokeCompleted -= OnStrokeCompleted;
        _editorCore.ShapeTool.ShapeChanged -= OnShapeChanged;
        _editorCore.ShapeTool.ShapeCompleted -= OnShapeCompleted;
        _editorCore.ShapeTool.PlacedShapeStateChanged -= OnPlacedShapeStateChanged;
        _editorCore.TextTool.TextChanged -= OnTextChanged;
        _editorCore.TextTool.TextCompleted -= OnTextCompleted;
        _editorCore.TextTool.PlacedTextStateChanged -= OnPlacedTextStateChanged;
        _editorCore.ZoomChanged -= OnEditorCoreZoomChanged;
        _editorCore.OutpaintTool.RegionChanged -= OnOutpaintRegionChanged;
    }

    /// <summary>
    /// Holds inpainting overlay state for rendering.
    /// </summary>
    private sealed record InpaintOverlayState(
        SKPoint? CursorPosition,
        float BrushSize,
        bool IsPainting,
        List<SKPoint>? StrokePoints);

    /// <summary>
    /// Custom draw operation for SkiaSharp rendering.
    /// </summary>
    private sealed class ImageEditorDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly ImageEditor.ImageEditorCore _editorCore;
        private readonly SKColor _backgroundColor;
        private readonly InpaintOverlayState? _inpaintOverlay;

        public ImageEditorDrawOperation(
            Rect bounds,
            ImageEditor.ImageEditorCore editorCore,
            SKColor backgroundColor,
            InpaintOverlayState? inpaintOverlay = null)
        {
            _bounds = bounds;
            _editorCore = editorCore;
            _backgroundColor = backgroundColor;
            _inpaintOverlay = inpaintOverlay;
        }

        public Rect Bounds => _bounds;

        public bool HitTest(Point p) => _bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other)
        {
            // Always return false to ensure redraws happen when InvalidateVisual is called.
            // The editor core's preview state can change without affecting the bounds or reference,
            // so we must always redraw to reflect changes like color grading or brightness adjustments.
            return false;
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

            RenderInpaintOverlay(canvas);
        }

        private void RenderInpaintOverlay(SKCanvas canvas)
        {
            if (_inpaintOverlay is null) return;

            // Draw live stroke preview while painting
            if (_inpaintOverlay.IsPainting && _inpaintOverlay.StrokePoints is { Count: > 0 })
            {
                using var strokePaint = new SKPaint
                {
                    Color = new SKColor(255, 255, 255, 100),
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = _inpaintOverlay.BrushSize,
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round
                };

                var points = _inpaintOverlay.StrokePoints;
                if (points.Count == 1)
                {
                    strokePaint.Style = SKPaintStyle.Fill;
                    canvas.DrawCircle(points[0], _inpaintOverlay.BrushSize / 2, strokePaint);
                }
                else
                {
                    using var path = new SKPath();
                    path.MoveTo(points[0]);
                    for (var i = 1; i < points.Count; i++)
                    {
                        path.LineTo(points[i]);
                    }
                    canvas.DrawPath(path, strokePaint);
                }
            }

            // Draw brush cursor circle
            if (_inpaintOverlay.CursorPosition is { } cursorPos)
            {
                var radius = _inpaintOverlay.BrushSize / 2;

                // Outer ring (white)
                using var outerPaint = new SKPaint
                {
                    Color = new SKColor(255, 255, 255, 200),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.5f,
                    IsAntialias = true
                };
                canvas.DrawCircle(cursorPos, radius, outerPaint);

                // Inner ring (black for contrast)
                using var innerPaint = new SKPaint
                {
                    Color = new SKColor(0, 0, 0, 140),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 0.75f,
                    IsAntialias = true
                };
                canvas.DrawCircle(cursorPos, radius + 1f, innerPaint);
            }
        }
    }
}
