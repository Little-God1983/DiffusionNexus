namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// Represents the type of content in a dataset.
/// These are hardcoded types that determine the primary content type of a training dataset.
/// </summary>
public enum DatasetType
{
    /// <summary>
    /// Dataset primarily contains images for training.
    /// </summary>
    Image = 0,

    /// <summary>
    /// Dataset primarily contains video files for training.
    /// </summary>
    Video = 1,

    /// <summary>
    /// Dataset contains instruction/prompt pairs for fine-tuning.
    /// </summary>
    Instruction = 2
}

/// <summary>
/// Extension methods for <see cref="DatasetType"/>.
/// </summary>
public static class DatasetTypeExtensions
{
    /// <summary>
    /// Gets a user-friendly display name for the dataset type.
    /// </summary>
    public static string GetDisplayName(this DatasetType type) => type switch
    {
        DatasetType.Image => "Image",
        DatasetType.Video => "Video",
        DatasetType.Instruction => "Instruction",
        _ => type.ToString()
    };

    /// <summary>
    /// Gets all available dataset types.
    /// </summary>
    public static IReadOnlyList<DatasetType> GetAll() =>
        [DatasetType.Image, DatasetType.Video, DatasetType.Instruction];
}
