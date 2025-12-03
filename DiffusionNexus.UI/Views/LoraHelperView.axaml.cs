using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Classes;
using System;
using System.Threading;

namespace DiffusionNexus.UI.Views;

public partial class LoraHelperView : UserControl
{
    private ScrollViewer? _scroll;
    private ItemsRepeater? _repeater;
    private CancellationTokenSource? _scrollDebounceTokenSource;
    private const int ScrollDebounceMilliseconds = 100;

    public LoraHelperView()
    {
        InitializeComponent();
        this.AttachedToVisualTree += OnAttached;
        this.DetachedFromVisualTree += OnDetached;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnSuggestionChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ViewModels.LoraHelperViewModel vm && sender is ComboBox cb && cb.SelectedItem is string text)
        {
            vm.ApplySuggestion(text);
            cb.IsDropDownOpen = false;
            cb.SelectedIndex = -1;
        }
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is LoraHelperViewModel vm && VisualRoot is Window window)
        {
            vm.DialogService = new DialogService(window);
            vm.SetWindow(window);
        }

        _scroll = this.FindControl<ScrollViewer>("CardScrollViewer");
        if (_scroll != null)
            _scroll.ScrollChanged += OnScrollChanged;

        _repeater = this.FindControl<ItemsRepeater>("CardRepeater");

        UpdateActivePreviewRange();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_scroll != null)
        {
            _scroll.ScrollChanged -= OnScrollChanged;
        }

        _scroll = null;
        _repeater = null;
    }

    private async void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroll == null)
            return;

        if (DataContext is not LoraHelperViewModel vm)
            return;

        // Debounce scroll events
        _scrollDebounceTokenSource?.Cancel();
        _scrollDebounceTokenSource = new CancellationTokenSource();
        var token = _scrollDebounceTokenSource.Token;

        try
        {
            await System.Threading.Tasks.Task.Delay(ScrollDebounceMilliseconds, token);

            if (token.IsCancellationRequested)
                return;

            // Load next page if near bottom
            if (_scroll.Offset.Y + _scroll.Viewport.Height > _scroll.Extent.Height - 300)
            {
                await vm.LoadNextPageAsync();
            }

            // Update active video preview range
            UpdateActivePreviewRange();
        }
        catch (OperationCanceledException)
        {
            // Debounced
        }
    }

    private void UpdateActivePreviewRange()
    {
        if (_scroll == null || _repeater == null || DataContext is not LoraHelperViewModel vm)
            return;

        if (_repeater.Layout is not UniformGridLayout layout)
        {
            vm.SetActiveVideoRange(0);
            return;
        }

        var itemHeight = (layout.MinItemHeight > 0 ? layout.MinItemHeight : 300) + 10;
        var itemWidth = (layout.MinItemWidth > 0 ? layout.MinItemWidth : 250) + 10;

        var viewportWidth = _scroll.Viewport.Width;
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = _scroll.Bounds.Width;
        }

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
