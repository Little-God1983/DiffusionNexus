using Avalonia;
using Avalonia.Controls;
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

    public ImageEditorControl()
    {
        _editorCore = new ImageEditor.ImageEditorCore();
        _editorCore.ImageChanged += OnEditorCoreImageChanged;
        ClipToBounds = true;
    }

    static ImageEditorControl()
    {
        AffectsRender<ImageEditorControl>(ImagePathProperty, CanvasBackgroundProperty);
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

    private void OnEditorCoreImageChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
        ImageChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _editorCore.ImageChanged -= OnEditorCoreImageChanged;
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

            _editorCore.RenderCentered(
                canvas,
                (float)_bounds.Width,
                (float)_bounds.Height,
                _backgroundColor);
        }
    }
}
