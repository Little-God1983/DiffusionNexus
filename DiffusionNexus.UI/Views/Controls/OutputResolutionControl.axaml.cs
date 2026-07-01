using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Reusable output-resolution picker: aspect-ratio buttons (source ratio or a common one), an orientation
/// toggle, and a megapixel slider. Bind its <see cref="Control.DataContext"/> to an
/// <see cref="ViewModels.Controls.OutputResolutionViewModel"/> and call that VM's
/// <c>ComputeDimensions</c> to get the output size for a source.
/// </summary>
public partial class OutputResolutionControl : UserControl
{
    public OutputResolutionControl() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
