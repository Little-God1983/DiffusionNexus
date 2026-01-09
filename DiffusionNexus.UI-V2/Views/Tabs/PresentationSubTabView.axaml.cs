using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiffusionNexus.UI.Views.Tabs;

/// <summary>
/// View for the Presentation sub-tab within dataset version detail view.
/// Currently an empty placeholder reserved for future use.
/// </summary>
public partial class PresentationSubTabView : UserControl
{
    public PresentationSubTabView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
