namespace DiffusionNexus.UI.Models.Pipelines;

/// <summary>Where a pipeline writes its generated images.</summary>
public enum PipelineOutputMode
{
    /// <summary>Create a new version of the selected dataset and write outputs there.</summary>
    NewDatasetVersion,

    /// <summary>Write outputs to a folder the user picks.</summary>
    PickFolder,

    /// <summary>Write each output next to its source image (loose-images input).</summary>
    InputFolderInPlace,
}

/// <summary>A selectable output destination shown in the run UI.</summary>
/// <param name="Mode">The destination kind.</param>
/// <param name="DisplayName">Label shown to the user.</param>
public sealed record PipelineOutputOption(PipelineOutputMode Mode, string DisplayName);
