using Avalonia.Controls;

namespace DiffusionNexus.UI.Views.Tabs;

/// <summary>
/// View for the Training Runs tab within the dataset version detail view.
/// Displays a list of training run cards and a detail view with Epochs/Notes/Presentation sub-tabs.
/// </summary>
public partial class TrainingRunsTabView : UserControl
{
    public TrainingRunsTabView()
    {
        InitializeComponent();
    }
}
