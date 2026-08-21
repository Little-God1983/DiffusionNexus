using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
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

    /// <summary>
    /// The module host attaches the LoRA Viewer to the visual tree <i>before</i> it assigns the
    /// ViewModel, so when this view's attach event fires, <c>DataContext</c> is still null and
    /// <see cref="TryInitializeSorter"/> has nothing to initialize. Without this hook the sorter
    /// stayed empty until something re-attached the module (e.g. opening Settings and coming back).
    /// Same two-event pattern <see cref="ControlBase.TryInjectServices"/> uses; the
    /// attached-to-visual-tree gate keeps the lazy "only when the tab is shown" semantics.
    /// </summary>
    protected override void OnDataContextChanged(object? sender, EventArgs e)
    {
        base.OnDataContextChanged(sender, e);
        TryInitializeSorter();
    }

    private void TryInitializeSorter()
    {
        if (_initialized || !this.IsAttachedToVisualTree() || DataContext is not LoraSorterViewModel vm) return;
        _initialized = true;
        _ = vm.InitializeAsync();
    }
}
