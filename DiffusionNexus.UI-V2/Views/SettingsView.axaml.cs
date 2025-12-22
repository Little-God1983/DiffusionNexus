using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// Settings view for configuring application settings.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
