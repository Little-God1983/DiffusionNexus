namespace DiffusionNexus.UI.Services;

public sealed record DatasetExportItem(
    string ImagePath,
    string FileName,
    string? CaptionPath,
    string? CaptionFileName);
