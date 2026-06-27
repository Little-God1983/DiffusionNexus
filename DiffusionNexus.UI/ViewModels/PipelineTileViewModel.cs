namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Represents a single pipeline tile shown in the <see cref="PipelinesViewModel"/> gallery.
/// </summary>
public class PipelineTileViewModel : ViewModelBase
{
    /// <summary>
    /// Stable identifier used to dispatch the tile when it is launched.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Display title shown on the tile (e.g. "Anime-To-Real").
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Short description rendered under the title and as the tile tooltip.
    /// </summary>
    public string Description { get; }

    public PipelineTileViewModel(string id, string title, string description)
    {
        Id = id;
        Title = title;
        Description = description;
    }

    /// <summary>
    /// Parameterless constructor for the XAML design-time <c>Design.DataContext</c>.
    /// </summary>
    public PipelineTileViewModel()
        : this("anime-to-real", "Anime-To-Real", "Convert anime-style images into photorealistic renders.")
    {
    }
}
