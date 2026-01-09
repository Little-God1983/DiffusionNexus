using Avalonia;
using Avalonia.Controls;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Base class for reusable controls that receive DataContext from their parent.
/// Provides automatic service injection and access to parent window context.
/// </summary>
/// <remarks>
/// <para>
/// <b>Key differences from ViewBase:</b>
/// <list type="bullet">
///   <item><description>Does NOT create its own ViewModel</description></item>
///   <item><description>Receives DataContext from parent view</description></item>
///   <item><description>Re-injects services when DataContext changes</description></item>
/// </list>
/// </para>
/// <para>
/// <b>When to use:</b> For reusable UI components embedded within feature views
/// (e.g., settings panels, custom input controls, overlay controls).
/// For feature views, use <see cref="ViewBase{TViewModel}"/> instead.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public partial class ProcessingOverlayControl : ControlBase
/// {
///     public ProcessingOverlayControl()
///     {
///         InitializeComponent();
///     }
/// }
/// </code>
/// </example>
public abstract class ControlBase : UserControl
{
    /// <summary>
    /// Initializes a new instance and sets up event handlers for service injection.
    /// </summary>
    protected ControlBase()
    {
        AttachedToVisualTree += OnAttachedToVisualTree;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Called when the control is attached to the visual tree.
    /// Override to perform additional initialization.
    /// </summary>
    protected virtual void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        TryInjectServices();
    }

    /// <summary>
    /// Called when the DataContext changes.
    /// Override to respond to DataContext changes.
    /// </summary>
    protected virtual void OnDataContextChanged(object? sender, EventArgs e)
    {
        TryInjectServices();
    }

    /// <summary>
    /// Attempts to inject services (like DialogService) into the ViewModel if it supports them.
    /// Called automatically when attached to visual tree or when DataContext changes.
    /// </summary>
    protected virtual void TryInjectServices()
    {
        if (DataContext is IDialogServiceAware aware && VisualRoot is Window window)
        {
            aware.DialogService = new DialogService(window);
        }
    }

    /// <summary>
    /// Gets the parent window if the control is attached to one.
    /// </summary>
    protected Window? ParentWindow => VisualRoot as Window;

    /// <summary>
    /// Gets the main window's ViewModel if available.
    /// Useful for accessing global application state or triggering navigation.
    /// </summary>
    protected DiffusionNexusMainWindowViewModel? MainWindowViewModel =>
        ParentWindow?.DataContext as DiffusionNexusMainWindowViewModel;
}
