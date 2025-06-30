using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Controls;

public partial class ProcessingOverlayControl : UserControl
{
    public ProcessingOverlayControl()
    {
        InitializeComponent();
        this.AttachedToVisualTree += (_, _) => DataContext ??= (Parent as Control)?.DataContext;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
