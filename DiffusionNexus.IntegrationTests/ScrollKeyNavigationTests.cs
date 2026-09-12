using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DiffusionNexus.UI.Behaviors;
using FluentAssertions;

namespace DiffusionNexus.IntegrationTests;

/// <summary>
/// <see cref="ScrollKeyNavigation"/> gives a <c>ScrollViewer</c> the Home / End / Page Up / Page Down
/// handling Avalonia only ships for <c>ListBox</c>-style controls (issue #564). Keys are sent through
/// the headless window's input pipeline, so these cover the focus story (click-then-key, text boxes
/// keep their own keys) and not just the key → offset table.
/// Same fixture rules as <see cref="ScrollViewerPaddingBehaviourTests"/>: the <c>[AvaloniaFact]</c>
/// session is themeless, so the content is a plain panel and the <c>ScrollViewer</c> gets an explicit
/// template with a <c>PART_ContentPresenter</c>.
/// </summary>
public class ScrollKeyNavigationTests
{
    [AvaloniaFact]
    public void Home_scrolls_to_the_top()
    {
        var (window, scroll, _) = Build();
        try
        {
            scroll.Focus().Should().BeTrue("the behavior makes the ScrollViewer focusable");
            ScrollTo(window, scroll, 2 * scroll.Viewport.Height);

            window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
            Layout(window);

            scroll.Offset.Y.Should().Be(0);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void End_scrolls_to_the_bottom()
    {
        var (window, scroll, _) = Build();
        try
        {
            scroll.Focus().Should().BeTrue();

            window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
            Layout(window);

            scroll.Offset.Y.Should().Be(scroll.Extent.Height - scroll.Viewport.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PageDown_scrolls_one_viewport_down()
    {
        var (window, scroll, _) = Build();
        try
        {
            scroll.Focus().Should().BeTrue();

            window.KeyPressQwerty(PhysicalKey.PageDown, RawInputModifiers.None);
            Layout(window);

            scroll.Offset.Y.Should().Be(scroll.Viewport.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PageUp_scrolls_one_viewport_up()
    {
        var (window, scroll, _) = Build();
        try
        {
            scroll.Focus().Should().BeTrue();
            ScrollTo(window, scroll, 2 * scroll.Viewport.Height);

            window.KeyPressQwerty(PhysicalKey.PageUp, RawInputModifiers.None);
            Layout(window);

            scroll.Offset.Y.Should().Be(scroll.Viewport.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Keys_are_ignored_while_a_TextBox_inside_has_focus()
    {
        var (window, scroll, textBox) = Build();
        try
        {
            textBox.Focus().Should().BeTrue();
            var start = 2 * scroll.Viewport.Height;
            ScrollTo(window, scroll, start);

            window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
            Layout(window);

            scroll.Offset.Y.Should().Be(start, "Home inside a text box moves the caret, not the grid");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Pressing_on_a_tile_then_End_scrolls_without_an_explicit_Focus()
    {
        var (window, scroll, _) = Build();
        try
        {
            var tile = ((Panel)scroll.Content!).Children.OfType<Border>().First();
            Press(tile, window);
            Layout(window);
            scroll.IsFocused.Should().BeTrue("a pointer press inside the grid hands focus to the ScrollViewer");

            window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
            Layout(window);

            scroll.Offset.Y.Should().Be(scroll.Extent.Height - scroll.Viewport.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Pressing_in_a_TextBox_does_not_move_focus_to_the_grid()
    {
        var (window, scroll, textBox) = Build();
        try
        {
            textBox.Focus().Should().BeTrue();

            Press(textBox, window);
            Layout(window);

            scroll.IsFocused.Should().BeFalse("the text box keeps focus so its own Home/End keep working");
            textBox.IsFocused.Should().BeTrue();
        }
        finally
        {
            window.Close();
        }
    }

    // ---- ForwardKeys: the host view forwards the keys when focus sits on a control that would
    // otherwise claim them (a TabItem header switches tabs on Home/End — issue #564 follow-up). ----

    [AvaloniaFact]
    public void ForwardKeys_scrolls_the_grid_while_a_tab_header_holds_focus()
    {
        var f = BuildHosted();
        try
        {
            f.Header.Focus().Should().BeTrue();

            f.Window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
            Layout(f.Window);

            f.Scroll.Offset.Y.Should().Be(f.Scroll.Extent.Height - f.Scroll.Viewport.Height);
            f.HeaderSaw.Should().BeEmpty("the header must not get to switch tabs");
        }
        finally
        {
            f.Window.Close();
        }
    }

    [AvaloniaFact]
    public void ForwardKeys_leaves_the_key_to_the_header_while_the_grid_is_hidden()
    {
        var f = BuildHosted();
        try
        {
            f.Scroll.IsVisible = false;
            Layout(f.Window);
            f.Header.Focus().Should().BeTrue();

            f.Window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
            Layout(f.Window);

            f.HeaderSaw.Should().Equal(new[] { Key.End }, "another tab is showing, so End means 'last tab' again");
            f.Scroll.Offset.Y.Should().Be(0);
        }
        finally
        {
            f.Window.Close();
        }
    }

    [AvaloniaFact]
    public void ForwardKeys_leaves_the_key_to_the_header_while_the_grid_is_detached()
    {
        var f = BuildHosted();
        try
        {
            // What a TabControl actually does with a non-selected tab's content: it is not in the tree.
            f.Host.Children.Remove(f.Scroll);
            Layout(f.Window);
            f.Header.Focus().Should().BeTrue();

            f.Window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
            Layout(f.Window);

            f.HeaderSaw.Should().Equal(Key.End);
        }
        finally
        {
            f.Window.Close();
        }
    }

    [AvaloniaFact]
    public void ForwardKeys_leaves_the_key_to_a_TextBox_in_the_host()
    {
        var f = BuildHosted();
        try
        {
            f.SearchBox.Focus().Should().BeTrue();

            f.Window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
            Layout(f.Window);

            f.Scroll.Offset.Y.Should().Be(0, "End in the search box moves the caret");
        }
        finally
        {
            f.Window.Close();
        }
    }

    private sealed record HostedFixture(Window Window, Panel Host, Border Header, TextBox SearchBox, ScrollViewer Scroll, List<Key> HeaderSaw);

    /// <summary>
    /// A host panel standing in for a view with a TabControl: a focusable "tab header" that answers
    /// Home/End on the bubble pass (as <c>TabControl</c> does), a search box, and the tile grid.
    /// Themeless session, so no real TabControl — it would never get a template.
    /// </summary>
    private static HostedFixture BuildHosted()
    {
        var headerSaw = new List<Key>();
        var header = new Border { Width = 120, Height = 32, Background = Brushes.DarkGray, Focusable = true };
        header.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Home or Key.End)
            {
                headerSaw.Add(e.Key);
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
        var searchBox = new TextBox { Width = 200, Height = 32 };

        var items = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16) };
        for (var i = 0; i < 24; i++)
        {
            items.Children.Add(new Border { Width = 340, Height = 440, Margin = new Thickness(8), Background = Brushes.Gray });
        }
        var scroll = new ScrollViewer
        {
            Content = items,
            Template = new FuncControlTemplate<ScrollViewer>((sv, ns) => new ScrollContentPresenter
            {
                Name = "PART_ContentPresenter",
                [!ContentPresenter.ContentProperty] = sv[!ContentControl.ContentProperty],
                [!ContentPresenter.PaddingProperty] = sv[!TemplatedControl.PaddingProperty],
            }.RegisterInNameScope(ns)),
        };
        ScrollKeyNavigation.SetIsEnabled(scroll, true);

        var host = new DockPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Height = 40 };
        top.Children.Add(header);
        top.Children.Add(searchBox);
        DockPanel.SetDock(top, Dock.Top);
        host.Children.Add(top);
        host.Children.Add(scroll);
        ScrollKeyNavigation.ForwardKeys(host, scroll);

        var window = new Window { Width = 1400, Height = 900, Content = host };
        window.Show();
        Layout(window);
        scroll.Extent.Height.Should().BeGreaterThan(3 * scroll.Viewport.Height, "the fixture must scroll more than two pages");
        return new HostedFixture(window, host, header, searchBox, scroll, headerSaw);
    }

    /// <summary>
    /// Raises the real <c>PointerPressed</c> routed event on <paramref name="target"/>, so it bubbles
    /// through the actual visual tree to the behavior's handler. Not <c>window.MouseDown(...)</c>:
    /// that goes through compositor hit-testing, which is dead for <c>[AvaloniaFact]</c> windows once
    /// <see cref="TestAppHost"/> has set up its second Avalonia platform in the same process (the press
    /// then hits nothing and is dropped — confirmed by running the suite with and without the
    /// <c>TestAppHost</c> classes). Only the hit-test step is skipped; routing and focus are real.
    /// </summary>
    private static void Press(Control target, Window window)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var position = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        target.RaiseEvent(new PointerPressedEventArgs(
            target,
            pointer,
            window,
            position,
            timestamp: 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
    }

    [AvaloniaFact]
    public void HandleKey_leaves_other_keys_to_the_caller()
    {
        var (window, scroll, _) = Build();
        try
        {
            var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A };

            ScrollKeyNavigation.HandleKey(scroll, e).Should().BeFalse();

            e.Handled.Should().BeFalse();
            scroll.Offset.Y.Should().Be(0);
        }
        finally
        {
            window.Close();
        }
    }

    private static (Window window, ScrollViewer scroll, TextBox textBox) Build()
    {
        // A plain panel: templated controls (ItemsControl etc.) never get a template in the themeless
        // headless session, so the scrolled content would stay empty.
        var items = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16) };
        var textBox = new TextBox { Width = 200, Height = 32, Margin = new Thickness(8) };
        items.Children.Add(textBox);
        for (var i = 0; i < 24; i++)
        {
            items.Children.Add(new Border { Width = 340, Height = 440, Margin = new Thickness(8), Background = Brushes.Gray });
        }
        var scroll = new ScrollViewer
        {
            Content = items,
            // Themeless session: supply the Fluent template's core so the presenter produces a real extent.
            Template = new FuncControlTemplate<ScrollViewer>((sv, ns) => new ScrollContentPresenter
            {
                Name = "PART_ContentPresenter",
                [!ContentPresenter.ContentProperty] = sv[!ContentControl.ContentProperty],
                [!ContentPresenter.PaddingProperty] = sv[!TemplatedControl.PaddingProperty],
            }.RegisterInNameScope(ns)),
        };
        ScrollKeyNavigation.SetIsEnabled(scroll, true);
        var window = new Window { Width = 1400, Height = 900, Content = scroll };
        window.Show();
        Layout(window);
        scroll.Extent.Height.Should().BeGreaterThan(3 * scroll.Viewport.Height, "the fixture must scroll more than two pages");
        return (window, scroll, textBox);
    }

    private static void ScrollTo(Window window, ScrollViewer scroll, double y)
    {
        scroll.Offset = new Vector(0, y);
        Layout(window);
        scroll.Offset.Y.Should().Be(y, "the starting offset must be exactly where the test assumes");
    }

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
