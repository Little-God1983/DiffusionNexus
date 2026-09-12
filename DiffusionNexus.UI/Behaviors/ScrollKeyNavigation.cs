using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace DiffusionNexus.UI.Behaviors;

/// <summary>
/// Home / End / Page Up / Page Down for a <see cref="ScrollViewer"/>. Avalonia only ships these keys
/// for <c>ListBox</c>-style controls; a bare <c>ScrollViewer</c> over an <c>ItemsControl</c> grid ignores
/// them (issue #564).
/// <para>
/// <c>behaviors:ScrollKeyNavigation.IsEnabled="True"</c> on a <c>ScrollViewer</c> makes it focusable,
/// focuses it on any pointer press inside it that nobody else claimed and is not in a text input,
/// and maps the four keys to <see cref="ScrollViewer.ScrollToHome"/>, <see cref="ScrollViewer.ScrollToEnd"/>,
/// <see cref="ScrollViewer.PageUp"/> and <see cref="ScrollViewer.PageDown"/>. Both handlers run on the
/// bubble pass, so a <c>TextBox</c> keeps its native Home/End and a card handler that marks the press
/// handled (Gallery, Dataset Management) keeps its own focus.
/// </para>
/// <para>
/// Views that hold focus themselves call <see cref="HandleKey"/> from their own <c>OnKeyDown</c> so the
/// keys work whichever of the two has focus.
/// </para>
/// </summary>
public static class ScrollKeyNavigation
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(ScrollKeyNavigation));

    static ScrollKeyNavigation()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>(OnIsEnabledChanged);
    }

    public static bool GetIsEnabled(ScrollViewer element) => element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollViewer element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>
    /// Applies the key → scroll mapping. Returns <c>true</c> (and marks <paramref name="e"/> handled) when
    /// the key was one of the four scroll keys with no modifier, the event did not originate inside a
    /// control that owns these keys itself (text box, slider, NumericUpDown), and the content actually
    /// overflows; otherwise leaves the event untouched so the caller (or whatever is next on the route)
    /// can handle its own keys. <paramref name="loadEverythingBeforeEnd"/> lets a paged grid fetch the
    /// rest before End, so End means the real end.
    /// </summary>
    public static bool HandleKey(ScrollViewer? scroll, KeyEventArgs e, Action? loadEverythingBeforeEnd = null)
    {
        if (scroll is null || e.Handled || e.KeyModifiers != KeyModifiers.None)
            return false;
        if (ClaimsTheKeysItself(e.Source))
            return false;
        // A grid that fits has no use for the key. Claiming it anyway would take Home/End away from
        // the tab header (= first/last tab) for exactly the users with the least content.
        if (scroll.Extent.Height <= scroll.Viewport.Height)
            return false;

        switch (e.Key)
        {
            case Key.Home:
                scroll.ScrollToHome();
                break;
            case Key.End:
                if (loadEverythingBeforeEnd is not null)
                {
                    // The extent only grows after a layout pass, so lay out before scrolling.
                    loadEverythingBeforeEnd();
                    scroll.UpdateLayout();
                }
                scroll.ScrollToEnd();
                break;
            case Key.PageUp:
                scroll.PageUp();
                break;
            case Key.PageDown:
                scroll.PageDown();
                break;
            default:
                return false;
        }

        e.Handled = true;
        return true;
    }

    /// <summary>
    /// Makes the four keys reach <paramref name="scroll"/> from anywhere in the window while it is on
    /// screen — no click into the view first. A tunnel <c>KeyDown</c> handler goes on the window
    /// (<see cref="TopLevel"/>) for as long as <paramref name="host"/> is attached to it. Neither a
    /// bubble handler on the ScrollViewer nor one on the view is enough: right after a page opens,
    /// focus sits on the navigation control that opened it, outside the view, and a focused
    /// <c>TabItem</c> header answers Home/End itself (switching tabs) before the grid could.
    /// Controls that own these keys keep them (text box, slider, NumericUpDown — see
    /// <see cref="HandleKey"/>), and so does any other scroll area that holds focus, such as a detail
    /// pane laid over the grid. Call once per grid; when the host has several, the one on screen
    /// answers. <paramref name="loadEverythingBeforeEnd"/>: see <see cref="HandleKey"/>.
    /// </summary>
    public static void ForwardKeys(Control host, ScrollViewer scroll, Action? loadEverythingBeforeEnd = null)
    {
        EventHandler<KeyEventArgs> onWindowKeyDown = (_, e) =>
        {
            if (IsOnScreen(scroll) && !IsInsideAnotherScrollArea(e.Source, scroll))
                HandleKey(scroll, e, loadEverythingBeforeEnd);
        };

        TopLevel? hooked = null;
        void Hook()
        {
            if (hooked is not null) return;
            hooked = TopLevel.GetTopLevel(host);
            hooked?.AddHandler(InputElement.KeyDownEvent, onWindowKeyDown, RoutingStrategies.Tunnel);
        }
        void Unhook()
        {
            hooked?.RemoveHandler(InputElement.KeyDownEvent, onWindowKeyDown);
            hooked = null;
        }

        host.AttachedToVisualTree += (_, _) => Hook();
        host.DetachedFromVisualTree += (_, _) => Unhook();
        Hook(); // no-op until the host is attached (GetTopLevel is null before that)
    }

    /// <summary>
    /// A TabControl keeps a non-selected tab's content out of the visual tree entirely, and a hidden
    /// list (<c>IsVisible</c> bound to "has items") is attached but not effectively visible.
    /// </summary>
    private static bool IsOnScreen(ScrollViewer scroll) =>
        scroll.IsEffectivelyVisible && scroll.GetVisualRoot() is not null;

    /// <summary>
    /// The focused control's nearest scroll area owns its keys. A pane laid <em>over</em> the grid
    /// (LoraViewerView's detail overlay) leaves the grid effectively visible underneath, so visibility
    /// alone cannot tell the two apart — focus can.
    /// </summary>
    private static bool IsInsideAnotherScrollArea(object? source, ScrollViewer scroll) =>
        source is Visual visual
        && visual.FindAncestorOfType<ScrollViewer>(includeSelf: true) is { } nearest
        && nearest != scroll;

    private static void OnIsEnabledChanged(ScrollViewer scroll, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetNewValue<bool>())
        {
            scroll.Focusable = true;
            scroll.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble);
            scroll.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        }
        else
        {
            scroll.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            scroll.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            scroll.ClearValue(InputElement.FocusableProperty);
        }
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || ClaimsTheKeysItself(e.Source))
            return;
        scroll.Focus();
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is ScrollViewer scroll)
            HandleKey(scroll, e);
    }

    /// <summary>
    /// A <c>TextBox</c> (Home/End move the caret), a <c>Slider</c> (Home/End = min/max, Page keys =
    /// large step) or a <c>NumericUpDown</c> (Page keys) — or anything built on one, such as an
    /// <c>AutoCompleteBox</c> whose events originate from its inner <c>TextBox</c> — owns these keys.
    /// </summary>
    private static bool ClaimsTheKeysItself(object? source) =>
        source is Visual visual
        && (visual.FindAncestorOfType<TextBox>(includeSelf: true) is not null
            || visual.FindAncestorOfType<Slider>(includeSelf: true) is not null
            || visual.FindAncestorOfType<NumericUpDown>(includeSelf: true) is not null);
}
