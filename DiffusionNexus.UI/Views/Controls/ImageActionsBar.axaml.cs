using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Reusable "Add Selected To… / Send Selected To…" toolbar. Bind its <c>DataContext</c> to an
/// <see cref="ViewModels.Controls.ImageActionsViewModel"/>; toggle destinations via that VM's
/// <c>Show*</c> flags (e.g. the Image Editor hides "Image Editor").
/// </summary>
public partial class ImageActionsBar : UserControl
{
    public ImageActionsBar() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
