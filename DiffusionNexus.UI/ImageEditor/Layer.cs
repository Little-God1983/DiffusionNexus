using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Represents a single layer in the image editor layer stack.
/// Each layer contains its own bitmap content and rendering properties.
/// </summary>
public class Layer : IDisposable
{
    private SKBitmap? _bitmap;
    private SKBitmap? _thumbnail;
    private string _name;
    private bool _isVisible;
    private float _opacity;
    private bool _isLocked;
    private BlendMode _blendMode;
    private bool _isDisposed;

    private const int ThumbnailSize = 48;

    /// <summary>
    /// Creates a new layer with the specified dimensions.
    /// </summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="name">Layer name.</param>
    public Layer(int width, int height, string name = "Layer")
    {
        _name = name;
        _isVisible = true;
        _opacity = 1.0f;
        _isLocked = false;
        _blendMode = BlendMode.Normal;

        _bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        _bitmap.Erase(SKColors.Transparent);

        UpdateThumbnail();
    }

    /// <summary>
    /// Creates a new layer from an existing bitmap.
    /// </summary>
    /// <param name="sourceBitmap">Source bitmap to copy.</param>
    /// <param name="name">Layer name.</param>
    public Layer(SKBitmap sourceBitmap, string name = "Layer")
    {
        _name = name;
        _isVisible = true;
        _opacity = 1.0f;
        _isLocked = false;
        _blendMode = BlendMode.Normal;

        _bitmap = sourceBitmap.Copy();
        UpdateThumbnail();
    }

    /// <summary>
    /// Gets or sets the layer name.
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                PropertyChanged?.Invoke(this, nameof(Name));
            }
        }
    }

    /// <summary>
    /// Gets or sets whether the layer is visible.
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                PropertyChanged?.Invoke(this, nameof(IsVisible));
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Gets or sets the layer opacity (0.0 to 1.0).
    /// </summary>
    public float Opacity
    {
        get => _opacity;
        set
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(_opacity - clamped) > 0.001f)
            {
                _opacity = clamped;
                PropertyChanged?.Invoke(this, nameof(Opacity));
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Gets or sets whether the layer is locked (prevents editing).
    /// </summary>
    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (_isLocked != value)
            {
                _isLocked = value;
                PropertyChanged?.Invoke(this, nameof(IsLocked));
            }
        }
    }

    /// <summary>
    /// Gets or sets the blend mode for this layer.
    /// </summary>
    public BlendMode BlendMode
    {
        get => _blendMode;
        set
        {
            if (_blendMode != value)
            {
                _blendMode = value;
                PropertyChanged?.Invoke(this, nameof(BlendMode));
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Gets the layer bitmap. Returns null if disposed.
    /// </summary>
    public SKBitmap? Bitmap => _bitmap;

    /// <summary>
    /// Gets the layer thumbnail for UI display.
    /// </summary>
    public SKBitmap? Thumbnail => _thumbnail;

    /// <summary>
    /// Gets the layer width in pixels.
    /// </summary>
    public int Width => _bitmap?.Width ?? 0;

    /// <summary>
    /// Gets the layer height in pixels.
    /// </summary>
    public int Height => _bitmap?.Height ?? 0;

    /// <summary>
    /// Gets whether this layer can be edited.
    /// </summary>
    public bool CanEdit => !_isLocked && _bitmap != null;

    /// <summary>
    /// Event raised when a property changes.
    /// </summary>
    public event Action<Layer, string>? PropertyChanged;

    /// <summary>
    /// Event raised when the layer content changes.
    /// </summary>
    public event EventHandler? ContentChanged;

    /// <summary>
    /// Creates a canvas for drawing on this layer.
    /// </summary>
    /// <returns>An SKCanvas for the layer bitmap, or null if locked or disposed.</returns>
    public SKCanvas? CreateCanvas()
    {
        if (!CanEdit || _bitmap == null) return null;
        return new SKCanvas(_bitmap);
    }

    /// <summary>
    /// Notifies that the layer content has been modified.
    /// Call this after drawing operations to update the thumbnail.
    /// </summary>
    public void NotifyContentChanged()
    {
        UpdateThumbnail();
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Clears the layer to transparent.
    /// </summary>
    public void Clear()
    {
        if (!CanEdit || _bitmap == null) return;

        _bitmap.Erase(SKColors.Transparent);
        NotifyContentChanged();
    }

    /// <summary>
    /// Fills the layer with a solid color.
    /// </summary>
    /// <param name="color">The fill color.</param>
    public void Fill(SKColor color)
    {
        if (!CanEdit || _bitmap == null) return;

        _bitmap.Erase(color);
        NotifyContentChanged();
    }

    /// <summary>
    /// Replaces the layer's bitmap with a new one.
    /// Used by image operations (color balance, brightness, etc.) to update the layer content.
    /// </summary>
    /// <param name="newBitmap">The new bitmap to use. This layer takes ownership.</param>
    public void ReplaceBitmap(SKBitmap newBitmap)
    {
        ArgumentNullException.ThrowIfNull(newBitmap);

        _bitmap?.Dispose();
        _bitmap = newBitmap;
        NotifyContentChanged();
    }

    /// <summary>
    /// Creates a copy of this layer.
    /// </summary>
    /// <returns>A new layer with copied content.</returns>
    public Layer Clone()
    {
        if (_bitmap == null)
            throw new InvalidOperationException("Cannot clone disposed layer");

        var clone = new Layer(_bitmap, $"{_name} Copy")
        {
            IsVisible = _isVisible,
            Opacity = _opacity,
            IsLocked = _isLocked,
            BlendMode = _blendMode
        };
        return clone;
    }

    /// <summary>
    /// Resizes the layer to new dimensions.
    /// </summary>
    /// <param name="newWidth">New width in pixels.</param>
    /// <param name="newHeight">New height in pixels.</param>
    public void Resize(int newWidth, int newHeight)
    {
        if (_bitmap == null) return;

        var newBitmap = new SKBitmap(newWidth, newHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        newBitmap.Erase(SKColors.Transparent);

        using var canvas = new SKCanvas(newBitmap);
        canvas.DrawBitmap(_bitmap, 0, 0);

        _bitmap.Dispose();
        _bitmap = newBitmap;
        UpdateThumbnail();
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Crops the layer to the specified rectangle.
    /// </summary>
    /// <param name="cropRect">The crop rectangle in pixel coordinates.</param>
    public void Crop(SKRectI cropRect)
    {
        if (_bitmap == null || cropRect.Width <= 0 || cropRect.Height <= 0) return;

        var newBitmap = new SKBitmap(cropRect.Width, cropRect.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        newBitmap.Erase(SKColors.Transparent);

        using var canvas = new SKCanvas(newBitmap);
        var srcRect = new SKRect(cropRect.Left, cropRect.Top, cropRect.Right, cropRect.Bottom);
        var destRect = new SKRect(0, 0, cropRect.Width, cropRect.Height);
        canvas.DrawBitmap(_bitmap, srcRect, destRect);

        _bitmap.Dispose();
        _bitmap = newBitmap;
        UpdateThumbnail();
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateThumbnail()
    {
        if (_bitmap == null) return;

        _thumbnail?.Dispose();

        // Calculate thumbnail dimensions maintaining aspect ratio
        var aspectRatio = (float)_bitmap.Width / _bitmap.Height;
        int thumbWidth, thumbHeight;

        if (aspectRatio > 1)
        {
            thumbWidth = ThumbnailSize;
            thumbHeight = (int)(ThumbnailSize / aspectRatio);
        }
        else
        {
            thumbHeight = ThumbnailSize;
            thumbWidth = (int)(ThumbnailSize * aspectRatio);
        }

        thumbWidth = Math.Max(1, thumbWidth);
        thumbHeight = Math.Max(1, thumbHeight);

        _thumbnail = new SKBitmap(thumbWidth, thumbHeight);
        using var canvas = new SKCanvas(_thumbnail);

        // Draw checkerboard pattern for transparency
        DrawCheckerboard(canvas, thumbWidth, thumbHeight);

        // Draw scaled layer content
        var destRect = new SKRect(0, 0, thumbWidth, thumbHeight);
        canvas.DrawBitmap(_bitmap, destRect);
    }

    private static void DrawCheckerboard(SKCanvas canvas, int width, int height)
    {
        const int checkSize = 4;
        using var lightPaint = new SKPaint { Color = new SKColor(200, 200, 200) };
        using var darkPaint = new SKPaint { Color = new SKColor(150, 150, 150) };

        canvas.Clear(lightPaint.Color);

        for (int y = 0; y < height; y += checkSize)
        {
            for (int x = 0; x < width; x += checkSize)
            {
                if ((x / checkSize + y / checkSize) % 2 == 1)
                {
                    canvas.DrawRect(x, y, checkSize, checkSize, darkPaint);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _bitmap?.Dispose();
        _bitmap = null;

        _thumbnail?.Dispose();
        _thumbnail = null;

        GC.SuppressFinalize(this);
    }
}
