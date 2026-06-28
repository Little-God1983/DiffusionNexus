using Avalonia.Controls;

namespace DiffusionNexus.UI.Views.Pipelines;

/// <summary>
/// Shared view for a pipeline "run" screen. Bound to a <see cref="ViewModels.Pipelines.PipelineRunViewModel"/>
/// (base-typed); pipeline-specific controls are supplied via in-XAML DataTemplates keyed on the
/// concrete run ViewModel.
/// </summary>
public partial class PipelineRunView : UserControl
{
    public PipelineRunView()
    {
        InitializeComponent();
    }
}
