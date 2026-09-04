using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using DiffusionNexus.UI.Views.Controls;

namespace DiffusionNexus.UI.Views.DiffusionCanvas;

/// <summary>
/// Code-behind for the Diffusion Canvas.
///
/// Pointer gestures live in <see cref="DiffusionCanvasSurface"/>; this file owns the keyboard, because
/// the shortcuts act on the view model (staging) as often as on the surface. Keys are taken on the
/// <b>tunnel</b> pass deliberately: this screen hosts a dozen Buttons, and an Avalonia Button activates
/// on Space and Enter while focused, so a bubbling handler would never see the staging shortcuts. The
/// price of tunnelling is that the prompt TextBox has to be excluded explicitly — the same guard
/// <c>ImageViewerDialog</c> uses.
/// </summary>
public partial class DiffusionCanvasView : UserControl
{
    public DiffusionCanvasView()
    {
        InitializeComponent();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private DiffusionCanvasViewModel? ViewModel => DataContext as DiffusionCanvasViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (Surface is not { } surface)
            return;

        surface.PropertyChanged += OnSurfacePropertyChanged;
        UpdateZoomReadout();

        // Every module view is one long-lived instance swapped through the shell's ContentControl, so the
        // canvas is detached and re-attached on every navigation and keyboard focus is lost each time.
        // Nothing restores it, so take focus here or the shortcuts are dead after the first visit.
        Dispatcher.UIThread.Post(() => surface.Focus(), DispatcherPriority.Input);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Surface is { } surface)
            surface.PropertyChanged -= OnSurfacePropertyChanged;

        base.OnDetachedFromVisualTree(e);
    }

    private void OnSurfacePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == DiffusionCanvasSurface.ZoomProperty)
            UpdateZoomReadout();
    }

    private void UpdateZoomReadout()
    {
        if (ZoomReadout is null || Surface is null)
            return;

        ZoomReadout.Text = string.Format(CultureInfo.InvariantCulture, "{0:0}%", Surface.Viewport.Zoom * 100);
    }

    // ────────────────────────────── Toolbar ──────────────────────────────

    private void OnFitClicked(object? sender, RoutedEventArgs e) => Surface?.FitToContent();

    private void OnOneToOneClicked(object? sender, RoutedEventArgs e) => Surface?.ResetZoom();

    private void OnCenterBoxClicked(object? sender, RoutedEventArgs e) => Surface?.CenterOnBox();

    // ────────────────────────────── Keyboard ──────────────────────────────

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ShouldLeaveTheKeyAlone())
            return;

        var vm = ViewModel;
        var staging = vm?.Staging;
        var hasCandidates = staging?.HasCandidates == true;

        switch (e.Key)
        {
            case Key.F:
                Surface?.FitToContent();
                e.Handled = true;
                return;

            case Key.D1 or Key.NumPad1:
                Surface?.ResetZoom();
                e.Handled = true;
                return;

            case Key.B:
                Surface?.CenterOnBox();
                e.Handled = true;
                return;

            case Key.G:
                if (vm is not null)
                {
                    vm.ShowGrid = !vm.ShowGrid;
                    e.Handled = true;
                }

                return;

            case Key.Escape:
                Surface?.CancelActiveGesture();
                e.Handled = true;
                return;

            case Key.Space:
                // Space means "flip the candidate against the canvas" while anything is staged, and
                // "arm drag-to-pan" when nothing is. Both are in the spec; the strip decides which.
                if (hasCandidates)
                    staging!.IsComparing = true;
                else
                    Surface?.SetSpaceHeld(true);

                e.Handled = true;
                return;

            case Key.Left when hasCandidates:
                staging!.PreviousCommand.Execute(null);
                e.Handled = true;
                return;

            case Key.Right when hasCandidates:
                staging!.NextCommand.Execute(null);
                e.Handled = true;
                return;

            case Key.Enter when hasCandidates:
                staging!.AcceptCommand.Execute(null);
                e.Handled = true;
                return;

            case Key.Delete when hasCandidates:
                staging!.DiscardCommand.Execute(null);
                e.Handled = true;
                return;
        }
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space)
            return;

        // Released unconditionally, even while typing: a space typed into the prompt must never leave the
        // canvas stuck in compare mode or with panning armed.
        if (ViewModel?.Staging is { } staging)
            staging.IsComparing = false;

        Surface?.SetSpaceHeld(false);
    }

    /// <summary>
    /// True when the focused control owns the keystroke, so the canvas must not claim it.
    /// </summary>
    /// <remarks>
    /// Two cases. A focused <see cref="TextBox"/> anywhere, because the prompt accepts returns and spaces.
    /// And <b>anything inside the generate panel</b>, because these handlers run on the tunnel pass and
    /// claim their keys unconditionally: a focused Slider would lose Left/Right to the staging strip, a
    /// ComboBox would lose Enter and Space, and the batch spinner would lose its arrows. Testing for
    /// "is a TextBox" was enough while the only input on this screen was the prompt; region B filled a
    /// whole column with sliders, combos and toggles.
    /// </remarks>
    private bool ShouldLeaveTheKeyAlone()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is TextBox)
            return true;

        return focused is Visual visual
            && GeneratePanel is { } panel
            && (ReferenceEquals(visual, panel) || visual.GetVisualAncestors().Contains(panel));
    }
}
