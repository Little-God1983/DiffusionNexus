namespace DiffusionNexus.UI.Services;

/// <summary>
/// Represents a dataset item to be exported, including the image file and optional caption file.
/// </summary>
/// <param name="ImagePath">The full path to the image file to export.</param>
/// <param name="FileName">The desired file name for the exported image.</param>
/// <param name="CaptionPath">The full path to the caption file, or <see langword="null"/> if no caption exists.</param>
/// <param name="CaptionFileName">The desired file name for the exported caption, or <see langword="null"/> to use the caption file's original name.</param>
public sealed record DatasetExportItem(
    string ImagePath,
    string FileName,
    string? CaptionPath,
    string? CaptionFileName);
