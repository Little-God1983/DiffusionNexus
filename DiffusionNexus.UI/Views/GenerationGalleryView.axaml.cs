using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// Generation gallery mosaic gallery view.
/// </summary>
public partial class GenerationGalleryView : UserControl
{
    public GenerationGalleryView()
    {
        InitializeComponent();
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

        if (e.Source is IVisual visual && visual.FindAncestorOfType<Button>() is not null)
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
}
