using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Views.Controls;

namespace DiffusionNexus.UI.Views.Tabs;

/// <summary>
/// View for the Color Distribution analysis detail section.
/// Displays color consistency issues: grayscale mixing, color-cast, palette outliers.
/// </summary>
public partial class ColorDistributionView : ControlBase
{
    public ColorDistributionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
