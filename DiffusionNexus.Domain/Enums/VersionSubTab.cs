namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// Represents the sub-tabs available within a dataset version detail view.
/// Each version folder can contain training data, epochs, notes, and presentation files.
/// </summary>
public enum VersionSubTab
{
    /// <summary>
    /// Training data tab - images, videos, and captions for training.
    /// This is the original/default view.
    /// </summary>
    Training = 0,

    /// <summary>
    /// Epochs tab - trained model checkpoint files (.safetensors, .pt, .pth, .gguf).
    /// </summary>
    Epochs = 1,

    /// <summary>
    /// Notes tab - text journal entries for training parameters, remarks, and documentation.
    /// </summary>
    Notes = 2,

    /// <summary>
    /// Presentation data tab - reserved for future use (e.g., showcase images, demos).
    /// </summary>
    Presentation = 3
}
