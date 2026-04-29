using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.PanAndZoom;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;

namespace DiffusionNexus.UI.Views.DiffusionCanvas;

/// <summary>
/// Code-behind for the Diffusion Canvas. Owns the per-frame drag and resize gestures,
/// translating screen-space pointer movement into canvas-space updates on the bound
/// <see cref="GenerationFrameViewModel"/> while the user holds the left mouse button.
///
/// Pan and zoom of the canvas itself are handled entirely by <see cref="ZoomBorder"/>
/// (middle-click drag pans; Ctrl + wheel zooms) — we don't intercept those.
/// </summary>
public partial class DiffusionCanvasView : UserControl
{
    // Per-pointer drag state. Keyed by IPointer so multi-touch / pen + mouse don't
    // collide if a future revision wants to support simultaneous gestures.
    private readonly Dictionary<IPointer, DragState> _dragStates = new();
    private readonly Dictionary<IPointer, ResizeState> _resizeStates = new();

    public DiffusionCanvasView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private ZoomBorder? CanvasZoomBorder => this.FindControl<ZoomBorder>("CanvasZoom");

    /// <summary>
    /// Returns the inner element that hosts the absolute-positioned frames (the Canvas
    /// inside the ItemsControl). All pointer positions are computed relative to it so
    /// the values are in canvas-space (unaffected by ZoomBorder pan/zoom transforms).
    /// </summary>
    private Visual? CanvasContentVisual => CanvasZoomBorder?.Child as Visual;

    // ────────────────────────────── DRAG (move frame) ──────────────────────────────

    private void OnFramePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not GenerationFrameViewModel vm) return;
        if (vm.IsBusy) return; // can't move a frame while it's generating

        var props = e.GetCurrentPoint(border).Properties;
        if (!props.IsLeftButtonPressed) return;

        // Capture the pointer's CANVAS-space position so zoom/pan don't distort the delta.
        var canvasPoint = e.GetPosition(CanvasContentVisual);
        _dragStates[e.Pointer] = new DragState(
            StartPointerCanvasX: canvasPoint.X,
            StartPointerCanvasY: canvasPoint.Y,
            StartFrameX: vm.CanvasX,
            StartFrameY: vm.CanvasY);

        e.Pointer.Capture(border);
        e.Handled = true;
    }

    private void OnFramePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragStates.TryGetValue(e.Pointer, out var state)) return;
        if (sender is not Border border || border.DataContext is not GenerationFrameViewModel vm) return;

        var canvasPoint = e.GetPosition(CanvasContentVisual);
        vm.CanvasX = state.StartFrameX + (canvasPoint.X - state.StartPointerCanvasX);
        vm.CanvasY = state.StartFrameY + (canvasPoint.Y - state.StartPointerCanvasY);
    }

    private void OnFramePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStates.Remove(e.Pointer))
            e.Pointer.Capture(null);
    }

    // ────────────────────────────── RESIZE (bottom-right handle) ────────────────

    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border handle) return;
        // The handle's DataContext is the parent template's frame VM (binding inheritance).
        if (handle.DataContext is not GenerationFrameViewModel vm) return;
        if (vm.IsBusy) return;

        var props = e.GetCurrentPoint(handle).Properties;
        if (!props.IsLeftButtonPressed) return;

        var canvasPoint = e.GetPosition(CanvasContentVisual);
        _resizeStates[e.Pointer] = new ResizeState(
            StartPointerCanvasX: canvasPoint.X,
            StartPointerCanvasY: canvasPoint.Y,
            StartWidth: vm.Width,
            StartHeight: vm.Height);

        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnResizePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizeStates.TryGetValue(e.Pointer, out var state)) return;
        if (sender is not Border handle || handle.DataContext is not GenerationFrameViewModel vm) return;

        var canvasPoint = e.GetPosition(CanvasContentVisual);
        var deltaX = canvasPoint.X - state.StartPointerCanvasX;
        var deltaY = canvasPoint.Y - state.StartPointerCanvasY;

        // Snap to the model's alignment grid (Z-Image-Turbo: 64) and clamp to allowed range.
        vm.Width = GenerationFrameViewModel.SnapDimension(state.StartWidth + deltaX);
        vm.Height = GenerationFrameViewModel.SnapDimension(state.StartHeight + deltaY);
    }

    private void OnResizePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizeStates.Remove(e.Pointer))
            e.Pointer.Capture(null);
    }

    private readonly record struct DragState(
        double StartPointerCanvasX, double StartPointerCanvasY,
        double StartFrameX, double StartFrameY);

    private readonly record struct ResizeState(
        double StartPointerCanvasX, double StartPointerCanvasY,
        int StartWidth, int StartHeight);
}
