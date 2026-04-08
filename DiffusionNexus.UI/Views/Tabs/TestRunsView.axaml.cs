using Avalonia.Controls;

namespace DiffusionNexus.UI.Views.Tabs;

/// <summary>
/// View for the Test Runs sub-tab within Dataset Quality.
/// Displays a history of "Analyze All" runs with scores and issue summaries.
/// </summary>
public partial class TestRunsView : UserControl
{
    public TestRunsView()
    {
        InitializeComponent();
    }
}
