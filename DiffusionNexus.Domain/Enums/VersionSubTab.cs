namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// Represents the top-level sub-tabs available within a dataset version detail view.
/// Training input (images + captions) is separated from training output (runs).
/// </summary>
public enum VersionSubTab
{
    /// <summary>
    /// Training data tab - images, videos, and captions for training.
    /// This is the original/default view.
    /// </summary>
    [Obsolete("Use TrainingData instead.")]
    Training = 0,

    /// <summary>
    /// Training data tab - images, videos, and captions for training input.
    /// </summary>
    TrainingData = 0,

    /// <summary>
    /// Training runs tab - groups of training outputs (Epochs, Notes, Presentation per run).
    /// </summary>
    TrainingRuns = 1
}
