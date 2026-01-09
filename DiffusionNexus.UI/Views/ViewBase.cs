using Avalonia;
using Avalonia.Controls;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// Base class for all feature views that own their ViewModel.
/// Provides automatic ViewModel creation, typed access, and service injection.
/// </summary>
/// <typeparam name="TViewModel">The ViewModel type. Must have a parameterless constructor.</typeparam>
/// <remarks>
/// <para>
/// <b>Key features:</b>
/// <list type="bullet">
///   <item><description>Automatically creates ViewModel instance in constructor</description></item>
///   <item><description>Provides typed <see cref="ViewModel"/> property</description></item>
///   <item><description>Auto-injects DialogService if ViewModel implements IDialogServiceAware</description></item>
/// </list>
/// </para>
/// <para>
/// <b>When to use:</b> For module/feature views that are navigation targets.
/// For reusable controls that receive DataContext from parent, use <see cref="Controls.ControlBase"/> instead.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Code-behind
/// public partial class LoraSortView : ViewBase&lt;LoraSortViewModel&gt;
/// {
///     public LoraSortView()
///     {
///         InitializeComponent();
///     }
/// }
/// </code>
/// </example>
public abstract class ViewBase<TViewModel> : UserControl where TViewModel : ViewModelBase, new()
{
    /// <summary>
    /// Gets the typed ViewModel for this view.
    /// </summary>
    protected TViewModel ViewModel => (TViewModel)DataContext!;

    /// <summary>
    /// Initializes a new instance of the view, creating the ViewModel automatically.
    /// </summary>
    protected ViewBase()
    {
        DataContext = new TViewModel();
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    /// <summary>
    /// Called when the view is attached to the visual tree.
    /// Override to perform additional initialization after the view is attached.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">Event arguments.</param>
    protected virtual void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        InjectServices();
    }

    /// <summary>
    /// Injects required services (like DialogService) into the ViewModel.
    /// Override to inject additional services.
    /// </summary>
    protected virtual void InjectServices()
    {
        if (VisualRoot is Window window && ViewModel is IDialogServiceAware aware)
        {
            aware.DialogService = new DialogService(window);
        }
    }

    /// <summary>
    /// Gets the parent window if the view is attached to one.
    /// </summary>
    protected Window? ParentWindow => VisualRoot as Window;
}
