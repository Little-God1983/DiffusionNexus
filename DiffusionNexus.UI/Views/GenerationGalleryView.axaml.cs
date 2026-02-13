using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// Generation gallery mosaic gallery view.
/// </summary>
public partial class GenerationGalleryView : UserControl
{
    private bool _isInitialized;
    private ScrollViewer? _galleryScrollViewer;

    public GenerationGalleryView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (!_isInitialized)
        {
            _isInitialized = true;

            if (DataContext is GenerationGalleryViewModel vm)
            {
                var window = this.VisualRoot as Window ?? TopLevel.GetTopLevel(this) as Window;
                if (window is not null)
                {
                    vm.DialogService = new DialogService(window);
                }
            }

            _galleryScrollViewer = this.FindControl<ScrollViewer>("GalleryScrollViewer");
            if (_galleryScrollViewer is not null)
            {
                _galleryScrollViewer.ScrollChanged += OnGalleryScrollChanged;
            }
        }
    }

    private void OnGalleryScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_galleryScrollViewer is null || DataContext is not GenerationGalleryViewModel vm)
            return;

        if (!vm.HasMoreItems)
            return;

        // Load more when within 200px of the bottom
        var distanceToBottom = _galleryScrollViewer.Extent.Height
                             - _galleryScrollViewer.Viewport.Height
                             - _galleryScrollViewer.Offset.Y;

        if (distanceToBottom < 200)
        {
            vm.LoadMoreItems();
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnMediaCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.DataContext is not GenerationGalleryMediaItemViewModel item) return;
        if (DataContext is not GenerationGalleryViewModel vm) return;

        if (e.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null)
        {
            return;
        }

        var props = e.GetCurrentPoint(border).Properties;
        if (!props.IsLeftButtonPressed) return;

        var isCtrlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var isShiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        vm.SelectWithModifiers(item, isShiftPressed, isCtrlPressed);
        e.Handled = true;
    }

    private void OnMediaDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border) return;

        if (e.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null)
        {
            return;
        }

        var parent = border.Parent;
        while (parent is not null)
        {
            if (parent.DataContext is GenerationGalleryMediaItemViewModel item)
            {
                if (DataContext is GenerationGalleryViewModel vm)
                {
                    vm.OpenImageViewerCommand.Execute(item);
                }
                e.Handled = true;
                return;
            }
            parent = parent.Parent as Control;
        }
    }
}
