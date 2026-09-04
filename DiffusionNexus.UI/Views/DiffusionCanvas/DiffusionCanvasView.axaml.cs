using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        if (ShouldLeaveTheKeyAlone(e.Key))
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
    /// True when the focused control genuinely owns <paramref name="key"/>, so the canvas must not claim it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These handlers run on the <b>tunnel</b> pass and claim their keys unconditionally, so any control
    /// that needs a key has to be excluded explicitly. Testing for "is a TextBox" was enough while the only
    /// input on this screen was the prompt; region B filled a whole column with sliders, combos and toggles.
    /// </para>
    /// <para>
    /// The exclusion is per <b>key</b>, not per control. Excluding every control in the panel wholesale
    /// looked simpler but was much worse: focus lands on a Button when it is clicked and stays there, and
    /// the panel has buttons that never disable (Unload, the seed controls, the LoRA picker's own), so one
    /// click on any of them killed every canvas and staging shortcut until the user clicked the canvas
    /// again. A Button does not consume Left, Right, Delete, F, G, B or 1 — only its activation key — so
    /// there is no reason to hand it those.
    /// </para>
    /// <para>
    /// Buttons are given <see cref="Key.Space"/> but deliberately not <see cref="Key.Enter"/>. Enter while
    /// candidates are staged means "accept", which the staging strip's own tooltip advertises, and a user
    /// who clicks a seed button and then presses Enter means to accept rather than to press it again. The
    /// canvas only claims Enter while the strip has candidates, so with nothing staged it still reaches the
    /// focused button as usual.
    /// </para>
    /// </remarks>
    private bool ShouldLeaveTheKeyAlone(Key key)
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

        return focused switch
        {
            // Typing wins outright: the prompt accepts spaces and returns.
            TextBox => true,

            // Arrows move the selection, Enter and Space open and commit, Escape closes, and letters
            // drive type-ahead — a ComboBox has a use for essentially every key.
            ComboBox => true,
            AutoCompleteBox => true,

            NumericUpDown => key is Key.Up or Key.Down or Key.PageUp or Key.PageDown,

            Slider => key is Key.Left or Key.Right or Key.Up or Key.Down
                or Key.Home or Key.End or Key.PageUp or Key.PageDown,

            // The staging strip: its own arrow navigation does the same thing as ours, so let it.
            ListBox => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End,

            // CheckBox before ToggleButton before Button: the first two derive from the last.
            CheckBox => key is Key.Space,
            ToggleButton => key is Key.Space,
            Button => key is Key.Space,

            _ => false,
        };
    }
}
