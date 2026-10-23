using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Behaviors;

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

        // The tile grid sits inside a TabControl. With focus on the "Installed" tab header, Home/End
        // would switch tabs before the grid ever saw the key — so the view forwards the scroll keys
        // on the tunnel pass while the grid is on screen.
        if (this.FindControl<ScrollViewer>("InstalledTileScrollViewer") is { } tiles)
            ScrollKeyNavigation.ForwardKeys(this, tiles);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
