using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Inference.Captioning;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Row in the Diffusion Nexus Core captioning dialog. One entry per
/// <see cref="CaptioningModelType"/>, with the live on-disk status of its
/// GGUF/mmproj pair.
/// </summary>
public sealed class CaptioningModelRowViewModel(
    string name,
    string description,
    string filePath,
    CaptioningModelStatus status)
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string FilePath { get; } = filePath;
    public CaptioningModelStatus Status { get; } = status;

    public string StatusLabel => Status switch
    {
        CaptioningModelStatus.Ready => "Ready",
        CaptioningModelStatus.Loaded => "Loaded",
        CaptioningModelStatus.Downloading => "Downloading…",
        CaptioningModelStatus.Corrupted => "Corrupted",
        _ => "Not present"
    };

    /// <summary>
    /// Colour palette matches WorkloadItemViewModel so the dialog feels like a
    /// natural sibling of the ComfyUI Workloads dialog.
    /// </summary>
    public string StatusColor => Status switch
    {
        CaptioningModelStatus.Ready or CaptioningModelStatus.Loaded => "#4CAF50",
        CaptioningModelStatus.Downloading => "#FFC107",
        CaptioningModelStatus.Corrupted => "#F44336",
        _ => "#999999"
    };
}

/// <summary>
/// ViewModel for the Diffusion Nexus Core captioning models dialog. Mirrors
/// the shape of <see cref="WorkloadsViewModel"/> so the view templates stay
/// consistent: a tabular list of items + a footer of contextual info.
/// </summary>
public partial class CaptioningModelsDialogViewModel : ViewModelBase
{
    /// <summary>
    /// The rows displayed in the DataGrid — one per captioning model.
    /// </summary>
    public ObservableCollection<CaptioningModelRowViewModel> Models { get; } = [];

    /// <summary>
    /// Every directory the manager will scan when resolving model files.
    /// Shown below the grid so users can see exactly where their GGUFs are
    /// being looked up — important for diagnosing "why isn't my file showing".
    /// </summary>
    public ObservableCollection<string> SearchPaths { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    public CaptioningModelsDialogViewModel(CaptioningModelManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        Load(manager);
    }

    private void Load(CaptioningModelManager manager)
    {
        try
        {
            IsLoading = true;
            Models.Clear();
            SearchPaths.Clear();

            foreach (var modelType in Enum.GetValues<CaptioningModelType>())
            {
                var info = manager.GetModelInfo(modelType);
                Models.Add(new CaptioningModelRowViewModel(
                    info.DisplayName,
                    info.Description,
                    info.FilePath,
                    info.Status));
            }

            foreach (var path in manager.SearchPaths)
            {
                SearchPaths.Add(path);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }
}
