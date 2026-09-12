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
    /// the key was one of the four scroll keys with no modifier and the event did not originate inside
    /// a text input; otherwise leaves the event untouched so the caller can handle its own keys.
    /// </summary>
    public static bool HandleKey(ScrollViewer? scroll, KeyEventArgs e)
    {
        if (scroll is null || e.Handled || e.KeyModifiers != KeyModifiers.None)
            return false;
        if (IsInsideTextInput(e.Source))
            return false;

        switch (e.Key)
        {
            case Key.Home:
                scroll.ScrollToHome();
                break;
            case Key.End:
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
        }
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || IsInsideTextInput(e.Source))
            return;
        scroll.Focus();
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is ScrollViewer scroll)
            HandleKey(scroll, e);
    }

    /// <summary>
    /// A <c>TextBox</c> — or anything built on one, such as <c>AutoCompleteBox</c> or <c>NumericUpDown</c>,
    /// whose events originate from the inner <c>TextBox</c> — owns these keys.
    /// </summary>
    private static bool IsInsideTextInput(object? source) =>
        source is Visual visual && visual.FindAncestorOfType<TextBox>(includeSelf: true) is not null;
}
