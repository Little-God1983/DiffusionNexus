using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// Generation gallery mosaic gallery view.
/// </summary>
public partial class GenerationGalleryView : UserControl
{
    public GenerationGalleryView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
