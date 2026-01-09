using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Activity log panel showing application events and active operations.
/// </summary>
public partial class ActivityLogPanel : UserControl
{
    public ActivityLogPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
