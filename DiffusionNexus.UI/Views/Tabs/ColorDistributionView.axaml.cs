using Avalonia.Controls;

namespace DiffusionNexus.UI.Views.Tabs;

/// <summary>
/// View for the Color Distribution analysis detail section.
/// Displays color consistency issues: grayscale mixing, color-cast, palette outliers.
/// </summary>
public partial class ColorDistributionView : UserControl
{
    public ColorDistributionView()
    {
        InitializeComponent();
    }
}
