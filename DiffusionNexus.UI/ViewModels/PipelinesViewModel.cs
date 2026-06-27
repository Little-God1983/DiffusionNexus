using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Pipelines module — a tile gallery of guided image
/// pipelines. Currently exposes a single tile (Anime-To-Real); additional
/// pipelines are added to <see cref="Pipelines"/> as they are implemented.
/// </summary>
public partial class PipelinesViewModel : ViewModelBase
{
    private static readonly ILogger Logger = Log.ForContext<PipelinesViewModel>();

    /// <summary>
    /// The pipeline tiles displayed in the gallery.
    /// </summary>
    public ObservableCollection<PipelineTileViewModel> Pipelines { get; } = new();

    public PipelinesViewModel()
    {
        Pipelines.Add(new PipelineTileViewModel(
            id: "anime-to-real",
            title: "Anime-To-Real",
            description: "Convert anime-style images into photorealistic renders."));
    }

    /// <summary>
    /// Launches the selected pipeline tile. The concrete pipelines are not yet
    /// implemented, so for now this records the selection; tile-specific
    /// navigation is wired up here as each pipeline lands.
    /// </summary>
    [RelayCommand]
    private void OpenPipeline(PipelineTileViewModel? tile)
    {
        if (tile is null)
            return;

        Logger.Information("Pipeline tile selected: {PipelineId} ({PipelineTitle})", tile.Id, tile.Title);

        // TODO: dispatch to the concrete pipeline once implemented, e.g.:
        //   switch (tile.Id) { case "anime-to-real": ... }
    }
}
