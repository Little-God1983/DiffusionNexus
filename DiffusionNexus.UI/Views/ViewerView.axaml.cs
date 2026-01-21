using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// Viewer mosaic gallery view.
/// </summary>
public partial class ViewerView : UserControl
{
    public ViewerView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
