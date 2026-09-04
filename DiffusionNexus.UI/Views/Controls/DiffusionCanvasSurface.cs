using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using DiffusionNexus.UI.DiffusionCanvas;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// The Diffusion Canvas drawing surface: an unbounded world containing every accepted result, with one
/// marching-ants bounding box that declares the generation region.
///
/// This is a hand-written <see cref="Control"/> rather than a <c>ZoomBorder</c> host, following the same
/// shape as <c>ImageEditorControl</c> (custom control + a transform object as the single source of
/// truth). The reasons are concrete: <c>ZoomBorder</c>'s <c>Matrix</c>/<c>ZoomX</c>/<c>OffsetX</c> are
/// read-only, it transforms through <c>RenderTransform</c> so handles and borders scale with zoom, its
/// two-finger rotation gesture is on by default and a rotated world silently breaks every axis-aligned
/// hit-test, and its zoom/pan bounds default to infinity. Unlike <c>ImageEditorControl</c> this renders
/// through Avalonia's own <see cref="DrawingContext"/> instead of leasing a Skia canvas — there is no
/// per-frame Skia work to do here, and staying on the UI thread avoids the compositor-render-thread
/// bitmap race that <c>ImageEditorCoreRenderRaceTests</c> exists to guard.
///
/// The bounding box is drawn by a child visual (<see cref="BoxLayer"/>) rather than in this control's own
/// <see cref="Render"/>: the marching ants animate at 20 fps for as long as the tab is open, and
/// invalidating the whole surface for that re-issued the grid's hundreds of dot fills plus a
/// <c>DrawImage</c> per accepted raster on an idle screen. The child layer is the only thing the ants tick
/// invalidates.
/// </summary>
public class DiffusionCanvasSurface : Control
{
    // ── Appearance constants. Hex literals in-line match the house convention: there is no shared
    //    theme dictionary in this repo (see REUSABLES.md §6).
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#242424"));
    private static readonly IBrush DotBrush = new SolidColorBrush(Color.Parse("#3E3E3E"));
    private static readonly IBrush MajorDotBrush = new SolidColorBrush(Color.Parse("#585858"));
    private static readonly IBrush OriginBrush = new SolidColorBrush(Color.Parse("#E0A030"));
    private static readonly IBrush HandleFill = new SolidColorBrush(Color.Parse("#F0F0F0"));
    private static readonly IBrush ReadoutBackground = new SolidColorBrush(Color.Parse("#CC1A1A1A"));
    private static readonly IBrush ReadoutForeground = new SolidColorBrush(Color.Parse("#EDEDED"));
    private static readonly IBrush RasterPlaceholder = new SolidColorBrush(Color.Parse("#1A1A1A"));
    private static readonly IPen RasterOutline = new Pen(new SolidColorBrush(Color.Parse("#4A4A4A")), 1);
    private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.Parse("#1A1A1A")), 1);
    private static readonly IPen AntsBackPen = new Pen(new SolidColorBrush(Color.Parse("#141414")), 2);

    /// <summary>Screen-space edge length of a resize handle. Constant, so handles never scale with zoom.</summary>
    private const double HandleScreenSize = 10;

    /// <summary>Screen-space grab radius for a handle — deliberately larger than the drawn handle.</summary>
    private const double HandleHitScreenRadius = 9;

    /// <summary>Smallest screen spacing the dot grid is allowed to use before it steps up a lattice multiple.</summary>
    private const double MinDotSpacing = 28;

    /// <summary>Marching-ants animation tick. ~20 fps is enough to read as motion.</summary>
    private static readonly TimeSpan AntsInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Length of the ants' dash pattern (4 on, 4 off), which is the cycle of distinct offsets.</summary>
    private const int AntsPeriod = 8;

    /// <summary>
    /// One immutable pen per dash offset, built once. The offset cycles through <see cref="AntsPeriod"/>
    /// values, so allocating a <c>Pen</c> plus a <c>DashStyle</c> on every tick was pure garbage.
    /// </summary>
    private static readonly ImmutablePen[] AntsPens = BuildAntsPens();

    private static ImmutablePen[] BuildAntsPens()
    {
        var white = new ImmutableSolidColorBrush(Colors.White);
        var pens = new ImmutablePen[AntsPeriod];
        for (var offset = 0; offset < AntsPeriod; offset++)
            pens[offset] = new ImmutablePen(white, 1.5, new ImmutableDashStyle([4, 4], offset));
        return pens;
    }

    /// <summary>
    /// The standard cursors this control uses, created on first use. <c>Cursor</c> is disposable and
    /// platform-backed; allocating a fresh one on every pointer move (the previous behaviour) leaked one
    /// per event and pushed a platform cursor update even when the shape had not changed.
    /// </summary>
    private static readonly Dictionary<StandardCursorType, Cursor> CursorCache = [];

    private readonly DispatcherTimer _antsTimer;
    private readonly BoxLayer _boxLayer;
    private int _antsOffset;

    /// <summary>
    /// The lattice the dot grid was last drawn for. Tracked so a box change only repaints the whole
    /// surface when the lattice actually moved — a box drag raises Changed on every pointer move, and
    /// repainting the grid for each of those is exactly what the box layer exists to avoid.
    /// </summary>
    private int _gridAlignment = GenerationBoundingBox.DefaultAlignment;

    // Gesture state. A single pointer at a time — the canvas has no multi-touch gestures.
    private IPointer? _capturedPointer;
    private bool _isPanning;
    private Point _panLastScreen;
    private bool _isDraggingBox;

    /// <summary>The raster under a right-button press, resolved on press and acted on at release.</summary>
    private ICanvasRaster? _contextRaster;

    private INotifyCollectionChanged? _observedCollection;
    private readonly List<INotifyPropertyChanged> _observedItems = [];
    private GenerationBoundingBox? _observedBox;
    private CanvasViewport? _observedViewport;

    // The readout's shaped text, rebuilt only when its string changes — it is drawn on every ants tick.
    private string? _readoutText;
    private FormattedText? _readoutFormatted;

    public DiffusionCanvasSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        Viewport = new CanvasViewport();
        _observedViewport = Viewport;
        Viewport.Changed += OnViewportChanged;

        _boxLayer = new BoxLayer(this);
        VisualChildren.Add(_boxLayer);
        LogicalChildren.Add(_boxLayer);

        _antsTimer = new DispatcherTimer { Interval = AntsInterval };
        _antsTimer.Tick += OnAntsTick;
    }

    /// <summary>The world transform. Exposed so the hosting view can drive Fit / 1:1 from the toolbar.</summary>
    public CanvasViewport Viewport { get; }

    // ────────────────────────────────── Properties ──────────────────────────────────

    public static readonly StyledProperty<IEnumerable?> RastersProperty =
        AvaloniaProperty.Register<DiffusionCanvasSurface, IEnumerable?>(nameof(Rasters));

    /// <summary>The accepted results on the canvas. Items must implement <see cref="ICanvasRaster"/>.</summary>
    public IEnumerable? Rasters
    {
        get => GetValue(RastersProperty);
        set => SetValue(RastersProperty, value);
    }

    public static readonly StyledProperty<GenerationBoundingBox?> BoxProperty =
        AvaloniaProperty.Register<DiffusionCanvasSurface, GenerationBoundingBox?>(nameof(Box));

    /// <summary>The generation region. Null hides the box entirely.</summary>
    public GenerationBoundingBox? Box
    {
        get => GetValue(BoxProperty);
        set => SetValue(BoxProperty, value);
    }

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<DiffusionCanvasSurface, bool>(nameof(ShowGrid), defaultValue: true);

    /// <summary>Whether the dot grid is drawn.</summary>
    public bool ShowGrid
    {
        get => GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public static readonly StyledProperty<IImage?> PreviewImageProperty =
        AvaloniaProperty.Register<DiffusionCanvasSurface, IImage?>(nameof(PreviewImage));

    /// <summary>
    /// The staged candidate drawn inside the bounding box. It is a preview only — nothing reaches
    /// <see cref="Rasters"/> until the user accepts it.
    /// </summary>
    public IImage? PreviewImage
    {
        get => GetValue(PreviewImageProperty);
        set => SetValue(PreviewImageProperty, value);
    }

    public static readonly StyledProperty<Rect> PreviewRectProperty =
        AvaloniaProperty.Register<DiffusionCanvasSurface, Rect>(nameof(PreviewRect));

    /// <summary>World rectangle the staged candidate occupies (the box as it was when generation started).</summary>
    public Rect PreviewRect
    {
        get => GetValue(PreviewRectProperty);
        set => SetValue(PreviewRectProperty, value);
    }

    public static readonly StyledProperty<bool> IsPreviewHiddenProperty =
        AvaloniaProperty.Register<DiffusionCanvasSurface, bool>(nameof(IsPreviewHidden));

    /// <summary>
    /// Set while the user holds the compare key, which hides the candidate so the canvas underneath shows
    /// through. The comparison gesture is the point of staging — a variant cannot be judged against nothing.
    /// </summary>
    public bool IsPreviewHidden
    {
        get => GetValue(IsPreviewHiddenProperty);
        set => SetValue(IsPreviewHiddenProperty, value);
    }

    public static readonly StyledProperty<bool> SpacePanEnabledProperty =
        AvaloniaProperty.Register<DiffusionCanvasSurface, bool>(nameof(SpacePanEnabled), defaultValue: true);

    /// <summary>
    /// Whether holding space arms drag-to-pan. The host clears this while candidates are staged, because
    /// space then means "flip the candidate against the canvas" instead.
    /// </summary>
    public bool SpacePanEnabled
    {
        get => GetValue(SpacePanEnabledProperty);
        set => SetValue(SpacePanEnabledProperty, value);
    }

    public static readonly StyledProperty<ICommand?> DeleteRasterCommandProperty =
        AvaloniaProperty.Register<DiffusionCanvasSurface, ICommand?>(nameof(DeleteRasterCommand));

    /// <summary>
    /// Invoked with the <see cref="ICanvasRaster"/> under the pointer when the user picks "Delete result"
    /// from the right-click flyout. Null disables the flyout entirely.
    /// </summary>
    public ICommand? DeleteRasterCommand
    {
        get => GetValue(DeleteRasterCommandProperty);
        set => SetValue(DeleteRasterCommandProperty, value);
    }

    private static readonly DirectProperty<DiffusionCanvasSurface, double> ZoomPropertyInternal =
        AvaloniaProperty.RegisterDirect<DiffusionCanvasSurface, double>(nameof(Zoom), o => o.Zoom);

    /// <summary>Current zoom, mirrored out of the viewport so the status bar can show it.</summary>
    public static readonly DirectProperty<DiffusionCanvasSurface, double> ZoomProperty = ZoomPropertyInternal;

    private double _zoom = 1.0;

    public double Zoom
    {
        get => _zoom;
        private set => SetAndRaise(ZoomPropertyInternal, ref _zoom, value);
    }

    /// <summary>True while space is held and drag-to-pan is armed.</summary>
    public bool IsSpaceHeld { get; private set; }

    // ────────────────────────────────── Lifecycle ──────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _antsTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // The view model is a DI singleton that outlives every navigation, so an un-stopped timer here
        // would keep ticking (and keep this control alive) for the rest of the session.
        _antsTimer.Stop();
        IsSpaceHeld = false;
        _contextRaster = null;
        ReleaseGesture();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == RastersProperty)
        {
            AttachRasters(change.GetNewValue<IEnumerable?>());
            InvalidateVisual();
        }
        else if (change.Property == BoxProperty)
        {
            AttachBox(change.GetNewValue<GenerationBoundingBox?>());
            _boxLayer.InvalidateVisual();
        }
        else if (change.Property == ShowGridProperty
              || change.Property == PreviewImageProperty
              || change.Property == PreviewRectProperty
              || change.Property == IsPreviewHiddenProperty)
        {
            InvalidateVisual();
        }
    }

    private void OnAntsTick(object? sender, EventArgs e)
    {
        if (Box is null)
            return;

        _antsOffset = (_antsOffset + 1) % AntsPeriod;
        // Only the box layer: the grid, rasters and preview underneath have not changed.
        _boxLayer.InvalidateVisual();
    }

    private void OnViewportChanged(object? sender, EventArgs e)
    {
        Zoom = Viewport.Zoom;
        InvalidateVisual();
        _boxLayer.InvalidateVisual();
    }

    private void AttachBox(GenerationBoundingBox? box)
    {
        if (_observedBox is not null)
            _observedBox.Changed -= OnObservedBoxChanged;

        _observedBox = box;

        if (_observedBox is not null)
            _observedBox.Changed += OnObservedBoxChanged;
    }

    private void AttachRasters(IEnumerable? rasters)
    {
        if (_observedCollection is not null)
            _observedCollection.CollectionChanged -= OnRastersCollectionChanged;

        foreach (var item in _observedItems)
            item.PropertyChanged -= OnRasterPropertyChanged;
        _observedItems.Clear();

        _observedCollection = rasters as INotifyCollectionChanged;
        if (_observedCollection is not null)
            _observedCollection.CollectionChanged += OnRastersCollectionChanged;

        if (rasters is null)
            return;

        foreach (var item in rasters)
        {
            if (item is not INotifyPropertyChanged observable)
                continue;

            observable.PropertyChanged += OnRasterPropertyChanged;
            _observedItems.Add(observable);
        }
    }

    private void OnRastersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Re-subscribing wholesale is cheap at canvas scale and immune to Reset, which carries no items.
        AttachRasters(Rasters);
        InvalidateVisual();
    }

    private void OnRasterPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private void OnObservedBoxChanged(object? sender, EventArgs e)
    {
        _boxLayer.InvalidateVisual();

        // Selecting a model re-snaps the box onto that model's lattice, and the grid draws the same
        // lattice, so it has to be re-recorded when that value moves.
        if (Box is { } box && box.Alignment != _gridAlignment)
        {
            _gridAlignment = box.Alignment;
            InvalidateVisual();
        }
    }

    // ────────────────────────────────── Public gestures ──────────────────────────────────

    /// <summary>Frames every accepted raster plus the bounding box; falls back to the box alone.</summary>
    public void FitToContent()
    {
        var content = GetContentWorldBounds();
        if (content is null)
            return;

        Viewport.Fit(content.Value, Bounds.Size);
    }

    /// <summary>Zoom 1:1 — one generated pixel per screen pixel.</summary>
    public void ResetZoom() => Viewport.OneToOne(Bounds.Size);

    /// <summary>Centres the viewport on the bounding box without changing the zoom.</summary>
    public void CenterOnBox()
    {
        if (Box is { } box)
            Viewport.CenterOn(box.WorldRect.Center, Bounds.Size);
    }

    /// <summary>Zooms about the viewport centre — the keyboard/toolbar equivalent of the wheel gesture.</summary>
    public void ZoomBy(double factor) =>
        Viewport.ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), factor);

    /// <summary>Arms or disarms space-drag panning. Called by the host's key handler.</summary>
    public void SetSpaceHeld(bool held)
    {
        if (!SpacePanEnabled)
            held = false;
        if (IsSpaceHeld == held)
            return;

        IsSpaceHeld = held;
        ApplyCursor(held ? CursorFor(StandardCursorType.Hand) : Cursor.Default);
    }

    /// <summary>Abandons an in-progress box gesture, restoring the box to where the drag began.</summary>
    public void CancelActiveGesture()
    {
        if (_isDraggingBox && Box is { } box)
        {
            box.CancelDrag();
            // Same restore as the release paths. Escape ends the drag, so the still-held button's
            // eventual release skips its own restore branch, and Alt would otherwise stay in effect for
            // every later SetPosition — CenterOn included.
            box.SnapPositionToGrid = true;
        }

        ReleaseGesture();
        _boxLayer.InvalidateVisual();
    }

    private Rect? GetContentWorldBounds()
    {
        Rect? bounds = null;

        foreach (var raster in EnumerateRasters())
        {
            var rect = raster.WorldRect;
            bounds = bounds is null ? rect : bounds.Value.Union(rect);
        }

        if (Box is { } box)
            bounds = bounds is null ? box.WorldRect : bounds.Value.Union(box.WorldRect);

        return bounds;
    }

    private IEnumerable<ICanvasRaster> EnumerateRasters()
    {
        if (Rasters is null)
            yield break;

        foreach (var item in Rasters)
        {
            if (item is ICanvasRaster raster)
                yield return raster;
        }
    }

    // ────────────────────────────────── Pointer ──────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetCurrentPoint(this);
        var screen = point.Position;

        if (point.Properties.IsMiddleButtonPressed || (IsSpaceHeld && point.Properties.IsLeftButtonPressed))
        {
            BeginPan(e.Pointer, screen);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            // Context menu on a result: resolved on press, shown on release (the platform convention),
            // and only when the press landed on a raster, so right-clicking empty canvas does nothing.
            if (!_isPanning && !_isDraggingBox)
                _contextRaster = CanvasRasterHitTest.TopmostAt(EnumerateRasters(), Viewport.ScreenToWorld(screen));
            return;
        }

        if (!point.Properties.IsLeftButtonPressed || Box is not { } box)
            return;

        var world = Viewport.ScreenToWorld(screen);
        var handle = box.HitTest(world, Viewport.ScreenToWorldLength(HandleHitScreenRadius));
        if (handle == BoxHandle.None)
            return;

        box.BeginDrag(handle, world);
        _isDraggingBox = true;
        Capture(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var screen = e.GetPosition(this);

        if (_isPanning)
        {
            Viewport.PanBy(screen.X - _panLastScreen.X, screen.Y - _panLastScreen.Y);
            _panLastScreen = screen;
            e.Handled = true;
            return;
        }

        if (_isDraggingBox && Box is { } dragging)
        {
            // Alt suspends POSITION snapping only. Sizes always snap: a latent size off the model's
            // lattice is invalid input, not a preference, and one produced here would outlive the
            // gesture and make Generate refuse every subsequent click.
            dragging.SnapPositionToGrid = !e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            dragging.DragTo(Viewport.ScreenToWorld(screen));
            e.Handled = true;
            return;
        }

        UpdateCursor(screen);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            var raster = _contextRaster;
            _contextRaster = null;
            if (raster is not null && DeleteRasterCommand is { } command)
            {
                ShowRasterFlyout(raster, command);
                e.Handled = true;
            }

            return;
        }

        if (_isDraggingBox && Box is { } box)
        {
            box.EndDrag();
            // Restore position snapping for the next gesture regardless of how this one ended.
            box.SnapPositionToGrid = true;
        }

        ReleaseGesture();
        UpdateCursor(e.GetPosition(this));
    }

    /// <summary>
    /// Losing capture (a context menu opening, the control being detached on navigation, a system drag)
    /// must end the gesture. Nothing else in this repository handles this event, and the old canvas's
    /// per-pointer gesture dictionaries were exactly where that bit: state removed only on
    /// PointerReleased silently re-arms on the next move.
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        if (_isDraggingBox && Box is { } box)
        {
            box.EndDrag();
            box.SnapPositionToGrid = true;
        }

        ReleaseGesture();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (e.Delta.Y == 0)
            return;

        var factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        Viewport.ZoomAt(e.GetPosition(this), factor);
        e.Handled = true;
    }

    /// <summary>
    /// The per-result flyout. Delete is the only live entry for now; the placeholders the old
    /// per-frame ContextMenu carried (send to Image Editor, ControlNet reference, copy seed / prompt) come
    /// back as real commands when their features ship (TODO(v2-context-menu)).
    /// </summary>
    private void ShowRasterFlyout(ICanvasRaster raster, ICommand command)
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(new MenuItem
        {
            Header = "Delete result",
            Command = command,
            CommandParameter = raster,
        });
        flyout.ShowAt(this, showAtPointer: true);
    }

    private void BeginPan(IPointer pointer, Point screen)
    {
        _isPanning = true;
        _panLastScreen = screen;
        ApplyCursor(CursorFor(StandardCursorType.SizeAll));
        Capture(pointer);
    }

    private void Capture(IPointer pointer)
    {
        _capturedPointer = pointer;
        pointer.Capture(this);
    }

    private void ReleaseGesture()
    {
        if (_capturedPointer is { } pointer)
        {
            // Only release if we still hold it — calling Capture(null) on a pointer another control has
            // taken would steal it back.
            if (ReferenceEquals(pointer.Captured, this))
                pointer.Capture(null);
            _capturedPointer = null;
        }

        _isPanning = false;
        _isDraggingBox = false;
        ApplyCursor(IsSpaceHeld ? CursorFor(StandardCursorType.Hand) : Cursor.Default);
    }

    private void UpdateCursor(Point screen)
    {
        if (IsSpaceHeld)
            return;

        if (Box is not { } box)
        {
            ApplyCursor(Cursor.Default);
            return;
        }

        var handle = box.HitTest(Viewport.ScreenToWorld(screen), Viewport.ScreenToWorldLength(HandleHitScreenRadius));
        ApplyCursor(CursorFor(handle switch
        {
            BoxHandle.NorthWest or BoxHandle.SouthEast => StandardCursorType.TopLeftCorner,
            BoxHandle.NorthEast or BoxHandle.SouthWest => StandardCursorType.TopRightCorner,
            BoxHandle.North or BoxHandle.South => StandardCursorType.SizeNorthSouth,
            BoxHandle.East or BoxHandle.West => StandardCursorType.SizeWestEast,
            BoxHandle.Move => StandardCursorType.SizeAll,
            _ => StandardCursorType.Arrow,
        }));
    }

    /// <summary>One shared <see cref="Cursor"/> per shape, created lazily on the UI thread.</summary>
    private static Cursor CursorFor(StandardCursorType type)
    {
        if (type == StandardCursorType.Arrow)
            return Cursor.Default;

        if (!CursorCache.TryGetValue(type, out var cursor))
        {
            cursor = new Cursor(type);
            CursorCache[type] = cursor;
        }

        return cursor;
    }

    /// <summary>Assigns only on change, so an unchanged shape costs neither a property change nor a platform call.</summary>
    private void ApplyCursor(Cursor cursor)
    {
        if (!ReferenceEquals(Cursor, cursor))
            Cursor = cursor;
    }

    // ────────────────────────────────── Render ──────────────────────────────────

    /// <summary>Everything except the box: background, grid, origin, accepted rasters, staged preview.</summary>
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(BackgroundBrush, bounds);

        if (ShowGrid)
            DrawGrid(context, bounds);

        DrawOrigin(context);
        DrawRasters(context, bounds);
        DrawPreview(context);
    }

    private void DrawGrid(DrawingContext context, Rect bounds)
    {
        // The box's own lattice, not a fixed 64: the box snaps to the selected model's DimensionAlignment
        // (16 for FLUX.2-klein and both Qwen models), and a grid drawn at a different spacing tells the
        // user their box is snapping somewhere it is not.
        // Step up until the dots are far enough apart to read; this also bounds the dot count, so zooming
        // out cannot turn the grid into tens of thousands of fills per frame.
        var step = (double)(Box?.Alignment ?? GenerationBoundingBox.DefaultAlignment);
        while (step * Viewport.Zoom < MinDotSpacing)
            step *= 2;

        var spacing = step * Viewport.Zoom;
        if (spacing <= 0 || double.IsInfinity(spacing))
            return;

        var topLeft = Viewport.ScreenToWorld(bounds.TopLeft);
        var bottomRight = Viewport.ScreenToWorld(bounds.BottomRight);

        var startX = Math.Floor(topLeft.X / step) * step;
        var startY = Math.Floor(topLeft.Y / step) * step;

        const double dotSize = 2;
        var majorEvery = Math.Max(step, 512);

        for (var worldX = startX; worldX <= bottomRight.X; worldX += step)
        {
            for (var worldY = startY; worldY <= bottomRight.Y; worldY += step)
            {
                var screen = Viewport.WorldToScreen(new Point(worldX, worldY));
                var isMajor = Math.Abs(worldX % majorEvery) < 0.001 && Math.Abs(worldY % majorEvery) < 0.001;
                context.FillRectangle(
                    isMajor ? MajorDotBrush : DotBrush,
                    new Rect(screen.X - dotSize / 2, screen.Y - dotSize / 2, dotSize, dotSize));
            }
        }
    }

    private void DrawOrigin(DrawingContext context)
    {
        var origin = Viewport.WorldToScreen(new Point(0, 0));
        context.FillRectangle(OriginBrush, new Rect(origin.X - 3, origin.Y - 3, 6, 6));
    }

    private void DrawRasters(DrawingContext context, Rect bounds)
    {
        foreach (var raster in EnumerateRasters())
        {
            var screen = Viewport.WorldToScreen(raster.WorldRect);
            if (!screen.Intersects(bounds))
                continue;

            if (raster.FrameImage is { } image)
                context.DrawImage(image, new Rect(image.Size), screen);
            else
                context.FillRectangle(RasterPlaceholder, screen);

            context.DrawRectangle(null, RasterOutline, screen);
        }
    }

    private void DrawPreview(DrawingContext context)
    {
        if (IsPreviewHidden || PreviewImage is not { } image)
            return;

        var rect = PreviewRect;
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        context.DrawImage(image, new Rect(image.Size), Viewport.WorldToScreen(rect));
    }

    /// <summary>The bounding box: ants, handles and readout. Drawn by <see cref="BoxLayer"/>.</summary>
    private void DrawBox(DrawingContext context)
    {
        if (Box is not { } box)
            return;

        var screen = Viewport.WorldToScreen(box.WorldRect);

        // Marching ants: a dark backing stroke plus a dashed light stroke whose offset animates, so the
        // box never reads as a committed frame the way a solid border would.
        context.DrawRectangle(null, AntsBackPen, screen);
        context.DrawRectangle(null, AntsPens[_antsOffset], screen);

        DrawHandles(context, box);
        DrawReadout(context, box, screen);
    }

    private void DrawHandles(DrawingContext context, GenerationBoundingBox box)
    {
        // Handles are laid out in screen space at a constant size, so they stay grabbable at 0.1x and
        // do not become dinner plates at 8x.
        foreach (var handle in GenerationBoundingBox.ResizeHandles)
        {
            var centre = Viewport.WorldToScreen(box.GetHandleCenter(handle));
            var rect = new Rect(
                centre.X - HandleScreenSize / 2,
                centre.Y - HandleScreenSize / 2,
                HandleScreenSize,
                HandleScreenSize);

            context.FillRectangle(HandleFill, rect);
            context.DrawRectangle(null, HandlePen, rect);
        }
    }

    private void DrawReadout(DrawingContext context, GenerationBoundingBox box, Rect screen)
    {
        // Invariant culture: the dev machine is German-locale and a comma decimal separator in a pixel
        // readout reads as a thousands separator.
        var text = string.Format(
            CultureInfo.InvariantCulture,
            "{0} x {1}   @ {2}, {3}",
            box.Width, box.Height, (int)Math.Round(box.X), (int)Math.Round(box.Y));

        if (_readoutFormatted is null || !string.Equals(text, _readoutText, StringComparison.Ordinal))
        {
            _readoutText = text;
            _readoutFormatted = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                ReadoutForeground);
        }

        var formatted = _readoutFormatted;

        const double padding = 5;
        var width = formatted.Width + padding * 2;
        var height = formatted.Height + padding * 2;

        // Preferred position is just above the box's top-left corner, but the readout must stay legible
        // when the box is larger than the viewport or scrolled off it — which is the zoomed-in case this
        // exists for. Clamping into the control's bounds handles every direction; re-anchoring by a fixed
        // offset only worked while the box's top edge was within a few pixels of the top edge.
        var x = Math.Clamp(screen.X, 0, Math.Max(0, Bounds.Width - width));
        var y = Math.Clamp(screen.Y - height - 4, 0, Math.Max(0, Bounds.Height - height));

        var boxRect = new Rect(x, y, width, height);
        context.FillRectangle(ReadoutBackground, boxRect);
        context.DrawText(formatted, new Point(boxRect.X + padding, boxRect.Y + padding));
    }

    /// <summary>
    /// The bounding box as its own visual, layered over the surface. Its <see cref="Render"/> is the only
    /// thing the marching-ants timer invalidates, so the animation never re-records the grid, the rasters
    /// or the preview. Not hit-testable: every pointer gesture belongs to the surface.
    /// </summary>
    private sealed class BoxLayer : Control
    {
        private readonly DiffusionCanvasSurface _owner;

        public BoxLayer(DiffusionCanvasSurface owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context) => _owner.DrawBox(context);
    }
}
