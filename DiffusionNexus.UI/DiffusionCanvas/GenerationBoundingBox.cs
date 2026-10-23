using Avalonia;

namespace DiffusionNexus.UI.DiffusionCanvas;

/// <summary>
/// The one movable, resizable box that declares the generation region.
///
/// Its <b>size</b> is the latent size, its <b>position</b> is where the pixels land, and whatever is
/// underneath it is what the model sees. That is the whole spatial model of the canvas — outpainting,
/// img2img and (later) inpainting all fall out of moving this rectangle rather than out of separate modes.
///
/// Coordinates are world units, which are generation pixels: a box 1024 wide always produces a 1024 px
/// latent regardless of zoom. Unlike the Image Editor's <c>CropTool</c> — which stores a normalised 0–1
/// rect precisely so zoom and pan need no recomputation — this box cannot use that trick, because the
/// pixel count is the point.
///
/// Holds no Avalonia visual types, so it is fully unit-testable without an Avalonia platform.
/// </summary>
public sealed class GenerationBoundingBox
{
    /// <summary>Default lattice the box snaps to. Matches the canvas dot grid and every current model's alignment.</summary>
    public const int DefaultAlignment = 64;

    /// <summary>Smallest generatable edge, in world units. Mirrors the previous frame model's floor.</summary>
    public const int MinSize = 256;

    /// <summary>Largest generatable edge, in world units. Mirrors the previous frame model's ceiling.</summary>
    public const int MaxSize = 2048;

    private int _alignment = DefaultAlignment;

    // Drag state, captured on BeginDrag so every DragTo is computed from the gesture's origin rather
    // than accumulated frame-by-frame (accumulating would drift once snapping quantises each step).
    private BoxHandle _dragHandle = BoxHandle.None;
    private Point _dragStartPointer;
    private double _dragStartX;
    private double _dragStartY;
    private int _dragStartWidth;
    private int _dragStartHeight;

    /// <summary>World X of the box's left edge.</summary>
    public double X { get; private set; }

    /// <summary>World Y of the box's top edge.</summary>
    public double Y { get; private set; }

    /// <summary>Box width in world units — the latent width handed to the backend.</summary>
    public int Width { get; private set; } = 1024;

    /// <summary>Box height in world units — the latent height handed to the backend.</summary>
    public int Height { get; private set; } = 1024;

    /// <summary>
    /// When true (the default) moves land on the <see cref="Alignment"/> lattice. The surface clears this
    /// while a modifier is held so a box can be placed off-grid deliberately.
    /// </summary>
    /// <remarks>
    /// This governs <b>position only</b>. The box's <b>size</b> always snaps, because a latent size off the
    /// model's lattice is not a preference — it is invalid input that every backend rejects. Letting a
    /// modifier produce one left the box in a state where Generate refused every subsequent click with no
    /// way for the user to see why.
    /// </remarks>
    public bool SnapPositionToGrid { get; set; } = true;

    /// <summary>
    /// The lattice sizes snap to. Set from the selected model's <c>DimensionAlignment</c> so the box can
    /// never propose a size the backend rejects — its validation throws lazily, on the first
    /// <c>MoveNextAsync</c>, long after a candidate has been created.
    /// </summary>
    /// <remarks>
    /// Assigning this always re-snaps the current size, even when the value is unchanged. The early return
    /// this used to have made the re-snap conditional on the alignment actually differing, which is the
    /// rarer case — the common one is "same alignment, box needs checking".
    ///
    /// The value is clamped into <c>[1, MaxSize]</c>, and callers must read it back from here rather than
    /// reusing the descriptor's raw field: a catalog entry with <c>DimensionAlignment = 0</c> is one typo
    /// away, and dividing by it downstream surfaces as a bare "Attempted to divide by zero". Above
    /// <see cref="MaxSize"/> the lattice has no generatable multiple at all, and <c>Math.Clamp</c> throws
    /// when its floor exceeds its ceiling.
    /// </remarks>
    public int Alignment
    {
        get => _alignment;
        set
        {
            var next = Math.Clamp(value, 1, MaxSize);
            var latticeMoved = next != _alignment;
            _alignment = next;

            var width = SnapSize(Width);
            var height = SnapSize(Height);
            if (width == Width && height == Height)
            {
                // The box did not move, but the lattice under it did, and the surface draws its dot grid
                // from this value. Staying silent left the grid showing 64px dots while the box snapped to
                // 16 — the drawn guide contradicting the actual behaviour.
                if (latticeMoved)
                    Raise();
                return;
            }

            Width = width;
            Height = height;
            Raise();
        }
    }

    /// <summary>True while a move or resize gesture is in progress.</summary>
    public bool IsDragging => _dragHandle != BoxHandle.None;

    /// <summary>The handle currently being dragged, or <see cref="BoxHandle.None"/>.</summary>
    public BoxHandle ActiveHandle => _dragHandle;

    /// <summary>The box as a world-space rectangle.</summary>
    public Rect WorldRect => new(X, Y, Width, Height);

    /// <summary>Raised on any change to position or size, so the surface and the readout can refresh.</summary>
    public event EventHandler? Changed;

    /// <summary>Moves the box without resizing it. Snaps when <see cref="SnapPositionToGrid"/> is set.</summary>
    public void SetPosition(double x, double y)
    {
        var nextX = SnapPositionToGrid ? SnapCoordinate(x) : x;
        var nextY = SnapPositionToGrid ? SnapCoordinate(y) : y;
        if (nextX == X && nextY == Y)
            return;

        X = nextX;
        Y = nextY;
        Raise();
    }

    /// <summary>Resizes the box around its top-left corner, snapping and clamping both edges.</summary>
    public void SetSize(int width, int height)
    {
        var nextW = SnapSize(width);
        var nextH = SnapSize(height);
        if (nextW == Width && nextH == Height)
            return;

        Width = nextW;
        Height = nextH;
        Raise();
    }

    /// <summary>Places the box so that its centre sits on <paramref name="worldCentre"/>.</summary>
    public void CenterOn(Point worldCentre) =>
        SetPosition(worldCentre.X - Width / 2.0, worldCentre.Y - Height / 2.0);

    /// <summary>
    /// Which part of the box a world-space point is over. Handles win over the body, so grabbing a corner
    /// of a small box resizes it rather than moving it.
    /// </summary>
    /// <param name="handleRadius">
    /// Half the handle's hit size, in <b>world</b> units. The surface converts its constant screen-space
    /// handle size through <see cref="CanvasViewport.ScreenToWorldLength"/>, so handles stay equally
    /// grabbable at every zoom.
    /// </param>
    /// <remarks>
    /// The radius is capped at a quarter of each edge so a movable core always survives. Without the cap,
    /// zooming out far enough makes the world-space radius exceed the box itself and all eight handle
    /// squares swallow the body — the box then cannot be moved at all, only resized.
    /// </remarks>
    public BoxHandle HitTest(Point world, double handleRadius)
    {
        var radiusX = Math.Min(handleRadius, Width / 4.0);
        var radiusY = Math.Min(handleRadius, Height / 4.0);

        foreach (var handle in ResizeHandles)
        {
            var centre = GetHandleCenter(handle);
            if (Math.Abs(world.X - centre.X) <= radiusX && Math.Abs(world.Y - centre.Y) <= radiusY)
                return handle;
        }

        return WorldRect.Contains(world) ? BoxHandle.Move : BoxHandle.None;
    }

    /// <summary>The eight resize handles, in the order <see cref="HitTest"/> considers them (corners first).</summary>
    public static readonly IReadOnlyList<BoxHandle> ResizeHandles =
    [
        BoxHandle.NorthWest, BoxHandle.NorthEast, BoxHandle.SouthEast, BoxHandle.SouthWest,
        BoxHandle.North, BoxHandle.East, BoxHandle.South, BoxHandle.West,
    ];

    /// <summary>World-space centre of a resize handle.</summary>
    public Point GetHandleCenter(BoxHandle handle)
    {
        double left = X, top = Y, right = X + Width, bottom = Y + Height;
        double midX = X + Width / 2.0, midY = Y + Height / 2.0;

        return handle switch
        {
            BoxHandle.NorthWest => new Point(left, top),
            BoxHandle.North => new Point(midX, top),
            BoxHandle.NorthEast => new Point(right, top),
            BoxHandle.East => new Point(right, midY),
            BoxHandle.SouthEast => new Point(right, bottom),
            BoxHandle.South => new Point(midX, bottom),
            BoxHandle.SouthWest => new Point(left, bottom),
            BoxHandle.West => new Point(left, midY),
            _ => new Point(midX, midY),
        };
    }

    /// <summary>Starts a move or resize gesture from a world-space pointer position.</summary>
    public void BeginDrag(BoxHandle handle, Point worldPointer)
    {
        if (handle == BoxHandle.None)
            return;

        _dragHandle = handle;
        _dragStartPointer = worldPointer;
        _dragStartX = X;
        _dragStartY = Y;
        _dragStartWidth = Width;
        _dragStartHeight = Height;
    }

    /// <summary>
    /// Continues the active gesture. Resizes pin the edge opposite the dragged handle, so grabbing the
    /// north-west corner grows the box up and to the left while its south-east corner stays put.
    /// </summary>
    public void DragTo(Point worldPointer)
    {
        if (_dragHandle == BoxHandle.None)
            return;

        var dx = worldPointer.X - _dragStartPointer.X;
        var dy = worldPointer.Y - _dragStartPointer.Y;

        if (_dragHandle == BoxHandle.Move)
        {
            SetPosition(_dragStartX + dx, _dragStartY + dy);
            return;
        }

        var west = _dragHandle is BoxHandle.NorthWest or BoxHandle.West or BoxHandle.SouthWest;
        var east = _dragHandle is BoxHandle.NorthEast or BoxHandle.East or BoxHandle.SouthEast;
        var north = _dragHandle is BoxHandle.NorthWest or BoxHandle.North or BoxHandle.NorthEast;
        var south = _dragHandle is BoxHandle.SouthWest or BoxHandle.South or BoxHandle.SouthEast;

        double width = _dragStartWidth;
        double height = _dragStartHeight;

        if (east) width = _dragStartWidth + dx;
        if (west) width = _dragStartWidth - dx;
        if (south) height = _dragStartHeight + dy;
        if (north) height = _dragStartHeight - dy;

        var nextWidth = SnapSize(width);
        var nextHeight = SnapSize(height);

        // Derive the position from the pinned edge and the *snapped* size. Snapping the position
        // separately here would let the pinned edge drift by up to one lattice cell per frame.
        var nextX = west ? _dragStartX + (_dragStartWidth - nextWidth) : _dragStartX;
        var nextY = north ? _dragStartY + (_dragStartHeight - nextHeight) : _dragStartY;

        if (nextX == X && nextY == Y && nextWidth == Width && nextHeight == Height)
            return;

        X = nextX;
        Y = nextY;
        Width = nextWidth;
        Height = nextHeight;
        Raise();
    }

    /// <summary>Ends the active gesture. Safe to call when nothing is being dragged.</summary>
    public void EndDrag() => _dragHandle = BoxHandle.None;

    /// <summary>
    /// Abandons the active gesture and restores the box to where it was when the gesture started.
    /// Called on <c>PointerCaptureLost</c>, which is otherwise the path that leaves a gesture half-applied.
    /// </summary>
    public void CancelDrag()
    {
        if (_dragHandle == BoxHandle.None)
            return;

        _dragHandle = BoxHandle.None;
        if (_dragStartX == X && _dragStartY == Y && _dragStartWidth == Width && _dragStartHeight == Height)
            return;

        X = _dragStartX;
        Y = _dragStartY;
        Width = _dragStartWidth;
        Height = _dragStartHeight;
        Raise();
    }

    /// <summary>
    /// Snaps a desired edge length to the lattice and clamps it into the generatable range. Always snaps —
    /// see the remarks on <see cref="SnapPositionToGrid"/> for why the modifier does not reach sizes.
    /// </summary>
    public int SnapSize(double desired)
    {
        if (double.IsNaN(desired))
            return MinSize;

        // Clamp before snapping so a wild drag delta cannot overflow the rounding, then walk the
        // snapped value back onto the lattice if rounding pushed it outside the bounds (possible when
        // the alignment does not divide MinSize/MaxSize).
        var clamped = Math.Clamp(desired, MinSize, MaxSize);
        var snapped = (int)Math.Round(clamped / _alignment) * _alignment;
        if (snapped > MaxSize)
            snapped -= _alignment;
        if (snapped < MinSize)
            snapped += _alignment;

        return Math.Clamp(snapped, _alignment, MaxSize);
    }

    private double SnapCoordinate(double value) => Math.Round(value / _alignment) * _alignment;

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
