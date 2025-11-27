using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Classes;
using System;

namespace DiffusionNexus.UI.Views;

public partial class LoraHelperView : UserControl
{
    private const double DefaultItemHeight = 300;
    private const double DefaultItemWidth = 250;
    private const double ItemMargin = 10;
    private ScrollViewer? _scroll;
    private ItemsRepeater? _repeater;

    public LoraHelperView()
    {
        InitializeComponent();
        this.AttachedToVisualTree += OnAttached;
        this.DetachedFromVisualTree += OnDetached;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ConfigureViewModel();
        HookScrollViewer();
        HookItemsRepeater();
        UpdateActivePreviewRange();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        UnhookScrollViewer();
        UnhookItemsRepeater();
    }

    private void ConfigureViewModel()
    {
        if (DataContext is LoraHelperViewModel vm && VisualRoot is Window window)
        {
            vm.DialogService = new DialogService(window);
            vm.SetWindow(window);
        }
    }

    private void HookScrollViewer()
    {
        _scroll = this.FindControl<ScrollViewer>("CardScrollViewer");
        if (_scroll == null)
            return;

        _scroll.ScrollChanged += OnScrollChanged;
        _scroll.SizeChanged += OnScrollSizeChanged;
    }

    private void UnhookScrollViewer()
    {
        if (_scroll != null)
        {
            _scroll.ScrollChanged -= OnScrollChanged;
            _scroll.SizeChanged -= OnScrollSizeChanged;
        }

        _scroll = null;
    }

    private void HookItemsRepeater()
    {
        _repeater = this.FindControl<ItemsRepeater>("CardRepeater");
        if (_repeater == null)
            return;

        _repeater.SizeChanged += OnRepeaterSizeChanged;
    }

    private void UnhookItemsRepeater()
    {
        if (_repeater != null)
        {
            _repeater.SizeChanged -= OnRepeaterSizeChanged;
        }

        _repeater = null;
    }

    private async void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroll == null)
            return;

        if (DataContext is LoraHelperViewModel vm)
        {
            if (_scroll.Offset.Y + _scroll.Viewport.Height > _scroll.Extent.Height - 300)
            {
                await vm.LoadNextPageAsync();
            }

            UpdateActivePreviewRange();
        }
    }

    private void OnScrollSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateActivePreviewRange();

    private void OnRepeaterSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateActivePreviewRange();

    private void UpdateActivePreviewRange()
    {
        if (_scroll == null || _repeater == null || DataContext is not LoraHelperViewModel vm)
            return;

        if (_repeater.Layout is not UniformGridLayout layout)
        {
            vm.SetActiveVideoRange(0);
            return;
        }

        var itemHeight = GetItemSize(layout.MinItemHeight, DefaultItemHeight);
        var itemWidth = GetItemSize(layout.MinItemWidth, DefaultItemWidth);

        var viewportWidth = GetViewportWidth();
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            vm.SetActiveVideoRange(0);
            return;
        }

        var columns = Math.Max(1, (int)Math.Floor(viewportWidth / itemWidth));
        var rowsScrolled = Math.Max(0, (int)Math.Floor(_scroll.Offset.Y / itemHeight));
        var startIndex = rowsScrolled * columns;

        vm.SetActiveVideoRange(startIndex);
    }

    private static double GetItemSize(double layoutValue, double fallback) =>
        (layoutValue > 0 ? layoutValue : fallback) + ItemMargin;

    private double GetViewportWidth()
    {
        if (_scroll == null)
            return double.NaN;

        var viewportWidth = _scroll.Viewport.Width;
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = _scroll.Bounds.Width;
        }

        return viewportWidth;
    }

    private async void OnCardPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;

        if (sender is not Border border)
            return;

        if (e.Source is Button || e.Source is ToggleButton)
            return;

        if (border.DataContext is not LoraCardViewModel card)
            return;

        if (card.OpenDetailsCommand is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(null);
        }
        else
        {
            card.OpenDetailsCommand.Execute(null);
        }

        e.Handled = true;
    }
}
