using Avalonia.Controls;
using Avalonia.Input;
using DiffusionNexus.UI.ViewModels.Tabs;

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

    private void IssueRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: ExpandableIssueViewModel vm } && vm.HasFiles)
        {
            vm.ToggleExpandedCommand.Execute(null);
        }
    }
}
