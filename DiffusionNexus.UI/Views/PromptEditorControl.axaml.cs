using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Classes;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

public partial class PromptEditorControl : UserControl
{

    public PromptEditorControl()
    {
        InitializeComponent();
        this.AttachedToVisualTree += OnAttached;

    }
    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // DataContext must already be your PromptEditViewModel
        if (DataContext is PromptEditorControlViewModel vm)
        {
            // VisualRoot is the Window or other top-level
            if (VisualRoot is Window window)
            {
                vm.DialogService = new DialogService(window);
            }
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}