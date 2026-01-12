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
/// Allows users to select what content to copy to the new version,
/// with filtering by content type and rating status.
/// </summary>
public partial class CreateVersionDialogViewModel : ObservableObject
{
    private readonly IReadOnlyList<int> _availableVersions;
    private readonly List<DatasetImageViewModel> _allMediaFiles;

    private VersionSourceOption _sourceOption = VersionSourceOption.StartFresh;
    private int _selectedSourceVersion;
    private bool _copyImages = true;
    private bool _copyVideos = true;
    private bool _copyCaptions = true;
    private bool _copyRatings = true;
    
    // Rating filter options - default: only Production Ready is selected
    private bool _includeProductionReady = true;
    private bool _includeUnrated;
    private bool _includeTrash;

    /// <summary>
    /// Creates a new CreateVersionDialogViewModel.
    /// </summary>
    /// <param name="currentVersion">The current version number (used as default source version).</param>
    /// <param name="availableVersions">All available source versions to copy from.</param>
    /// <param name="mediaFiles">All media files in the current version (for rating counts).</param>
    public CreateVersionDialogViewModel(
        int currentVersion,
        IReadOnlyList<int> availableVersions,
        IEnumerable<DatasetImageViewModel> mediaFiles)
    {
        _availableVersions = availableVersions;
        _selectedSourceVersion = currentVersion;
        _allMediaFiles = mediaFiles.ToList();
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public CreateVersionDialogViewModel() : this(1, [1], [])
    {
    }

    /// <summary>
    /// All available versions to copy from.
    /// </summary>
    public IReadOnlyList<int> AvailableVersions => _availableVersions;

    #region Content Type Counts

    /// <summary>
    /// Number of images in the current version.
    /// </summary>
    public int ImageCount => _allMediaFiles.Count(m => m.IsImage);

    /// <summary>
    /// Number of videos in the current version.
    /// </summary>
    public int VideoCount => _allMediaFiles.Count(m => m.IsVideo);

    /// <summary>
    /// Number of captions in the current version.
    /// </summary>
    public int CaptionCount => _allMediaFiles.Count(m => File.Exists(m.CaptionFilePath));

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

    #endregion

    #region Rating Counts

    /// <summary>
    /// Number of media files marked as production ready (approved).
    /// </summary>
    public int ProductionReadyCount => _allMediaFiles.Count(m => m.IsApproved);

    /// <summary>
    /// Number of media files that are unrated.
    /// </summary>
    public int UnratedCount => _allMediaFiles.Count(m => m.IsUnrated);

    /// <summary>
    /// Number of media files marked as trash (rejected).
    /// </summary>
    public int TrashCount => _allMediaFiles.Count(m => m.IsRejected);

    /// <summary>
    /// Number of media files that will be copied based on current rating selections.
    /// </summary>
    public int FilteredMediaCount
    {
        get
        {
            var count = 0;
            if (_includeProductionReady)
                count += ProductionReadyCount;
            if (_includeUnrated)
                count += UnratedCount;
            if (_includeTrash)
                count += TrashCount;
            return count;
        }
    }

    /// <summary>
    /// Whether at least one rating category is selected for copying.
    /// </summary>
    public bool HasRatingSelection => _includeProductionReady || _includeUnrated || _includeTrash;

    /// <summary>
    /// Whether there are any rated media files (non-unrated).
    /// </summary>
    public bool HasRatedMedia => ProductionReadyCount > 0 || TrashCount > 0;

    #endregion

    #region Source Option Properties

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

    #endregion

    #region Content Type Selection

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
    /// Whether to copy ratings (production ready/trash status) to the new version.
    /// Default: true.
    /// </summary>
    public bool CopyRatings
    {
        get => _copyRatings;
        set => SetProperty(ref _copyRatings, value);
    }

    #endregion

    #region Rating Selection

    /// <summary>
    /// Whether to include production ready (approved) media.
    /// Default: true.
    /// </summary>
    public bool IncludeProductionReady
    {
        get => _includeProductionReady;
        set
        {
            if (SetProperty(ref _includeProductionReady, value))
            {
                OnPropertyChanged(nameof(FilteredMediaCount));
                OnPropertyChanged(nameof(HasRatingSelection));
            }
        }
    }

    /// <summary>
    /// Whether to include unrated media.
    /// Default: false.
    /// </summary>
    public bool IncludeUnrated
    {
        get => _includeUnrated;
        set
        {
            if (SetProperty(ref _includeUnrated, value))
            {
                OnPropertyChanged(nameof(FilteredMediaCount));
                OnPropertyChanged(nameof(HasRatingSelection));
            }
        }
    }

    /// <summary>
    /// Whether to include trash (rejected) media.
    /// Default: false.
    /// </summary>
    public bool IncludeTrash
    {
        get => _includeTrash;
        set
        {
            if (SetProperty(ref _includeTrash, value))
            {
                OnPropertyChanged(nameof(FilteredMediaCount));
                OnPropertyChanged(nameof(HasRatingSelection));
            }
        }
    }

    #endregion

    #region Display Text

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

    #endregion
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
    /// Whether to copy ratings (production ready/trash status) to the new version.
    /// </summary>
    public bool CopyRatings { get; init; }

    /// <summary>
    /// Whether to include production ready (approved) media.
    /// </summary>
    public bool IncludeProductionReady { get; init; }

    /// <summary>
    /// Whether to include unrated media.
    /// </summary>
    public bool IncludeUnrated { get; init; }

    /// <summary>
    /// Whether to include trash (rejected) media.
    /// </summary>
    public bool IncludeTrash { get; init; }

    /// <summary>
    /// Creates a cancelled result.
    /// </summary>
    public static CreateVersionResult Cancelled() => new() { Confirmed = false };
}
