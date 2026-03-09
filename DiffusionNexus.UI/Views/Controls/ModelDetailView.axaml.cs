using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Detail panel control that displays model information, versions, trigger words, and tags.
/// </summary>
public partial class ModelDetailView : UserControl
{
    public ModelDetailView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
