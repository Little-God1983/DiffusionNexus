using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;

namespace DiffusionNexus.UI.Views.CivitaiBrowser;

public partial class CivitaiBrowserView : UserControl
{
    public CivitaiBrowserView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Pointer-pressed handler on each result card. Mirrors the Generation Gallery
    /// behavior: Shift+LMB extends the selection from the last clicked card to the
    /// current one; Ctrl+LMB toggles the current card; plain LMB clears and selects
    /// only the current card. Clicks on inner buttons/checkboxes are ignored so they
    /// keep their own behavior.
    /// </summary>
    private void OnResultCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control) return;
        if (control.DataContext is not CivitaiResultViewModel item) return;
        if (DataContext is not CivitaiBrowserViewModel vm) return;

        // Let nested interactive controls handle the click themselves (checkbox on the
        // card, version-picker button + its flyout, etc.).
        if (e.Source is Visual visual)
        {
            if (visual.FindAncestorOfType<Button>() is not null) return;
            if (visual.FindAncestorOfType<CheckBox>() is not null) return;
        }

        var props = e.GetCurrentPoint(control).Properties;
        if (!props.IsLeftButtonPressed) return;

        var isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        vm.SelectWithModifiers(item, isShift, isCtrl);
        e.Handled = true;
    }
}
