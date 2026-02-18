namespace DiffusionNexus.UI.Services;

using DiffusionNexus.UI.ViewModels;

/// <summary>
/// Destination for the Save As operation.
/// </summary>
public enum SaveAsDestination
{
    OriginFolder,
    ExistingDataset,
    LayeredTiff
}

/// <summary>
/// Result from the Save As dialog containing the new filename and rating.
/// </summary>
public sealed record SaveAsResult
{
    /// <summary>
    /// Gets whether the dialog was cancelled.
    /// </summary>
    public bool IsCancelled { get; init; }

    /// <summary>
    /// Gets the destination for the saved image.
    /// </summary>
    public SaveAsDestination Destination { get; init; } = SaveAsDestination.OriginFolder;

    /// <summary>
    /// Gets the new filename (without path or extension).
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// Gets the rating to apply to the saved image.
    /// </summary>
    public ImageRatingStatus Rating { get; init; } = ImageRatingStatus.Unrated;

    /// <summary>
    /// Gets the selected dataset when saving to a dataset.
    /// </summary>
    public DatasetCardViewModel? SelectedDataset { get; init; }

    /// <summary>
    /// Gets the selected version when saving to a dataset.
    /// </summary>
    public int? SelectedVersion { get; init; }

    /// <summary>
    /// Gets the folder path for layered TIFF export.
    /// </summary>
    public string? CustomFolderPath { get; init; }

    /// <summary>
    /// Creates a cancelled result.
    /// </summary>
    public static SaveAsResult Cancelled() => new() { IsCancelled = true };

    /// <summary>
    /// Creates a successful result for saving to the origin folder.
    /// </summary>
    public static SaveAsResult Success(string fileName, ImageRatingStatus rating) =>
        new()
        {
            Destination = SaveAsDestination.OriginFolder,
            FileName = fileName,
            Rating = rating
        };

    /// <summary>
    /// Creates a successful result for saving to a dataset.
    /// </summary>
    public static SaveAsResult SuccessToDataset(string fileName, ImageRatingStatus rating, DatasetCardViewModel dataset, int? version) =>
        new()
        {
            Destination = SaveAsDestination.ExistingDataset,
            FileName = fileName,
            Rating = rating,
            SelectedDataset = dataset,
            SelectedVersion = version
        };

    /// <summary>
    /// Creates a successful result for saving as a layered TIFF.
    /// </summary>
    public static SaveAsResult SuccessLayeredTiff(string fileName, string folderPath) =>
        new()
        {
            Destination = SaveAsDestination.LayeredTiff,
            FileName = fileName,
            CustomFolderPath = folderPath
        };
}
