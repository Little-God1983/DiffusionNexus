using Avalonia.Controls;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Reusable panel that displays the readiness status for a single feature, regardless of
/// which backend (ComfyUI, local inference) answers the check. Set the
/// <see cref="Control.DataContext"/> to a <see cref="ViewModels.FeatureReadinessViewModel"/>
/// instance to use.
/// </summary>
public partial class FeatureReadinessPanel : UserControl
{
    public FeatureReadinessPanel()
    {
        InitializeComponent();
    }
}
