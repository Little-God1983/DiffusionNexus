using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Classes;
using System.Net.WebSockets;

namespace DiffusionNexus.UI.Views;

public partial class LoraHelperView : UserControl
{
    private ScrollViewer? _scroll;

    public LoraHelperView()
    {
        InitializeComponent();
        this.AttachedToVisualTree += OnAttached;
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
        }
    }
}
