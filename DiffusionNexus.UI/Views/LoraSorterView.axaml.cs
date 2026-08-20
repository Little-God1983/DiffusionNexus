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

    /// <summary>
    /// The <b>only</b> initialization trigger, deliberately. This view is inline XAML inside a
    /// <c>TabItem</c>, and Avalonia resolves <c>DataContext="{Binding SorterViewModel}"</c> through
    /// the logical tree as soon as the parent view's DataContext is set — regardless of which tab
    /// is selected. Initializing from <c>OnDataContextChanged</c> as well therefore ran a full disk
    /// walk, a SHA256 of every unknown file and by-hash API requests on <i>every</i> LoRA Viewer
    /// open, even when the user never touched the Sorter tab. A non-selected TabItem's content is
    /// not attached to the <i>visual</i> tree until it is selected, so attaching is the event that
    /// actually means "the user is looking at the sorter". <see cref="ControlBase"/>'s service
    /// injection still runs on both events — it is inherited and untouched.
    /// </summary>
    protected override void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(sender, e);
        TryInitializeSorter();
    }

    private void TryInitializeSorter()
    {
        if (_initialized || DataContext is not LoraSorterViewModel vm) return;
        _initialized = true;
        _ = vm.InitializeAsync();
    }
}
