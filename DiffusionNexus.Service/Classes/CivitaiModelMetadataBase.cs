using System.Collections.Generic;

namespace DiffusionNexus.Service.Classes;

/// <summary>
/// Common properties shared between metadata models sourced from Civitai.
/// </summary>
public abstract class CivitaiModelMetadataBase
{
    private string modelVersionName = string.Empty;
    private string? baseModel;
    private List<string> trainedWords = new();

    /// <summary>
    /// Display name of the model version.
    /// </summary>
    public virtual string ModelVersionName
    {
        get => modelVersionName;
        set => modelVersionName = value ?? string.Empty;
    }

    /// <summary>
    /// Base diffusion model that the asset targets.
    /// </summary>
    public virtual string? BaseModel
    {
        get => baseModel;
        set => baseModel = value;
    }

    /// <summary>
    /// Tags supplied by the author to describe how the model was trained.
    /// </summary>
    public IReadOnlyList<string> TrainedWords => trainedWords;

    /// <summary>
    /// Optional long form description of the model version.
    /// </summary>
    public virtual string? Description { get; set; }

    /// <summary>
    /// Provides derived classes with mutable access to the trained word list.
    /// </summary>
    protected List<string> MutableTrainedWords
    {
        get => trainedWords;
        set => trainedWords = value ?? new List<string>();
    }
}
