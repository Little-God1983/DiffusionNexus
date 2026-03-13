namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// Represents the sub-tabs available within a training run detail view.
/// Each training run contains epochs, notes, and presentation data.
/// </summary>
public enum TrainingRunSubTab
{
    /// <summary>
    /// Epochs tab - trained model checkpoint files (.safetensors, .pt, .pth, .gguf).
    /// </summary>
    Epochs = 0,

    /// <summary>
    /// Notes tab - text journal entries for training parameters, remarks, and documentation.
    /// </summary>
    Notes = 1,

    /// <summary>
    /// Presentation data tab - showcase images, demos, and documents.
    /// </summary>
    Presentation = 2
}
