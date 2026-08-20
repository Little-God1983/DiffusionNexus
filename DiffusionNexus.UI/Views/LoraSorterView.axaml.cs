using Avalonia;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Views.Controls;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// View for the "LoRA Sorter" sub-tab. Receives its <see cref="LoraSorterViewModel"/> from the
/// parent <c>LoraViewerView</c> tab binding, so this derives from <see cref="ControlBase"/> (not
/// <c>ViewBase&lt;TViewModel&gt;</c>, which would construct its own VM) to get the same
/// attach/DataContext-changed <c>IDialogServiceAware</c> injection every other DataContext-from-parent
/// control in this app relies on — the sorter's Browse/Start-sorting commands need a live
/// <see cref="DiffusionNexus.UI.Services.IDialogService"/> or they silently no-op.
/// </summary>
public partial class LoraSorterView : ControlBase
{
    private bool _initialized;

    public LoraSorterView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(sender, e);
        TryInitializeSorter();
    }

    protected override void OnDataContextChanged(object? sender, EventArgs e)
    {
        base.OnDataContextChanged(sender, e);
        TryInitializeSorter();
    }

    private void TryInitializeSorter()
    {
        if (_initialized || DataContext is not LoraSorterViewModel vm) return;
        _initialized = true;
        _ = vm.InitializeAsync();
    }
}
