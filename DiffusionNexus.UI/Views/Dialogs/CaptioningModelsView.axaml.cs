using Avalonia.Controls;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Reusable captioning-models list (DataGrid + search paths) bound to a
/// <see cref="ViewModels.CaptioningModelsDialogViewModel"/>. Hosted in the
/// Diffusion Nexus Core workloads dialog's "Captioning Models" tab.
/// </summary>
public partial class CaptioningModelsView : UserControl
{
    public CaptioningModelsView()
    {
        InitializeComponent();
    }
}
