using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Shared state and drag math for tools that grow the canvas outward from the image:
/// per-edge pixel extension, outward-only handle dragging, aspect-ratio and target-size
/// presets. Subclasses decide where the handles sit (<see cref="GetHandleCenter"/>),
/// how big their hit zone is, how the frame is drawn, and how much room the viewport
/// must reserve around the frame (<see cref="FitMargin"/>).
/// </summary>
public abstract class CanvasExtensionTool
{
    private static readonly OutpaintHandle[] HitTestOrder =
    [
        // Corners first: they are the "more specific" choice when a click lands in the diagonal area.
        OutpaintHandle.TopLeft, OutpaintHandle.TopRight, OutpaintHandle.BottomLeft, OutpaintHandle.BottomRight,
        OutpaintHandle.Top, OutpaintHandle.Bottom, OutpaintHandle.Left, OutpaintHandle.Right
    ];

    private SKRect _imageRect;
    private OutpaintHandle _activeHandle = OutpaintHandle.None;
    private SKPoint _dragStartPoint;

    // Extension stored in image pixels (how many pixels to add on each side)
    private int _extendTop;
    private int _extendRight;
    private int _extendBottom;
    private int _extendLeft;

    // Drag start state
    private int _dragStartExtendTop;
    private int _dragStartExtendRight;
    private int _dragStartExtendBottom;
    private int _dragStartExtendLeft;

    private bool _isActive;
    private bool _shrinkRaisedThisGesture;
    private bool _isShrinkBlocked;

    /// <summary>Gets or sets whether the tool is active. Deactivating resets the extension.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            if (!value)
            {
                _activeHandle = OutpaintHandle.None;
                Reset();
            }
        }
    }

    /// <summary>The original image width in pixels.</summary>
    public int ImagePixelWidth { get; set; }

    /// <summary>The original image height in pixels.</summary>
    public int ImagePixelHeight { get; set; }

    /// <summary>Pixel extension for the top edge.</summary>
    public int ExtendTop => _extendTop;

    /// <summary>Pixel extension for the right edge.</summary>
    public int ExtendRight => _extendRight;

    /// <summary>Pixel extension for the bottom edge.</summary>
    public int ExtendBottom => _extendBottom;

    /// <summary>Pixel extension for the left edge.</summary>
    public int ExtendLeft => _extendLeft;

    /// <summary>Whether any extension has been applied.</summary>
    public bool HasExtension => _extendTop > 0 || _extendRight > 0 || _extendBottom > 0 || _extendLeft > 0;

    /// <summary>Whether a handle is currently being dragged.</summary>
    public bool IsDragging => _activeHandle != OutpaintHandle.None;

    /// <summary>
    /// True while the last pointer move of the current gesture tried to pull a handle past the
    /// image edge and was clamped. False once the pointer is released or moves outward again.
    /// </summary>
    public bool IsShrinkBlocked => _isShrinkBlocked;

    /// <summary>
    /// Screen pixels the viewport reserves on each side of the extended frame so the
    /// handles and the size label stay visible while the tool is active.
    /// </summary>
    public abstract float FitMargin { get; }

    /// <summary>Hit radius in screen pixels around each handle centre.</summary>
    protected abstract float HandleHitRadius { get; }

    /// <summary>The image rectangle in screen coordinates, as last set by <see cref="SetImageBounds"/>.</summary>
    protected SKRect ImageRect => _imageRect;

    /// <summary>The handle being dragged, or <see cref="OutpaintHandle.None"/>.</summary>
    protected OutpaintHandle ActiveHandle => _activeHandle;

    /// <summary>Raised when the extension amounts change.</summary>
    public event EventHandler? RegionChanged;

    /// <summary>
    /// Raised when the user tries to make the canvas smaller than the image: an inward
    /// handle drag (once per gesture) or a target size below the image size (per call).
    /// </summary>
    public event EventHandler? ShrinkAttempted;

    /// <summary>Screen-space centre of the given handle.</summary>
    protected abstract SKPoint GetHandleCenter(OutpaintHandle handle);

    /// <summary>Renders the tool's overlay.</summary>
    public abstract void Render(SKCanvas canvas, SKRect canvasBounds);

    /// <summary>Gets the new total resolution including extensions.</summary>
    public (int Width, int Height) GetNewDimensions()
    {
        if (ImagePixelWidth <= 0 || ImagePixelHeight <= 0)
            return (0, 0);

        return (ImagePixelWidth + _extendLeft + _extendRight,
                ImagePixelHeight + _extendTop + _extendBottom);
    }

    /// <summary>Sets the image bounds (screen coordinates) used for rendering and drag scaling.</summary>
    public void SetImageBounds(SKRect imageRect)
    {
        _imageRect = imageRect;
    }

    /// <summary>Resets all extensions to zero.</summary>
    public void Reset()
    {
        _extendTop = 0;
        _extendRight = 0;
        _extendBottom = 0;
        _extendLeft = 0;
        _activeHandle = OutpaintHandle.None;
        _isShrinkBlocked = false;
        RegionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the extension amounts for each edge in image pixels. Negative values clamp to zero.</summary>
    public void SetExtension(int top, int right, int bottom, int left)
    {
        _extendTop = Math.Max(0, top);
        _extendRight = Math.Max(0, right);
        _extendBottom = Math.Max(0, bottom);
        _extendLeft = Math.Max(0, left);
        RegionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sets extension to match a target aspect ratio, expanding symmetrically.
    /// The image is never made smaller, only extended on the necessary sides.
    /// </summary>
    public void SetAspectRatio(float ratioW, float ratioH)
    {
        if (ratioW <= 0 || ratioH <= 0 || ImagePixelWidth <= 0 || ImagePixelHeight <= 0)
            return;

        var currentW = ImagePixelWidth;
        var currentH = ImagePixelHeight;
        var targetRatio = ratioW / ratioH;
        var currentRatio = (float)currentW / currentH;

        int newW, newH;
        if (targetRatio > currentRatio)
        {
            newW = (int)Math.Round(currentH * targetRatio);
            newH = currentH;
        }
        else
        {
            newW = currentW;
            newH = (int)Math.Round(currentW / targetRatio);
        }

        ApplySymmetricTarget(newW, newH);
        RegionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sets the total canvas size. Extra pixels are split evenly between left/right and
    /// top/bottom (the odd pixel goes right / bottom). A dimension below the image size is
    /// clamped to the image size and <see cref="ShrinkAttempted"/> is raised.
    /// </summary>
    public void SetTargetSize(int width, int height)
    {
        if (ImagePixelWidth <= 0 || ImagePixelHeight <= 0)
            return;

        var shrinkRequested = width < ImagePixelWidth || height < ImagePixelHeight;
        ApplySymmetricTarget(width, height);

        if (shrinkRequested)
            ShrinkAttempted?.Invoke(this, EventArgs.Empty);
        RegionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplySymmetricTarget(int width, int height)
    {
        var totalExtendX = Math.Max(0, width - ImagePixelWidth);
        var totalExtendY = Math.Max(0, height - ImagePixelHeight);

        _extendLeft = totalExtendX / 2;
        _extendRight = totalExtendX - _extendLeft;
        _extendTop = totalExtendY / 2;
        _extendBottom = totalExtendY - _extendTop;
    }

    /// <summary>Handles pointer pressed. Returns true when a handle was grabbed.</summary>
    public bool OnPointerPressed(SKPoint point)
    {
        if (!_isActive) return false;

        _activeHandle = HitTestHandle(point);
        if (_activeHandle == OutpaintHandle.None)
            return false;

        _dragStartPoint = point;
        _dragStartExtendTop = _extendTop;
        _dragStartExtendRight = _extendRight;
        _dragStartExtendBottom = _extendBottom;
        _dragStartExtendLeft = _extendLeft;
        _shrinkRaisedThisGesture = false;
        _isShrinkBlocked = false;
        return true;
    }

    /// <summary>Handles pointer moved. Returns true when a drag is in progress.</summary>
    public bool OnPointerMoved(SKPoint point)
    {
        if (!_isActive || _activeHandle == OutpaintHandle.None) return false;

        var deltaX = point.X - _dragStartPoint.X;
        var deltaY = point.Y - _dragStartPoint.Y;

        // Convert screen delta to pixel delta based on image-to-screen scale
        var scaleX = _imageRect.Width > 0 ? ImagePixelWidth / _imageRect.Width : 1f;
        var scaleY = _imageRect.Height > 0 ? ImagePixelHeight / _imageRect.Height : 1f;

        // Corner handles extend two adjacent edges simultaneously from one drag.
        var extendTopDelta = -(int)(deltaY * scaleY);
        var extendBottomDelta = (int)(deltaY * scaleY);
        var extendLeftDelta = -(int)(deltaX * scaleX);
        var extendRightDelta = (int)(deltaX * scaleX);

        var clamped = false;
        int ClampToZero(int requested)
        {
            if (requested >= 0) return requested;
            clamped = true;
            return 0;
        }

        switch (_activeHandle)
        {
            case OutpaintHandle.Top:
                _extendTop = ClampToZero(_dragStartExtendTop + extendTopDelta);
                break;
            case OutpaintHandle.Bottom:
                _extendBottom = ClampToZero(_dragStartExtendBottom + extendBottomDelta);
                break;
            case OutpaintHandle.Left:
                _extendLeft = ClampToZero(_dragStartExtendLeft + extendLeftDelta);
                break;
            case OutpaintHandle.Right:
                _extendRight = ClampToZero(_dragStartExtendRight + extendRightDelta);
                break;
            case OutpaintHandle.TopLeft:
                _extendTop = ClampToZero(_dragStartExtendTop + extendTopDelta);
                _extendLeft = ClampToZero(_dragStartExtendLeft + extendLeftDelta);
                break;
            case OutpaintHandle.TopRight:
                _extendTop = ClampToZero(_dragStartExtendTop + extendTopDelta);
                _extendRight = ClampToZero(_dragStartExtendRight + extendRightDelta);
                break;
            case OutpaintHandle.BottomLeft:
                _extendBottom = ClampToZero(_dragStartExtendBottom + extendBottomDelta);
                _extendLeft = ClampToZero(_dragStartExtendLeft + extendLeftDelta);
                break;
            case OutpaintHandle.BottomRight:
                _extendBottom = ClampToZero(_dragStartExtendBottom + extendBottomDelta);
                _extendRight = ClampToZero(_dragStartExtendRight + extendRightDelta);
                break;
        }

        _isShrinkBlocked = clamped;
        if (clamped && !_shrinkRaisedThisGesture)
        {
            _shrinkRaisedThisGesture = true;
            ShrinkAttempted?.Invoke(this, EventArgs.Empty);
        }

        RegionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Handles pointer released.</summary>
    public bool OnPointerReleased()
    {
        if (!_isActive) return false;
        _activeHandle = OutpaintHandle.None;
        _isShrinkBlocked = false;
        return true;
    }

    /// <summary>Gets the handle under the point, for cursor selection.</summary>
    public OutpaintHandle GetCursorForPoint(SKPoint point)
    {
        if (!_isActive) return OutpaintHandle.None;
        return HitTestHandle(point);
    }

    /// <summary>The extended frame in screen coordinates.</summary>
    protected SKRect GetExtendedScreenRect()
    {
        if (ImagePixelWidth <= 0 || ImagePixelHeight <= 0)
            return _imageRect;

        var pixelsPerScreenX = _imageRect.Width / ImagePixelWidth;
        var pixelsPerScreenY = _imageRect.Height / ImagePixelHeight;

        return new SKRect(
            _imageRect.Left - _extendLeft * pixelsPerScreenX,
            _imageRect.Top - _extendTop * pixelsPerScreenY,
            _imageRect.Right + _extendRight * pixelsPerScreenX,
            _imageRect.Bottom + _extendBottom * pixelsPerScreenY);
    }

    private OutpaintHandle HitTestHandle(SKPoint point)
    {
        var hitRadius = HandleHitRadius;
        foreach (var handle in HitTestOrder)
        {
            var center = GetHandleCenter(handle);
            var dx = point.X - center.X;
            var dy = point.Y - center.Y;
            if (dx * dx + dy * dy <= hitRadius * hitRadius)
                return handle;
        }
        return OutpaintHandle.None;
    }
}
