using Avalonia.Controls;

namespace DiffusionNexus.UI.Views.Tabs;

/// <summary>
/// View for the Image Quality analysis detail section.
/// Displays per-image blur and exposure scores in a sortable table.
/// </summary>
public partial class ImageQualityView : UserControl
{
    public ImageQualityView()
    {
        InitializeComponent();
    }
}
