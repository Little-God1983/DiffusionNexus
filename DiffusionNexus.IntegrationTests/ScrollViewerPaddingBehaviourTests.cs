using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAssertions;

namespace DiffusionNexus.IntegrationTests;

/// <summary>
/// Canary for the Avalonia layout behaviour behind <c>DiffusionNexus.Tests.Views.ScrollViewerPaddingLintTests</c>:
/// a stretchable child inside a <c>ScrollViewer</c> with <c>Padding</c> is arranged shorter than it
/// measured and the padding is missing from the extent, so its bottom can never be scrolled into
/// view. The same inset applied as <c>Margin</c> on the content is fully scrollable. If the
/// "Padding" test starts failing, Avalonia fixed it upstream and the lint can be retired.
/// </summary>
public class ScrollViewerPaddingBehaviourTests : IClassFixture<TestAppHost>
{
    private const double Inset = 16;

    public ScrollViewerPaddingBehaviourTests(TestAppHost _)
    {
    }

    [AvaloniaFact]
    public void Margin_on_content_keeps_the_content_bottom_reachable()
    {
        var (window, scroll, content) = Build(useMargin: true);
        try
        {
            ScrollToEnd(window, scroll);

            var contentBottom = content.TranslatePoint(new Point(0, content.Bounds.Height), window)!.Value.Y;
            var viewportBottom = scroll.TranslatePoint(new Point(0, scroll.Bounds.Height), window)!.Value.Y;

            content.Bounds.Height.Should().Be(content.DesiredSize.Height - 2 * Inset, "bounds exclude the margin only");
            (viewportBottom - contentBottom).Should().Be(Inset, "the bottom inset is scrolled into view");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Padding_on_ScrollViewer_hides_the_content_bottom_CANARY()
    {
        var (window, scroll, content) = Build(useMargin: false);
        try
        {
            ScrollToEnd(window, scroll);

            var contentBottom = content.TranslatePoint(new Point(0, content.Bounds.Height), window)!.Value.Y;
            var viewportBottom = scroll.TranslatePoint(new Point(0, scroll.Bounds.Height), window)!.Value.Y;

            content.Bounds.Height.Should().Be(content.DesiredSize.Height - 2 * Inset,
                "Avalonia arranges the child 2×Padding shorter than it measured (the bug this canary pins)");
            scroll.Extent.Height.Should().Be(content.Bounds.Height, "the extent omits the padding (the bug this canary pins)");
            (contentBottom - viewportBottom).Should().Be(Inset,
                "the last rows of the content sit below the viewport even at max offset (the bug this canary pins)");
        }
        finally
        {
            window.Close();
        }
    }

    private static (Window window, ScrollViewer scroll, Control content) Build(bool useMargin)
    {
        // A plain panel: templated controls (ItemsControl etc.) never get a template in the themeless
        // headless session, so the scrolled content would stay empty.
        var items = new WrapPanel { Orientation = Orientation.Horizontal, Margin = useMargin ? new Thickness(Inset) : default };
        for (var i = 0; i < 24; i++)
        {
            items.Children.Add(new Border { Width = 340, Height = 440, Margin = new Thickness(8), Background = Brushes.Gray });
        }
        var scroll = new ScrollViewer
        {
            Padding = useMargin ? default : new Thickness(Inset),
            Content = items,
            // The [AvaloniaFact] session runs a themeless Application (see CivitaiBrowserViewDeferredLoadTests),
            // under which ScrollViewer falls back to the generic ContentControl theme. Supply the same
            // core the Fluent template uses — a ScrollContentPresenter with Content and Padding bound to
            // the ScrollViewer — since it is exactly that presenter's arrange/extent behaviour this pins.
            Template = new FuncControlTemplate<ScrollViewer>((sv, ns) => new ScrollContentPresenter
            {
                Name = "PART_ContentPresenter",
                [!ContentPresenter.ContentProperty] = sv[!ContentControl.ContentProperty],
                [!ContentPresenter.PaddingProperty] = sv[!TemplatedControl.PaddingProperty],
            }.RegisterInNameScope(ns)),
        };
        var window = new Window { Width = 1400, Height = 900, Content = scroll };
        window.Show();
        Layout(window);
        return (window, scroll, items);
    }

    private static void ScrollToEnd(Window window, ScrollViewer scroll)
    {
        scroll.Extent.Height.Should().BeGreaterThan(scroll.Viewport.Height, "the fixture must actually scroll");
        scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
        Layout(window);
    }

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
