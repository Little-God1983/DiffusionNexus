using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// View for browsing and managing LoRA models. The tile grid is a virtualizing
/// <c>ItemsRepeater</c> (<c>UniformGridLayout</c>) inside the ScrollViewer — see
/// LoraViewerView.axaml — so only the tiles inside the viewport are realized and
/// each tile loads/releases its thumbnail as its container recycles. No manual
/// scroll-window management lives here anymore.
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
