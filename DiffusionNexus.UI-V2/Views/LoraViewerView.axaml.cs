using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// View for browsing and managing LoRA models.
/// </summary>
public partial class LoraViewerView : UserControl
{
    public LoraViewerView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
