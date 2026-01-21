using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel representing a single media item in the Viewer gallery.
/// </summary>
public class ViewerMediaItemViewModel : ObservableObject
{
    public ViewerMediaItemViewModel(string filePath, bool isVideo, DateTime createdAtUtc)
    {
        FilePath = filePath;
        IsVideo = isVideo;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Full path to the media file.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// True when the media item is a video file.
    /// </summary>
    public bool IsVideo { get; }

    /// <summary>
    /// True when the media item is an image file.
    /// </summary>
    public bool IsImage => !IsVideo;

    /// <summary>
    /// The file name without extension.
    /// </summary>
    public string FileName => Path.GetFileNameWithoutExtension(FilePath);

    /// <summary>
    /// File extension (lowercase).
    /// </summary>
    public string FileExtension => Path.GetExtension(FilePath).ToLowerInvariant();

    /// <summary>
    /// Creation timestamp for sorting.
    /// </summary>
    public DateTime CreatedAtUtc { get; }
}
