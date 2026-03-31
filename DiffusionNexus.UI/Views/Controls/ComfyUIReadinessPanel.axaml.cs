using Avalonia.Controls;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Reusable panel that displays the ComfyUI readiness status for a single feature.
/// Set the <see cref="Control.DataContext"/> to a <see cref="ViewModels.ComfyUIReadinessViewModel"/>
/// instance to use.
/// </summary>
public partial class ComfyUIReadinessPanel : UserControl
{
    public ComfyUIReadinessPanel()
    {
        InitializeComponent();
    }
}
