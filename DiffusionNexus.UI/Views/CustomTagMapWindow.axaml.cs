using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

public partial class CustomTagMapWindow : Window
{
    public CustomTagMapWindow()
    {
        InitializeComponent();
        this.AttachedToVisualTree += OnAttached;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is CustomTagMapWindowViewModel vm)
            vm.SetWindow(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
