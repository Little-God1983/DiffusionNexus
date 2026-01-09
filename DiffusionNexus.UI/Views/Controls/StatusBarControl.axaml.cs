using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Status bar control showing current status, active operations, and toggle for activity log panel.
/// </summary>
public partial class StatusBarControl : UserControl
{
    public StatusBarControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
