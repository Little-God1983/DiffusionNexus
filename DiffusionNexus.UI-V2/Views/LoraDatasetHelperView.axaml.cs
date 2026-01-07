using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// Main shell view for the LoRA Dataset Helper module.
/// Acts as a container for tab views and manages DialogService injection.
/// 
/// <para>
/// <b>Responsibilities:</b>
/// <list type="bullet">
/// <item>Hosting the TabControl with child tab views</item>
/// <item>Injecting DialogService into the ViewModel hierarchy</item>
/// </list>
/// </para>
/// 
/// <para>
/// <b>Architecture:</b>
/// This view follows the Shell pattern - it contains minimal logic and delegates
/// all tab-specific UI and behavior to the child views:
/// <list type="bullet">
/// <item><see cref="Tabs.DatasetManagementView"/> - Dataset listing, drag-drop, selection</item>
/// <item><see cref="Tabs.ImageEditView"/> - Image editor with canvas wiring</item>
/// </list>
/// </para>
/// </summary>
public partial class LoraDatasetHelperView : UserControl
{
    private bool _isInitialized;

    public LoraDatasetHelperView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_isInitialized) return;
        _isInitialized = true;

        // Inject DialogService into the ViewModel and forward to children
        if (VisualRoot is Window window && DataContext is LoraDatasetHelperViewModel vm)
        {
            vm.DialogService = new DialogService(window);
            vm.OnDialogServiceSet();
            
            // Load datasets after DialogService is set up
            vm.DatasetManagement.CheckStorageConfigurationCommand.Execute(null);
        }
    }
}
