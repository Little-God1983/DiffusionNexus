namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Represents a version item for the Image Edit tab version dropdown.
/// Displays in format: "V1 | 45 Images"
/// </summary>
public class EditorVersionItem
{
    /// <summary>
    /// The version number (1-based).
    /// </summary>
    public int Version { get; init; }

    /// <summary>
    /// Number of images in this version.
    /// </summary>
    public int ImageCount { get; init; }

    /// <summary>
    /// Display text for the dropdown.
    /// Format: "V1 | 45 Images" or "V1 | 1 Image"
    /// </summary>
    public string DisplayText => ImageCount == 1
        ? $"V{Version} | 1 Image"
        : $"V{Version} | {ImageCount} Images";

    /// <summary>
    /// Creates a new EditorVersionItem.
    /// </summary>
    /// <param name="version">Version number.</param>
    /// <param name="imageCount">Number of images in this version.</param>
    public static EditorVersionItem Create(int version, int imageCount)
    {
        return new EditorVersionItem
        {
            Version = version,
            ImageCount = imageCount
        };
    }
}
