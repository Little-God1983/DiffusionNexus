using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Defines how the new version should be initialized.
/// </summary>
public enum VersionSourceOption
{
    /// <summary>
    /// Start with an empty version.
    /// </summary>
    StartFresh,

    /// <summary>
    /// Copy selected content types from a source version.
    /// </summary>
    CopyFromVersion
}

/// <summary>
/// ViewModel for the Create Version dialog.
/// Allows users to select what content to copy to the new version.
/// </summary>
public partial class CreateVersionDialogViewModel : ObservableObject
{
    private readonly IReadOnlyList<int> _availableVersions;

    private VersionSourceOption _sourceOption = VersionSourceOption.StartFresh;
    private int _selectedSourceVersion;
    private bool _copyImages = true;
    private bool _copyVideos = true;
    private bool _copyCaptions = true;

    /// <summary>
    /// Creates a new CreateVersionDialogViewModel.
    /// </summary>
    /// <param name="currentVersion">The current version number (used as default source version).</param>
    /// <param name="availableVersions">All available source versions to copy from.</param>
    /// <param name="imageCount">Number of images in current version.</param>
    /// <param name="videoCount">Number of videos in current version.</param>
    /// <param name="captionCount">Number of captions in current version.</param>
    public CreateVersionDialogViewModel(
        int currentVersion,
        IReadOnlyList<int> availableVersions,
        int imageCount,
        int videoCount,
        int captionCount)
    {
        _availableVersions = availableVersions;
        _selectedSourceVersion = currentVersion;
        ImageCount = imageCount;
        VideoCount = videoCount;
        CaptionCount = captionCount;
    }

    /// <summary>
    /// All available versions to copy from.
    /// </summary>
    public IReadOnlyList<int> AvailableVersions => _availableVersions;

    /// <summary>
    /// Number of images in the current version.
    /// </summary>
    public int ImageCount { get; }

    /// <summary>
    /// Number of videos in the current version.
    /// </summary>
    public int VideoCount { get; }

    /// <summary>
    /// Number of captions in the current version.
    /// </summary>
    public int CaptionCount { get; }

    /// <summary>
    /// Whether there are any images available to copy.
    /// </summary>
    public bool HasImages => ImageCount > 0;

    /// <summary>
    /// Whether there are any videos available to copy.
    /// </summary>
    public bool HasVideos => VideoCount > 0;

    /// <summary>
    /// Whether there are any captions available to copy.
    /// </summary>
    public bool HasCaptions => CaptionCount > 0;

    /// <summary>
    /// Whether there is any content available to copy.
    /// </summary>
    public bool HasAnyContent => HasImages || HasVideos || HasCaptions;

    /// <summary>
    /// The source option for the new version.
    /// </summary>
    public VersionSourceOption SourceOption
    {
        get => _sourceOption;
        set
        {
            if (SetProperty(ref _sourceOption, value))
            {
                OnPropertyChanged(nameof(IsCopyFromVersion));
                OnPropertyChanged(nameof(IsStartFresh));
            }
        }
    }

    /// <summary>
    /// Whether the "Start Fresh" option is selected.
    /// </summary>
    public bool IsStartFresh
    {
        get => _sourceOption == VersionSourceOption.StartFresh;
        set
        {
            if (value)
            {
                SourceOption = VersionSourceOption.StartFresh;
            }
        }
    }

    /// <summary>
    /// Whether the "Copy from Version" option is selected.
    /// </summary>
    public bool IsCopyFromVersion
    {
        get => _sourceOption == VersionSourceOption.CopyFromVersion;
        set
        {
            if (value)
            {
                SourceOption = VersionSourceOption.CopyFromVersion;
            }
        }
    }

    /// <summary>
    /// The source version to copy from.
    /// </summary>
    public int SelectedSourceVersion
    {
        get => _selectedSourceVersion;
        set => SetProperty(ref _selectedSourceVersion, value);
    }

    /// <summary>
    /// Whether to copy images to the new version.
    /// </summary>
    public bool CopyImages
    {
        get => _copyImages;
        set => SetProperty(ref _copyImages, value);
    }

    /// <summary>
    /// Whether to copy videos to the new version.
    /// </summary>
    public bool CopyVideos
    {
        get => _copyVideos;
        set => SetProperty(ref _copyVideos, value);
    }

    /// <summary>
    /// Whether to copy captions to the new version.
    /// </summary>
    public bool CopyCaptions
    {
        get => _copyCaptions;
        set => SetProperty(ref _copyCaptions, value);
    }

    /// <summary>
    /// Summary text for what content will be copied.
    /// </summary>
    public string ContentSummary
    {
        get
        {
            var parts = new List<string>();
            if (ImageCount > 0)
                parts.Add($"{ImageCount} image{(ImageCount == 1 ? "" : "s")}");
            if (VideoCount > 0)
                parts.Add($"{VideoCount} video{(VideoCount == 1 ? "" : "s")}");
            if (CaptionCount > 0)
                parts.Add($"{CaptionCount} caption{(CaptionCount == 1 ? "" : "s")}");

            return parts.Count > 0 ? string.Join(", ", parts) : "No content";
        }
    }
}

/// <summary>
/// Result returned from the Create Version dialog.
/// </summary>
public sealed record CreateVersionResult
{
    /// <summary>
    /// Whether the user confirmed the dialog.
    /// </summary>
    public bool Confirmed { get; init; }

    /// <summary>
    /// The source option selected by the user.
    /// </summary>
    public VersionSourceOption SourceOption { get; init; }

    /// <summary>
    /// The source version to copy from (if CopyFromVersion).
    /// </summary>
    public int SourceVersion { get; init; }

    /// <summary>
    /// Whether to copy images.
    /// </summary>
    public bool CopyImages { get; init; }

    /// <summary>
    /// Whether to copy videos.
    /// </summary>
    public bool CopyVideos { get; init; }

    /// <summary>
    /// Whether to copy captions.
    /// </summary>
    public bool CopyCaptions { get; init; }

    /// <summary>
    /// Creates a cancelled result.
    /// </summary>
    public static CreateVersionResult Cancelled() => new() { Confirmed = false };
}
