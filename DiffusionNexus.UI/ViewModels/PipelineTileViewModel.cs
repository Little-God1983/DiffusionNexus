using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Models.Pipelines;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>On-disk readiness of a pipeline's assets, surfaced as a tile badge.</summary>
public enum PipelineStatus
{
    /// <summary>Readiness has not been determined yet.</summary>
    Unknown,

    /// <summary>One or more required assets are missing.</summary>
    NotInstalled,

    /// <summary>Assets are currently downloading.</summary>
    Downloading,

    /// <summary>All required assets are present.</summary>
    Ready,

    /// <summary>The last check or install failed.</summary>
    Error,
}

/// <summary>
/// A single pipeline tile in the <see cref="PipelinesViewModel"/> gallery. Carries the
/// underlying <see cref="PipelineManifest"/> plus the live readiness badge state.
/// </summary>
public partial class PipelineTileViewModel : ViewModelBase
{
    /// <summary>The manifest describing this pipeline and its required assets.</summary>
    public PipelineManifest Manifest { get; }

    public string Id => Manifest.Id;
    public string Title => Manifest.Title;
    public string Description => Manifest.Description;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    private PipelineStatus _status = PipelineStatus.Unknown;

    [ObservableProperty]
    private string _statusText = "Checking…";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Badge colour derived from <see cref="Status"/>.</summary>
    public IBrush StatusBrush => Status switch
    {
        PipelineStatus.Ready => new SolidColorBrush(Color.Parse("#2E7D32")),
        PipelineStatus.Downloading => new SolidColorBrush(Color.Parse("#1565C0")),
        PipelineStatus.NotInstalled => new SolidColorBrush(Color.Parse("#5A5A5A")),
        PipelineStatus.Error => new SolidColorBrush(Color.Parse("#C62828")),
        _ => new SolidColorBrush(Color.Parse("#424242")),
    };

    public PipelineTileViewModel(PipelineManifest manifest)
    {
        Manifest = manifest;
    }

    /// <summary>Parameterless constructor for the XAML design-time <c>Design.DataContext</c>.</summary>
    public PipelineTileViewModel()
        : this(new PipelineManifest
        {
            Id = "anime-to-real",
            Title = "Anime-To-Real",
            Description = "Convert anime-style images into photorealistic renders.",
        })
    {
    }
}
