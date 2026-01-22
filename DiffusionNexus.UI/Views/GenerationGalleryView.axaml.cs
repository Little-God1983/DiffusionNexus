using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using DiffusionNexus.UI.ViewModels;
using System.Linq;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// Generation gallery mosaic gallery view.
/// </summary>
public partial class GenerationGalleryView : UserControl
{
    public GenerationGalleryView()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
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

        var props = e.GetCurrentPoint(border).Properties;
        if (!props.IsLeftButtonPressed) return;

        if (e.Source is Control control)
        {
            if (control is Button || control.GetVisualAncestors().OfType<Button>().Any())
            {
                return;
            }
        }

        var isCtrlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var isShiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        vm.SelectWithModifiers(item, isShiftPressed, isCtrlPressed);
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not GenerationGalleryViewModel vm) return;

        if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.SelectAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.HasSelection)
        {
            vm.ClearSelectionCommand.Execute(null);
            e.Handled = true;
        }
    }
}
