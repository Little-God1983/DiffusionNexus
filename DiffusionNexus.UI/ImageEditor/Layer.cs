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
    private bool _isInpaintMask;
    private bool _isDisposed;
    private int _offsetX;
    private int _offsetY;

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
    /// Creates a layer from an existing bitmap placed at <paramref name="offset"/> (canvas pixels).
    /// </summary>
    public Layer(SKBitmap sourceBitmap, string name, SKPointI offset) : this(sourceBitmap, name)
    {
        _offsetX = offset.X;
        _offsetY = offset.Y;
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
    /// Gets or sets whether this layer is an inpainting mask layer.
    /// Inpaint mask layers are rendered with a checkerboard pattern and can only
    /// be painted on by the inpainting brush tool.
    /// </summary>
    public bool IsInpaintMask
    {
        get => _isInpaintMask;
        set
        {
            if (_isInpaintMask != value)
            {
                _isInpaintMask = value;
                PropertyChanged?.Invoke(this, nameof(IsInpaintMask));
                ContentChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>Left edge of this layer in canvas pixels. Zero for a canvas-aligned layer.</summary>
    public int OffsetX => _offsetX;

    /// <summary>Top edge of this layer in canvas pixels. Zero for a canvas-aligned layer.</summary>
    public int OffsetY => _offsetY;

    /// <summary>The layer's rectangle in canvas pixels: offset plus its own bitmap size.</summary>
    public SKRectI Bounds => new(_offsetX, _offsetY, _offsetX + Width, _offsetY + Height);

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
    /// Moves the layer without touching its pixels. Raises <see cref="PropertyChanged"/> with
    /// "Offset" and <see cref="ContentChanged"/> so the compositor redraws.
    /// </summary>
    internal void SetOffset(int x, int y)
    {
        if (_offsetX == x && _offsetY == y) return;
        _offsetX = x;
        _offsetY = y;
        PropertyChanged?.Invoke(this, "Offset");
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

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

        var clone = new Layer(_bitmap, $"{_name} Copy", new SKPointI(_offsetX, _offsetY))
        {
            IsVisible = _isVisible,
            Opacity = _opacity,
            IsLocked = _isLocked,
            BlendMode = _blendMode,
            IsInpaintMask = _isInpaintMask
        };
        return clone;
    }

    /// <summary>
    /// Builds the bitmap <see cref="ResizeCanvas"/> would swap in, without touching the layer.
    /// The layer is shifted by (<paramref name="offsetX"/>, <paramref name="offsetY"/>) and grown
    /// to the union of its shifted bounds and the new canvas, so a canvas-aligned layer stays
    /// canvas-aligned (drawing on the new area keeps working) and a moved layer keeps every pixel.
    /// Throws when SkiaSharp cannot allocate, so the caller can report the failure.
    /// Callers pass non-negative offsets (the canvas only grows); a negative offset would move
    /// a canvas-aligned layer, including the inpaint mask, off (0, 0).
    /// </summary>
    internal SKBitmap CreateResizedBitmap(int newWidth, int newHeight, int offsetX, int offsetY, out SKPointI newOffset)
    {
        if (_bitmap == null)
            throw new InvalidOperationException("The layer has no bitmap to resize.");

        var shifted = new SKRectI(_offsetX + offsetX, _offsetY + offsetY, _offsetX + offsetX + Width, _offsetY + offsetY + Height);
        var union = SKRectI.Union(shifted, new SKRectI(0, 0, newWidth, newHeight));

        var newBitmap = new SKBitmap(union.Width, union.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        if (newBitmap.IsEmpty || newBitmap.Width != union.Width || newBitmap.Height != union.Height)
        {
            newBitmap.Dispose();
            throw new InvalidOperationException($"Could not allocate a {union.Width}x{union.Height} canvas.");
        }
        newBitmap.Erase(SKColors.Transparent);

        using var canvas = new SKCanvas(newBitmap);
        canvas.DrawBitmap(_bitmap, shifted.Left - union.Left, shifted.Top - union.Top);
        newOffset = new SKPointI(union.Left, union.Top);
        return newBitmap;
    }

    /// <summary>Resizes the layer canvas and draws the existing content at the specified offset.</summary>
    public void ResizeCanvas(int newWidth, int newHeight, int offsetX, int offsetY)
    {
        if (_bitmap == null) return;
        AdoptBitmap(CreateResizedBitmap(newWidth, newHeight, offsetX, offsetY, out var newOffset), newOffset);
    }

    /// <summary>Replaces the layer's bitmap with one prepared by <see cref="CreateResizedBitmap"/>.</summary>
    internal void AdoptBitmap(SKBitmap newBitmap)
    {
        _bitmap?.Dispose();
        _bitmap = newBitmap;
        UpdateThumbnail();
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the bitmap and the offset together (one ContentChanged).</summary>
    internal void AdoptBitmap(SKBitmap newBitmap, SKPointI offset)
    {
        _offsetX = offset.X;
        _offsetY = offset.Y;
        AdoptBitmap(newBitmap);
    }

    /// <summary>
    /// Like <see cref="AdoptBitmap(SKBitmap, SKPointI)"/> but hands the previous bitmap back
    /// instead of disposing it, for callers that must dispose outside the render lock.
    /// </summary>
    internal SKBitmap? AdoptBitmapKeepingOld(SKBitmap newBitmap, SKPointI offset)
    {
        var old = _bitmap;
        _bitmap = newBitmap;
        _offsetX = offset.X;
        _offsetY = offset.Y;
        UpdateThumbnail();
        ContentChanged?.Invoke(this, EventArgs.Empty);
        return old;
    }

    /// <summary>
    /// Crops the layer to <paramref name="cropRect"/> (canvas pixels): keeps the intersection of
    /// the layer's bounds with the rect and re-offsets relative to the rect's top-left. A layer
    /// wholly outside becomes a 1x1 transparent bitmap at (0, 0) so it stays valid.
    /// </summary>
    public void Crop(SKRectI cropRect)
    {
        if (_bitmap == null || cropRect.Width <= 0 || cropRect.Height <= 0) return;

        var inter = SKRectI.Intersect(Bounds, cropRect);
        if (inter.IsEmpty || inter.Width <= 0 || inter.Height <= 0)
        {
            var empty = new SKBitmap(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
            empty.Erase(SKColors.Transparent);
            AdoptBitmap(empty, new SKPointI(0, 0));
            return;
        }

        var newBitmap = new SKBitmap(inter.Width, inter.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        newBitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(newBitmap))
        {
            // Source rect in layer-local pixels.
            var src = new SKRect(inter.Left - _offsetX, inter.Top - _offsetY, inter.Right - _offsetX, inter.Bottom - _offsetY);
            canvas.DrawBitmap(_bitmap, src, new SKRect(0, 0, inter.Width, inter.Height));
        }
        AdoptBitmap(newBitmap, new SKPointI(inter.Left - cropRect.Left, inter.Top - cropRect.Top));
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
