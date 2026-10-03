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
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using DiffusionNexus.UI.DiffusionCanvas;
using DiffusionNexus.UI.ViewModels;
using SkiaSharp;

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
///
/// The inpaint mask (#595) is a second child visual (<see cref="MaskLayer"/>) under the box, for the same
/// reason: a brush stroke redraws it on every pointer move, and nothing under it has changed. It is drawn
/// through Skia because the eraser needs a clearing blend, and only from immutable stroke snapshots, so
/// nothing the render thread reads can change under it.
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
    private static readonly IPen SelectedRasterOutline = new Pen(new SolidColorBrush(Color.Parse("#3D8BFD")), 2);
    private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.Parse("#1A1A1A")), 1);
    private static readonly IPen AntsBackPen = new Pen(new SolidColorBrush(Color.Parse("#141414")), 2);
    private static readonly IPen BrushCursorPen = new Pen(new SolidColorBrush(Color.Parse("#F0F0F0")), 1);
    private static readonly IPen BrushCursorBackPen = new Pen(new SolidColorBrush(Color.Parse("#141414")), 3);

    /// <summary>The mask overlay's colour: the repaint area, drawn translucent so the image shows through.</summary>
    private static readonly SKColor MaskOverlayColor = new(0xE0, 0x30, 0x30);

    /// <summary>The mask overlay's opacity, applied to the whole overlay so overlapping strokes do not darken.</summary>
    private const byte MaskOverlayAlpha = 110;

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
    private readonly MaskLayer _maskLayer;
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

    /// <summary>World points of the brush or eraser stroke in progress; null when not painting.</summary>
    private List<Point>? _paintPoints;
    private bool _paintIsErase;
    private double _paintSize;

    /// <summary>Where the pointer is, for the brush cursor; null when it is outside the control.</summary>
    private Point? _hoverScreen;

    private ICanvasMask? _observedMask;

    /// <summary>
    /// The mask's finished strokes recorded once as a picture, and the stroke list it was recorded from.
    /// The list is replaced on every change, so a different reference means a re-record; a brush drag then
    /// replays one picture plus the live stroke instead of rebuilding every stroke's path on each move.
    /// Reference-counted (<see cref="SharedPicture"/>): this field holds one reference and every frame's
    /// draw operation another, so a replaced picture's native memory is released as soon as the last
    /// frame replaying it is gone, rather than whenever the finalizer runs.
    /// </summary>
    private SharedPicture? _strokePicture;
    private IReadOnlyList<CanvasMaskStroke>? _strokePictureSource;

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

        _maskLayer = new MaskLayer(this);
        VisualChildren.Add(_maskLayer);
        LogicalChildren.Add(_maskLayer);

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

    public static readonly StyledProperty<object?> SelectedRasterProperty =
        AvaloniaProperty.Register<DiffusionCanvasSurface, object?>(nameof(SelectedRaster));

    /// <summary>
    /// The layer selected in the layer panel. It is outlined in solid accent blue, distinct from the
    /// box's marching ants, so the user can see which raster the inspector is editing.
    /// </summary>
    public object? SelectedRaster
    {
        get => GetValue(SelectedRasterProperty);
        set => SetValue(SelectedRasterProperty, value);
    }

    public static readonly StyledProperty<ICanvasMask?> MaskProperty =
        AvaloniaProperty.Register<DiffusionCanvasSurface, ICanvasMask?>(nameof(Mask));

    /// <summary>The inpaint mask, drawn as a translucent overlay and painted on with <see cref="PaintTool"/>.</summary>
    public ICanvasMask? Mask
    {
        get => GetValue(MaskProperty);
        set => SetValue(MaskProperty, value);
    }

    public static readonly StyledProperty<CanvasPaintTool> PaintToolProperty =
        AvaloniaProperty.Register<DiffusionCanvasSurface, CanvasPaintTool>(nameof(PaintTool));

    /// <summary>
    /// What a left drag does. While a paint tool is active a left press paints the mask and never grabs
    /// the box, so the box cannot be dragged by accident; panning, zooming and right-click still work.
    /// </summary>
    public CanvasPaintTool PaintTool
    {
        get => GetValue(PaintToolProperty);
        set => SetValue(PaintToolProperty, value);
    }

    public static readonly StyledProperty<double> BrushSizeProperty =
        AvaloniaProperty.Register<DiffusionCanvasSurface, double>(nameof(BrushSize), defaultValue: 64);

    /// <summary>Brush diameter in world pixels.</summary>
    public double BrushSize
    {
        get => GetValue(BrushSizeProperty);
        set => SetValue(BrushSizeProperty, value);
    }

    /// <summary>True when a left drag paints: a paint tool is active and the mask is there and visible.</summary>
    private bool CanPaintNow => PaintTool != CanvasPaintTool.None && Mask is { IsVisible: true } && BrushSize > 0;

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
        _hoverScreen = null;
        CommitPaintStroke();
        ReleaseGesture();

        // Recorded again on the next attach; nothing needs the picture while the canvas is not shown.
        _strokePicture?.Release();
        _strokePicture = null;
        _strokePictureSource = null;

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
        else if (change.Property == MaskProperty)
        {
            AttachMask(change.GetNewValue<ICanvasMask?>());
            _maskLayer.InvalidateVisual();
        }
        else if (change.Property == PaintToolProperty)
        {
            // The tool was put down (Escape, the mask hidden or deselected) while a stroke was in progress:
            // that stroke was never finished, so it is dropped rather than committed.
            if (_paintPoints is not null && !CanPaintNow)
            {
                _paintPoints = null;
                ReleaseGesture();
            }

            _maskLayer.InvalidateVisual();
            _boxLayer.InvalidateVisual();
            if (_hoverScreen is { } hover)
                UpdateCursor(hover);
        }
        else if (change.Property == BrushSizeProperty)
        {
            _boxLayer.InvalidateVisual();
        }
        else if (change.Property == PreviewImageProperty
              || change.Property == PreviewRectProperty
              || change.Property == IsPreviewHiddenProperty)
        {
            // The staged preview is drawn by the mask layer, above the mask.
            _maskLayer.InvalidateVisual();
        }
        else if (change.Property == ShowGridProperty
              || change.Property == SelectedRasterProperty)
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
        _maskLayer.InvalidateVisual();
        _boxLayer.InvalidateVisual();
    }

    private void AttachMask(ICanvasMask? mask)
    {
        if (_observedMask is not null)
            _observedMask.PropertyChanged -= OnMaskPropertyChanged;

        _observedMask = mask;

        if (_observedMask is not null)
            _observedMask.PropertyChanged += OnMaskPropertyChanged;

        // A stroke in progress belongs to the mask it started on.
        if (_paintPoints is not null)
        {
            _paintPoints = null;
            ReleaseGesture();
        }
    }

    private void OnMaskPropertyChanged(object? sender, PropertyChangedEventArgs e) => _maskLayer.InvalidateVisual();

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

        // An inverted mask is drawn clipped to the box, so its overlay has to follow the box.
        if (Mask is { Invert: true })
            _maskLayer.InvalidateVisual();

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
        if (_paintPoints is not null)
        {
            // Escape mid-stroke takes the stroke back.
            _paintPoints = null;
            _maskLayer.InvalidateVisual();
        }

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

        if (point.Properties.IsLeftButtonPressed && PaintTool != CanvasPaintTool.None)
        {
            // A paint tool owns the left button, even over the box, so painting near the box never moves
            // it. Without a paintable mask the press does nothing rather than falling through to the box.
            if (CanPaintNow)
                BeginPaint(e.Pointer, Viewport.ScreenToWorld(screen));

            e.Handled = true;
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

        if (_paintPoints is not null)
        {
            ExtendPaint(Viewport.ScreenToWorld(screen));
            _hoverScreen = screen;
            _boxLayer.InvalidateVisual();
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

        _hoverScreen = screen;
        if (PaintTool != CanvasPaintTool.None)
            _boxLayer.InvalidateVisual();

        UpdateCursor(screen);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        if (_paintPoints is not null)
            return;

        _hoverScreen = null;
        _boxLayer.InvalidateVisual();
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

        CommitPaintStroke();
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

        // What was drawn before the capture went is kept: the user saw it land.
        CommitPaintStroke();
        ReleaseGesture();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (e.Delta.Y == 0)
            return;

        // Shift+wheel resizes the brush while a paint tool is active, as in the Image Editor. The host
        // binds BrushSize two-way and clamps it.
        if (PaintTool != CanvasPaintTool.None && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Clamped here as well as by the host: at a limit the host's value does not change, so it
            // raises nothing and the binding would leave this side holding the unclamped size.
            SetCurrentValue(BrushSizeProperty, CanvasBrush.Step(BrushSize, grow: e.Delta.Y > 0));
            e.Handled = true;
            return;
        }

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
            // The command refuses a locked layer, so the item renders disabled. The reason goes in the
            // header because Avalonia does not show tooltips on disabled items by default.
            Header = raster is ILayerStackItem { IsLocked: true }
                ? "Delete result (locked: unlock it in the layer panel first)"
                : "Delete result",
            Command = command,
            CommandParameter = raster,
        });
        flyout.ShowAt(this, showAtPointer: true);
    }

    private void BeginPaint(IPointer pointer, Point world)
    {
        _paintPoints = [world];
        _paintIsErase = PaintTool == CanvasPaintTool.Eraser;
        _paintSize = BrushSize;
        Capture(pointer);
        _maskLayer.InvalidateVisual();
    }

    private void ExtendPaint(Point world)
    {
        // Skip points closer than a quarter of the brush (at least one screen pixel): a slow drag would
        // otherwise record hundreds of points that change nothing a round-capped path draws.
        var last = _paintPoints![^1];
        var minStep = Math.Max(_paintSize / 4, Viewport.ScreenToWorldLength(1));
        var dx = world.X - last.X;
        var dy = world.Y - last.Y;
        if (dx * dx + dy * dy < minStep * minStep)
            return;

        _paintPoints.Add(world);
        _maskLayer.InvalidateVisual();
    }

    /// <summary>Hands the stroke in progress to the mask as one finished stroke. A no-op when not painting.</summary>
    private void CommitPaintStroke()
    {
        if (_paintPoints is not { Count: > 0 } points)
            return;

        _paintPoints = null;
        if (Mask is { IsVisible: true } mask)
            mask.AddStroke(new CanvasMaskStroke(points, _paintSize, _paintIsErase));

        _maskLayer.InvalidateVisual();
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
        _paintPoints = null;
        ApplyCursor(IsSpaceHeld ? CursorFor(StandardCursorType.Hand) : Cursor.Default);
    }

    private void UpdateCursor(Point screen)
    {
        if (IsSpaceHeld)
            return;

        if (PaintTool != CanvasPaintTool.None)
        {
            // The brush circle drawn by the box layer is the real cursor; the cross marks its centre.
            ApplyCursor(CursorFor(StandardCursorType.Cross));
            return;
        }

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

    /// <summary>
    /// The bottom of the stack: background, grid, origin and accepted rasters. The mask and the staged
    /// preview are drawn by <see cref="MaskLayer"/>, the box by <see cref="BoxLayer"/>.
    /// </summary>
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(BackgroundBrush, bounds);

        if (ShowGrid)
            DrawGrid(context, bounds);

        DrawOrigin(context);
        DrawRasters(context, bounds);
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
            // Hidden means hidden: no pixels and no outline. A layer too faint to count (below 4 %) is
            // treated the same, because the hit test and the compositor skip it too: drawing its outline
            // would invite a right-click that lands on the layer underneath. One rule, IsShown, for all three.
            if (!CanvasRegionCompositor.IsShown(raster))
                continue;

            var screen = Viewport.WorldToScreen(raster.WorldRect);
            if (!screen.Intersects(bounds))
                continue;

            using (context.PushOpacity(Math.Clamp(raster.Opacity, 0.0, 1.0)))
            {
                if (raster.FrameImage is { } image)
                    context.DrawImage(image, new Rect(image.Size), screen);
                else
                    context.FillRectangle(RasterPlaceholder, screen);
            }

            context.DrawRectangle(null, RasterOutline, screen);
        }

        // Outlined even when hidden, so a selected hidden layer can still be found on the canvas.
        if (SelectedRaster is ICanvasRaster selected)
        {
            var screen = Viewport.WorldToScreen(selected.WorldRect);
            if (screen.Intersects(bounds))
                context.DrawRectangle(null, SelectedRasterOutline, screen.Inflate(1));
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
        DrawBrushCursor(context);

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

    /// <summary>The brush's true on-screen size around the pointer, while a paint tool is active.</summary>
    private void DrawBrushCursor(DrawingContext context)
    {
        if (PaintTool == CanvasPaintTool.None || _hoverScreen is not { } centre)
            return;

        var size = _paintPoints is not null ? _paintSize : BrushSize;
        var radius = Math.Max(2, size * Viewport.Zoom / 2);
        context.DrawEllipse(null, BrushCursorBackPen, centre, radius, radius);
        context.DrawEllipse(null, BrushCursorPen, centre, radius, radius);
    }

    /// <summary>
    /// The mask overlay's draw operation for this frame, or null when there is nothing to draw: no mask,
    /// a hidden mask, or a mask with no strokes that is not inverted.
    /// </summary>
    private MaskDrawOperation? CreateMaskDrawOperation()
    {
        if (Mask is not { IsVisible: true } mask)
            return null;

        var committed = StrokePicture(mask.Strokes);
        var live = _paintPoints is { Count: > 0 } points
            ? new CanvasMaskStroke(points.ToArray(), _paintSize, _paintIsErase)
            : null;

        if (committed is null && live is null && !mask.Invert)
            return null;

        // Inverted, the repaint area is the box minus the painting, so the overlay is confined to the box.
        Rect? clip = mask.Invert ? (Box is { } box ? Viewport.WorldToScreen(box.WorldRect) : null) : null;
        if (mask.Invert && clip is null)
            return null;

        // The operation takes its own reference, released when Avalonia disposes it with its frame.
        return new MaskDrawOperation(
            new Rect(Bounds.Size), committed?.Acquire(), live, Viewport.Zoom, Viewport.PanX, Viewport.PanY, clip);
    }

    /// <summary>The picture of <paramref name="strokes"/>, re-recorded only when the list was replaced.</summary>
    private SharedPicture? StrokePicture(IReadOnlyList<CanvasMaskStroke> strokes)
    {
        if (ReferenceEquals(strokes, _strokePictureSource))
            return _strokePicture;

        _strokePictureSource = strokes;
        _strokePicture?.Release();
        _strokePicture = null;
        if (strokes.Count == 0)
            return null;

        var bounds = strokes[0].Bounds;
        foreach (var stroke in strokes)
            bounds = bounds.Union(stroke.Bounds);

        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(
            (float)bounds.X, (float)bounds.Y, (float)bounds.Right, (float)bounds.Bottom));
        CanvasMaskRasterizer.DrawStrokes(canvas, strokes, MaskOverlayColor);
        _strokePicture = new SharedPicture(recorder.EndRecording());
        return _strokePicture;
    }

    /// <summary>
    /// An <see cref="SKPicture"/> shared between the surface and the frames replaying it, disposed when
    /// the last holder releases it. Thread-safe: frames are released on the render thread.
    /// </summary>
    internal sealed class SharedPicture
    {
        private int _references = 1;

        public SharedPicture(SKPicture picture) => Picture = picture;

        public SKPicture Picture { get; }

        /// <summary>True once the picture has been disposed.</summary>
        public bool IsReleased => Volatile.Read(ref _references) == 0;

        /// <summary>Takes another reference. Only called while the caller still holds one.</summary>
        public SharedPicture Acquire()
        {
            Interlocked.Increment(ref _references);
            return this;
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
                Picture.Dispose();
        }
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
    /// The inpaint mask and, above it, the staged preview, as one visual between the rasters and the box.
    /// A stroke in progress redraws only this. The preview sits above the mask so a candidate is judged
    /// without the red tint over the very area it repainted; holding the compare key hides the preview
    /// and shows the canvas with its mask. Not hit-testable: every pointer gesture belongs to the surface.
    /// </summary>
    private sealed class MaskLayer : Control
    {
        private readonly DiffusionCanvasSurface _owner;

        public MaskLayer(DiffusionCanvasSurface owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context)
        {
            if (_owner.CreateMaskDrawOperation() is { } operation)
                context.Custom(operation);

            _owner.DrawPreview(context);
        }
    }

    /// <summary>
    /// Draws the mask's repaint area through Skia, from an immutable snapshot: the finished strokes as a
    /// recorded picture and the live stroke as a copy, so the render thread reads nothing the UI thread can
    /// change. Strokes are drawn opaque into a layer that is composited translucent, so overlaps do not
    /// darken; erasers clear within that layer; an inverted mask fills the box and clears the painting.
    /// </summary>
    private sealed class MaskDrawOperation : ICustomDrawOperation
    {
        private readonly SharedPicture? _committed;
        private readonly CanvasMaskStroke? _live;
        private int _disposed;
        private readonly double _zoom;
        private readonly double _panX;
        private readonly double _panY;
        private readonly Rect? _invertClip;

        public MaskDrawOperation(
            Rect bounds, SharedPicture? committed, CanvasMaskStroke? live, double zoom, double panX, double panY, Rect? invertClip)
        {
            Bounds = bounds;
            _committed = committed;
            _live = live;
            _zoom = zoom;
            _panX = panX;
            _panY = panY;
            _invertClip = invertClip;
        }

        public Rect Bounds { get; }

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose()
        {
            // Once only: the reference this frame took is the one it gives back.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _committed?.Release();
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
                return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;
            var bounds = new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height);

            canvas.Save();
            canvas.ClipRect(bounds);
            if (_invertClip is { } clip)
                canvas.ClipRect(new SKRect((float)clip.X, (float)clip.Y, (float)clip.Right, (float)clip.Bottom));

            using (var layerPaint = new SKPaint { Color = SKColors.White.WithAlpha(MaskOverlayAlpha) })
                canvas.SaveLayer(bounds, layerPaint);

            canvas.Save();
            canvas.Translate((float)_panX, (float)_panY);
            canvas.Scale((float)_zoom);
            // The picture replays its erasers' clearing blend into this layer, like drawing the strokes would.
            if (_committed is not null)
                canvas.DrawPicture(_committed.Picture);
            if (_live is not null)
                CanvasMaskRasterizer.DrawStrokes(canvas, [_live], MaskOverlayColor);
            canvas.Restore();

            if (_invertClip is not null)
            {
                // Xor with an opaque fill: full colour where nothing was painted, clear where it was, and a
                // soft blend on antialiased edges.
                using var fill = new SKPaint { Color = MaskOverlayColor, BlendMode = SKBlendMode.Xor };
                canvas.DrawRect(bounds, fill);
            }

            canvas.Restore();   // the translucent layer
            canvas.Restore();   // the clips
        }
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
