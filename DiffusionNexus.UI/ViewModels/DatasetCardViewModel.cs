using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Converters;
using DiffusionNexus.UI.Utilities;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel representing a single dataset folder as a card.
/// Displays folder name, media count, and thumbnail preview.
/// 
/// Folder structure:
/// - Legacy: DatasetName/images+captions (flat structure)
/// - Versioned: DatasetName/.dataset/config.json + DatasetName/V1/images+captions
/// </summary>
public class DatasetCardViewModel : ObservableObject
{
    private string _name = string.Empty;
    private string _folderPath = string.Empty;
    private string? _currentVersionDescription;
    private int _imageCount;
    private int _videoCount;
    private int _captionCount;
    private int _totalImageCountAllVersions;
    private int _totalVideoCountAllVersions;
    private int _totalCaptionCountAllVersions;
    private string? _thumbnailPath;
    private bool _isSelected;
    private int? _categoryId;
    private int? _categoryOrder;
    private string? _categoryName;
    private DatasetType? _type;
    private int _currentVersion = 1;
    private int _totalVersions = 1;
    private bool _isVersionedStructure;
    private int? _displayVersion;
    private Dictionary<int, int> _versionBranchedFrom = new();
    private Dictionary<int, string?> _versionDescriptions = new();
    private Dictionary<int, bool> _versionNsfwFlags = new();
    private bool _isNsfw;
    private Bitmap? _thumbnail;
    private bool _isThumbnailLoading;

    /// <summary>
    /// Name of the dataset (folder name).
    /// </summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>
    /// Full path to the dataset folder (root).
    /// </summary>
    public string FolderPath
    {
        get => _folderPath;
        set => SetProperty(ref _folderPath, value);
    }

    /// <summary>
    /// Description for the current version.
    /// Getting/setting this property updates the description for the current version.
    /// </summary>
    public string? Description
    {
        get => _currentVersionDescription;
        set
        {
            if (SetProperty(ref _currentVersionDescription, value))
            {
                // Also update the version descriptions dictionary
                _versionDescriptions[_currentVersion] = value;
                OnPropertyChanged(nameof(HasDescription));
            }
        }
    }

    /// <summary>
    /// Whether the current version has a description.
    /// </summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(_currentVersionDescription);

    /// <summary>
    /// Dictionary of descriptions for each version.
    /// </summary>
    public Dictionary<int, string?> VersionDescriptions
    {
        get => _versionDescriptions;
        set => SetProperty(ref _versionDescriptions, value);
    }

    /// <summary>
    /// Dictionary of NSFW flags for each version.
    /// Key is version number, value is whether that version is NSFW.
    /// </summary>
    public Dictionary<int, bool> VersionNsfwFlags
    {
        get => _versionNsfwFlags;
        set => SetProperty(ref _versionNsfwFlags, value);
    }

    /// <summary>
    /// Whether the current version is marked as NSFW.
    /// Getting/setting this property updates the NSFW flag for the current version.
    /// </summary>
    public bool IsNsfw
    {
        get => _isNsfw;
        set
        {
            if (SetProperty(ref _isNsfw, value))
            {
                // Also update the version NSFW flags dictionary
                _versionNsfwFlags[_currentVersion] = value;
                OnPropertyChanged(nameof(HasAnyNsfwVersion));
                OnPropertyChanged(nameof(AreAllVersionsNsfw));
            }
        }
    }

    /// <summary>
    /// Whether any version of this dataset is marked as NSFW.
    /// Used for filtering in flattened view.
    /// </summary>
    public bool HasAnyNsfwVersion => _versionNsfwFlags.Count > 0 && _versionNsfwFlags.Values.Any(v => v);

    /// <summary>
    /// Whether ALL versions of this dataset are marked as NSFW.
    /// Used for filtering in collapsed (non-flattened) view - only hide if all versions are NSFW.
    /// </summary>
    public bool AreAllVersionsNsfw
    {
        get
        {
            // If no versions have NSFW flags set, not all are NSFW
            if (_versionNsfwFlags.Count == 0)
                return false;

            // Get all version numbers that exist
            var allVersions = GetAllVersionNumbers();
            
            // Check if every existing version is marked as NSFW
            foreach (var version in allVersions)
            {
                if (!_versionNsfwFlags.TryGetValue(version, out var isNsfw) || !isNsfw)
                    return false;
            }
            
            return true;
        }
    }

    /// <summary>
    /// Number of images in the current version.
    /// </summary>
    public int ImageCount
    {
        get => _imageCount;
        set
        {
            if (SetProperty(ref _imageCount, value))
            {
                OnPropertyChanged(nameof(ImageCountText));
                OnPropertyChanged(nameof(MediaCountText));
                OnPropertyChanged(nameof(TotalMediaCount));
                OnPropertyChanged(nameof(CanIncrementVersion));
                OnPropertyChanged(nameof(DetailedCountText));
            }
        }
    }

    /// <summary>
    /// Number of videos in the current version.
    /// </summary>
    public int VideoCount
    {
        get => _videoCount;
        set
        {
            if (SetProperty(ref _videoCount, value))
            {
                OnPropertyChanged(nameof(VideoCountText));
                OnPropertyChanged(nameof(MediaCountText));
                OnPropertyChanged(nameof(TotalMediaCount));
                OnPropertyChanged(nameof(HasVideos));
                OnPropertyChanged(nameof(CanIncrementVersion));
                OnPropertyChanged(nameof(DetailedCountText));
            }
        }
    }

    /// <summary>
    /// Number of captions in the current version.
    /// </summary>
    public int CaptionCount
    {
        get => _captionCount;
        set
        {
            if (SetProperty(ref _captionCount, value))
            {
                OnPropertyChanged(nameof(CaptionCountText));
                OnPropertyChanged(nameof(DetailedCountText));
            }
        }
    }

    /// <summary>
    /// Total number of media files (images + videos).
    /// </summary>
    public int TotalMediaCount => _imageCount + _videoCount;

    /// <summary>
    /// Total number of images across all versions.
    /// Used when displaying collapsed (non-flattened) view.
    /// </summary>
    public int TotalImageCountAllVersions
    {
        get => _totalImageCountAllVersions;
        set
        {
            if (SetProperty(ref _totalImageCountAllVersions, value))
            {
                OnPropertyChanged(nameof(TotalImageCountAllVersionsText));
                OnPropertyChanged(nameof(AllVersionsDetailedCountText));
            }
        }
    }

    /// <summary>
    /// Total number of videos across all versions.
    /// Used when displaying collapsed (non-flattened) view.
    /// </summary>
    public int TotalVideoCountAllVersions
    {
        get => _totalVideoCountAllVersions;
        set
        {
            if (SetProperty(ref _totalVideoCountAllVersions, value))
            {
                OnPropertyChanged(nameof(TotalVideoCountAllVersionsText));
                OnPropertyChanged(nameof(AllVersionsDetailedCountText));
            }
        }
    }

    /// <summary>
    /// Total number of captions across all versions.
    /// Used when displaying collapsed (non-flattened) view.
    /// </summary>
    public int TotalCaptionCountAllVersions
    {
        get => _totalCaptionCountAllVersions;
        set
        {
            if (SetProperty(ref _totalCaptionCountAllVersions, value))
            {
                OnPropertyChanged(nameof(TotalCaptionCountAllVersionsText));
                OnPropertyChanged(nameof(AllVersionsDetailedCountText));
            }
        }
    }

    /// <summary>
    /// Whether this dataset contains video files.
    /// </summary>
    public bool HasVideos => _videoCount > 0;

    /// <summary>
    /// Path to the first image for thumbnail preview.
    /// </summary>
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set
        {
            if (SetProperty(ref _thumbnailPath, value))
            {
                OnPropertyChanged(nameof(HasThumbnail));
                // Reset thumbnail when path changes
                _thumbnail = null;
                _isThumbnailLoading = false;
                OnPropertyChanged(nameof(Thumbnail));
            }
        }
    }

    /// <summary>
    /// The loaded thumbnail bitmap. Loads asynchronously on first access.
    /// Bind to this property for efficient async thumbnail display.
    /// </summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (_thumbnail is not null)
                return _thumbnail;

            // Try to get from cache synchronously
            var path = ThumbnailPath;
            if (string.IsNullOrEmpty(path))
                return null;

            var thumbnailService = PathToBitmapConverter.ThumbnailService;
            if (thumbnailService?.TryGetCached(path, out var cached) == true)
            {
                _thumbnail = cached;
                return _thumbnail;
            }

            // Start async load if not already loading
            if (!_isThumbnailLoading)
            {
                _isThumbnailLoading = true;
                _ = LoadThumbnailAsync(path);
            }

            return null;
        }
    }

    /// <summary>
    /// Loads the thumbnail asynchronously and notifies when ready.
    /// </summary>
    private async Task LoadThumbnailAsync(string path)
    {
        var thumbnailService = PathToBitmapConverter.ThumbnailService;
        if (thumbnailService is null)
        {
            _isThumbnailLoading = false;
            return;
        }

        try
        {
            var bitmap = await thumbnailService.LoadThumbnailAsync(path);
            
            if (bitmap is not null)
            {
                // Update on UI thread - check if dispatcher is available
                if (Dispatcher.UIThread.CheckAccess())
                {
                    // Already on UI thread
                    _thumbnail = bitmap;
                    _isThumbnailLoading = false;
                    OnPropertyChanged(nameof(Thumbnail));
                }
                else
                {
                    // Post to UI thread, don't wait
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            _thumbnail = bitmap;
                            _isThumbnailLoading = false;
                            OnPropertyChanged(nameof(Thumbnail));
                        }
                        catch (InvalidOperationException)
                        {
                            // Control might be disposed, ignore
                            _isThumbnailLoading = false;
                        }
                    });
                }
            }
            else
            {
                _isThumbnailLoading = false;
            }
        }
        catch
        {
            _isThumbnailLoading = false;
        }
    }

    /// <summary>
    /// Whether this card is currently selected.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Category ID for this dataset (resolved from database at runtime).
    /// </summary>
    public int? CategoryId
    {
        get => _categoryId;
        set
        {
            if (SetProperty(ref _categoryId, value))
            {
                OnPropertyChanged(nameof(HasCategory));
                CategoryChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Category order for this dataset (stable across database recreations).
    /// This is what gets persisted to config.json.
    /// </summary>
    public int? CategoryOrder
    {
        get => _categoryOrder;
        set => SetProperty(ref _categoryOrder, value);
    }

    /// <summary>
    /// Category name for display.
    /// </summary>
    public string? CategoryName
    {
        get => _categoryName;
        set => SetProperty(ref _categoryName, value);
    }

    /// <summary>
    /// The type of content in this dataset (Image, Video, Instruction).
    /// </summary>
    public DatasetType? Type
    {
        get => _type;
        set
        {
            if (SetProperty(ref _type, value))
            {
                OnPropertyChanged(nameof(HasType));
                OnPropertyChanged(nameof(TypeDisplayName));
            }
        }
    }

    /// <summary>
    /// Whether this dataset has a type assigned.
    /// </summary>
    public bool HasType => _type.HasValue;

    /// <summary>
    /// Display name for the dataset type.
    /// </summary>
    public string? TypeDisplayName => _type?.GetDisplayName();

    /// <summary>
    /// Current active version number.
    /// </summary>
    public int CurrentVersion
    {
        get => _currentVersion;
        set
        {
            if (SetProperty(ref _currentVersion, value))
            {
                OnPropertyChanged(nameof(VersionDisplayText));
                OnPropertyChanged(nameof(CurrentVersionFolderPath));
                OnPropertyChanged(nameof(VersionBadgeText));
                
                // Update the current version description
                _currentVersionDescription = _versionDescriptions.GetValueOrDefault(value);
                OnPropertyChanged(nameof(Description));
                OnPropertyChanged(nameof(HasDescription));
                
                // Update the current version NSFW flag
                _isNsfw = _versionNsfwFlags.GetValueOrDefault(value, false);
                OnPropertyChanged(nameof(IsNsfw));
            }
        }
    }

    /// <summary>
    /// Total number of versions available.
    /// </summary>
    public int TotalVersions
    {
        get => _totalVersions;
        set
        {
            if (SetProperty(ref _totalVersions, value))
            {
                OnPropertyChanged(nameof(VersionDisplayText));
                OnPropertyChanged(nameof(HasMultipleVersions));
                OnPropertyChanged(nameof(VersionBadgeText));
                OnPropertyChanged(nameof(ShowVersionBadge));
            }
        }
    }

    /// <summary>
    /// Whether this dataset uses the versioned folder structure.
    /// </summary>
    public bool IsVersionedStructure
    {
        get => _isVersionedStructure;
        set => SetProperty(ref _isVersionedStructure, value);
    }

    /// <summary>
    /// When set, this card represents a specific version in flattened view.
    /// When null, this is a normal dataset card (collapsed view).
    /// </summary>
    public int? DisplayVersion
    {
        get => _displayVersion;
        set
        {
            if (SetProperty(ref _displayVersion, value))
            {
                OnPropertyChanged(nameof(IsVersionCard));
                OnPropertyChanged(nameof(VersionBadgeText));
                OnPropertyChanged(nameof(ShowVersionBadge));
            }
        }
    }

    /// <summary>
    /// Tracks which version each version was branched from.
    /// </summary>
    public Dictionary<int, int> VersionBranchedFrom
    {
        get => _versionBranchedFrom;
        set => SetProperty(ref _versionBranchedFrom, value);
    }

    /// <summary>
    /// Whether this card represents a specific version (flattened view).
    /// </summary>
    public bool IsVersionCard => _displayVersion.HasValue;

    /// <summary>
    /// Whether this dataset has a category assigned.
    /// </summary>
    public bool HasCategory => _categoryId.HasValue;

    /// <summary>
    /// Display text showing image count.
    /// </summary>
    public string ImageCountText => _imageCount == 1 ? "1 image" : $"{_imageCount} images";

    /// <summary>
    /// Display text showing video count.
    /// </summary>
    public string VideoCountText => _videoCount == 1 ? "1 video" : $"{_videoCount} videos";

    /// <summary>
    /// Display text showing caption count.
    /// </summary>
    public string CaptionCountText => _captionCount == 1 ? "1 caption" : $"{_captionCount} captions";

    /// <summary>
    /// Display text showing combined media count (images + videos).
    /// </summary>
    public string MediaCountText
    {
        get
        {
            if (_videoCount == 0)
                return ImageCountText;
            if (_imageCount == 0)
                return VideoCountText;
            return $"{_imageCount} images, {_videoCount} videos";
        }
    }

    /// <summary>
    /// Display text showing total images across all versions.
    /// </summary>
    public string TotalImageCountAllVersionsText => _totalImageCountAllVersions == 1 ? "1 image" : $"{_totalImageCountAllVersions} images";

    /// <summary>
    /// Display text showing total videos across all versions.
    /// </summary>
    public string TotalVideoCountAllVersionsText => _totalVideoCountAllVersions == 1 ? "1 video" : $"{_totalVideoCountAllVersions} videos";

    /// <summary>
    /// Display text showing total captions across all versions.
    /// </summary>
    public string TotalCaptionCountAllVersionsText => _totalCaptionCountAllVersions == 1 ? "1 caption" : $"{_totalCaptionCountAllVersions} captions";

    /// <summary>
    /// Detailed count text showing images, videos, and captions for the current version.
    /// Format: "X Images; X Videos; X Captions" (omits zero counts).
    /// </summary>
    public string DetailedCountText
    {
        get
        {
            var parts = new List<string>();
            if (_imageCount > 0) parts.Add($"{_imageCount} {(_imageCount == 1 ? "Image" : "Images")}");
            if (_videoCount > 0) parts.Add($"{_videoCount} {(_videoCount == 1 ? "Video" : "Videos")}");
            if (_captionCount > 0) parts.Add($"{_captionCount} {(_captionCount == 1 ? "Caption" : "Captions")}");
            return parts.Count > 0 ? string.Join("; ", parts) : "Empty";
        }
    }

    /// <summary>
    /// Detailed count text showing total images, videos, and captions across all versions.
    /// Format: "X Images; X Videos; X Captions" (omits zero counts).
    /// Used in collapsed (non-flattened) view.
    /// </summary>
    public string AllVersionsDetailedCountText
    {
        get
        {
            var parts = new List<string>();
            if (_totalImageCountAllVersions > 0) parts.Add($"{_totalImageCountAllVersions} {(_totalImageCountAllVersions == 1 ? "Image" : "Images")}");
            if (_totalVideoCountAllVersions > 0) parts.Add($"{_totalVideoCountAllVersions} {(_totalVideoCountAllVersions == 1 ? "Video" : "Videos")}");
            if (_totalCaptionCountAllVersions > 0) parts.Add($"{_totalCaptionCountAllVersions} {(_totalCaptionCountAllVersions == 1 ? "Caption" : "Captions")}");
            return parts.Count > 0 ? string.Join("; ", parts) : "Empty";
        }
    }

    /// <summary>
    /// Display text for version (e.g., "V1" or "V2 of 3").
    /// </summary>
    public string VersionDisplayText => _totalVersions > 1 
        ? $"V{_currentVersion} of {_totalVersions}" 
        : $"V{_currentVersion}";

    /// <summary>
    /// Badge text for card display.
    /// In flattened view: shows "V1", "V2", etc.
    /// In collapsed view: shows "3 Versions" for multi-version datasets.
    /// </summary>
    public string VersionBadgeText => _displayVersion.HasValue 
        ? $"V{_displayVersion}" 
        : (_totalVersions > 1 ? $"{_totalVersions} Versions" : string.Empty);

    /// <summary>
    /// Whether to show the version badge on the card.
    /// </summary>
    public bool ShowVersionBadge => _displayVersion.HasValue || _totalVersions > 1;

    /// <summary>
    /// Whether there are multiple versions.
    /// </summary>
    public bool HasMultipleVersions => _totalVersions > 1;

    /// <summary>
    /// Whether this dataset has media files and can have its version incremented.
    /// </summary>
    public bool CanIncrementVersion => TotalMediaCount > 0;

    /// <summary>
    /// Whether this dataset has a thumbnail to display.
    /// </summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(_thumbnailPath);

    /// <summary>
    /// Path to the .dataset config folder.
    /// </summary>
    public string ConfigFolderPath => Path.Combine(_folderPath, ".dataset");

    /// <summary>
    /// Path to the metadata file for this dataset.
    /// For versioned: DatasetName/.dataset/config.json
    /// For legacy: DatasetName/.dataset.json (will be migrated)
    /// </summary>
    public string MetadataFilePath => Path.Combine(ConfigFolderPath, "config.json");

    /// <summary>
    /// Legacy metadata file path (for migration).
    /// </summary>
    public string LegacyMetadataFilePath => Path.Combine(_folderPath, ".dataset.json");

    /// <summary>
    /// Path to the current version's image folder.
    /// For versioned: DatasetName/V1, DatasetName/V2, etc.
    /// For legacy (no versions yet): DatasetName/ (root folder)
    /// </summary>
    public string CurrentVersionFolderPath => _isVersionedStructure 
        ? Path.Combine(_folderPath, $"V{_currentVersion}") 
        : _folderPath;

    /// <summary>
    /// Path to the Epochs subfolder within the current version folder.
    /// Used to store trained model checkpoints (.safetensors, .pt, .pth, .gguf).
    /// </summary>
    public string EpochsFolderPath => Path.Combine(CurrentVersionFolderPath, "Epochs");

    /// <summary>
    /// Path to the Notes subfolder within the current version folder.
    /// Used to store journal/text notes about training.
    /// </summary>
    public string NotesFolderPath => Path.Combine(CurrentVersionFolderPath, "Notes");

    /// <summary>
    /// Path to the Presentation subfolder within the current version folder.
    /// Reserved for future use (showcase images, demos, etc.).
    /// </summary>
    public string PresentationFolderPath => Path.Combine(CurrentVersionFolderPath, "Presentation");

    /// <summary>
    /// Path to the Release subfolder within the current version folder.
    /// Used to store release-ready files.
    /// </summary>
    public string ReleaseFolderPath => Path.Combine(CurrentVersionFolderPath, "Release");

    /// <summary>
    /// Event raised when category changes.
    /// </summary>
    public event EventHandler? CategoryChanged;

    /// <summary>
    /// Gets the expected thumbnail path for a video file.
    /// Uses the naming convention: {videoname}_thumb.webp
    /// </summary>
    public static string GetVideoThumbnailPath(string videoPath) 
        => MediaFileExtensions.GetVideoThumbnailPath(videoPath);

    /// <summary>
    /// Checks if a file is a video thumbnail (ends with _thumb.webp, _thumb.jpg, or _thumb.png).
    /// </summary>
    public static bool IsVideoThumbnailFile(string filePath) 
        => MediaFileExtensions.IsVideoThumbnailFile(filePath);

    /// <summary>
    /// Checks if a file is an image file.
    /// </summary>
    public static bool IsImageFile(string filePath) 
        => MediaFileExtensions.IsImageFile(filePath);

    /// <summary>
    /// Checks if a file is a video file.
    /// </summary>
    public static bool IsVideoFile(string filePath) 
        => MediaFileExtensions.IsVideoFile(filePath);

    /// <summary>
    /// Checks if a file is a media file (image or video).
    /// </summary>
    public static bool IsMediaFile(string filePath) 
        => MediaFileExtensions.IsMediaFile(filePath);

    /// <summary>
    /// Checks if a file is a caption file.
    /// </summary>
    public static bool IsCaptionFile(string filePath) 
        => MediaFileExtensions.IsCaptionFile(filePath);

    /// <summary>
    /// Gets supported media extensions (images + videos).
    /// </summary>
    public static IReadOnlyList<string> GetMediaExtensions() 
        => MediaFileExtensions.MediaExtensions;

    /// <summary>
    /// Gets supported image extensions.
    /// </summary>
    public static IReadOnlyList<string> GetImageExtensions() 
        => MediaFileExtensions.ImageExtensions;

    /// <summary>
    /// Gets supported video extensions.
    /// </summary>
    public static IReadOnlyList<string> GetVideoExtensions() 
        => MediaFileExtensions.VideoExtensions;

    /// <summary>
    /// Creates a DatasetCardViewModel from a folder path.
    /// Detects whether it's a legacy or versioned structure.
    /// </summary>
    public static DatasetCardViewModel FromFolder(string folderPath)
    {
        var name = Path.GetFileName(folderPath) ?? folderPath;
        
        var vm = new DatasetCardViewModel
        {
            Name = name,
            FolderPath = folderPath
        };

        // Load metadata (will detect and migrate legacy structure if needed)
        vm.LoadMetadata();
        
        // Detect folder structure and load media files
        vm.DetectAndLoadMedia();

        return vm;
    }

    /// <summary>
    /// Creates a version-specific card for the flattened view.
    /// </summary>
    /// <param name="version">The version number to display.</param>
    /// <returns>A new card representing this specific version.</returns>
    public DatasetCardViewModel CreateVersionCard(int version)
    {
        var versionPath = GetVersionFolderPath(version);
        
        var card = new DatasetCardViewModel
        {
            Name = Name,
            FolderPath = FolderPath,
            CategoryId = CategoryId,
            CategoryOrder = CategoryOrder,
            CategoryName = CategoryName,
            Type = Type,
            IsVersionedStructure = true,
            CurrentVersion = version,
            TotalVersions = TotalVersions,
            DisplayVersion = version,
            VersionDescriptions = VersionDescriptions,
            VersionNsfwFlags = VersionNsfwFlags
        };
        
        // Set the version-specific description
        card._currentVersionDescription = VersionDescriptions.GetValueOrDefault(version);
        
        // Set the version-specific NSFW flag
        card._isNsfw = VersionNsfwFlags.GetValueOrDefault(version, false);

        // Load media files from this specific version folder
        if (Directory.Exists(versionPath))
        {
            var files = Directory.EnumerateFiles(versionPath).ToList();
            
            var videos = files.Where(f => IsVideoFile(f)).ToList();
            
            // Count images, excluding video thumbnails (files ending with _thumb.webp etc.)
            var images = files.Where(f => IsImageFile(f) && !IsVideoThumbnailFile(f)).ToList();
            
            // Count captions
            var captions = files.Where(f => IsCaptionFile(f)).ToList();
            
            card.ImageCount = images.Count;
            card.VideoCount = videos.Count;
            card.CaptionCount = captions.Count;
            
            // For version cards, the "all versions" totals are not used (they show current version info)
            card.TotalImageCountAllVersions = images.Count;
            card.TotalVideoCountAllVersions = videos.Count;
            card.TotalCaptionCountAllVersions = captions.Count;
            
            // Prefer image for thumbnail, fallback to video thumbnail if available
            if (images.Count > 0)
            {
                card.ThumbnailPath = images.First();
            }
            else if (videos.Count > 0)
            {
                // For videos, look for an existing thumbnail (video_thumb.webp)
                var firstVideo = videos.First();
                var videoThumbnail = GetVideoThumbnailPath(firstVideo);
                card.ThumbnailPath = File.Exists(videoThumbnail) ? videoThumbnail : firstVideo;
            }
        }

        return card;
    }

    /// <summary>
    /// Gets all version numbers for this dataset.
    /// </summary>
    public List<int> GetAllVersionNumbers()
    {
        if (IsTemporaryDataset)
            return [1];

        if (!Directory.Exists(_folderPath))
            return [1];

        var versionFolders = Directory.GetDirectories(_folderPath)
            .Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^V\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Select(d => int.Parse(Path.GetFileName(d).Substring(1)))
            .OrderBy(v => v)
            .ToList();

        return versionFolders.Count > 0 ? versionFolders : [1];
    }

    /// <summary>
    /// Detects the folder structure (versioned or legacy) and loads media file info.
    /// </summary>
    private void DetectAndLoadMedia()
    {
        if (IsTemporaryDataset)
        {
            LoadTemporaryDatasetMedia();
            return;
        }

        if (!Directory.Exists(_folderPath))
        {
            ImageCount = 0;
            VideoCount = 0;
            CaptionCount = 0;
            TotalImageCountAllVersions = 0;
            TotalVideoCountAllVersions = 0;
            TotalCaptionCountAllVersions = 0;
            ThumbnailPath = null;
            return;
        }

        // Check for versioned structure (V1, V2, etc. folders)
        var versionFolders = Directory.GetDirectories(_folderPath)
            .Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^V\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .OrderBy(d => int.Parse(Path.GetFileName(d).Substring(1)))
            .ToList();

        if (versionFolders.Count > 0)
        {
            // Versioned structure
            IsVersionedStructure = true;
            TotalVersions = versionFolders.Count;
            
            // If current version from metadata doesn't exist, use highest version
            var maxVersion = versionFolders.Max(d => int.Parse(Path.GetFileName(d).Substring(1)));
            if (CurrentVersion > maxVersion || CurrentVersion < 1)
            {
                CurrentVersion = maxVersion;
            }

            // Calculate totals across all versions
            var totalImages = 0;
            var totalVideos = 0;
            var totalCaptions = 0;
            foreach (var versionFolder in versionFolders)
            {
                var files = Directory.EnumerateFiles(versionFolder).ToList();
                totalImages += files.Count(f => IsImageFile(f) && !IsVideoThumbnailFile(f));
                totalVideos += files.Count(f => IsVideoFile(f));
                totalCaptions += files.Count(f => IsCaptionFile(f));
            }
            TotalImageCountAllVersions = totalImages;
            TotalVideoCountAllVersions = totalVideos;
            TotalCaptionCountAllVersions = totalCaptions;
        }
        else
        {
            // Legacy or empty structure - check if there are media files in root
            IsVersionedStructure = false;
            TotalVersions = 1;
            CurrentVersion = 1;
            
            // For non-versioned, totals equal current counts (set below)
        }

        // Load media files from current version folder
        var mediaPath = CurrentVersionFolderPath;
        if (Directory.Exists(mediaPath))
        {
            var files = Directory.EnumerateFiles(mediaPath).ToList();
            
            var videos = files.Where(f => IsVideoFile(f)).ToList();
            
            // Count images, excluding video thumbnails (files ending with _thumb.webp etc.)
            var images = files.Where(f => IsImageFile(f) && !IsVideoThumbnailFile(f)).ToList();
            
            // Count captions
            var captions = files.Where(f => IsCaptionFile(f)).ToList();

            ImageCount = images.Count;
            VideoCount = videos.Count;
            CaptionCount = captions.Count;
            
            // For non-versioned structure, totals equal current counts
            if (!IsVersionedStructure)
            {
                TotalImageCountAllVersions = images.Count;
                TotalVideoCountAllVersions = videos.Count;
                TotalCaptionCountAllVersions = captions.Count;
            }
            
            // Prefer image for thumbnail, fallback to video thumbnail if available
            if (images.Count > 0)
            {
                ThumbnailPath = images.First();
            }
            else if (videos.Count > 0)
            {
                // For videos, look for an existing thumbnail (video_thumb.webp)
                var firstVideo = videos.First();
                var videoThumbnail = GetVideoThumbnailPath(firstVideo);
                ThumbnailPath = File.Exists(videoThumbnail) ? videoThumbnail : firstVideo;
            }
            else
            {
                ThumbnailPath = null;
            }
        }
        else
        {
            ImageCount = 0;
            VideoCount = 0;
            CaptionCount = 0;
            ThumbnailPath = null;
            
            if (!IsVersionedStructure)
            {
                TotalImageCountAllVersions = 0;
                TotalVideoCountAllVersions = 0;
                TotalCaptionCountAllVersions = 0;
            }
        }
    }

    /// <summary>
    /// Loads metadata from the config file.
    /// Handles migration from legacy .dataset.json to .dataset/config.json.
    /// </summary>
    public void LoadMetadata()
    {
        // Check for versioned config first
        if (File.Exists(MetadataFilePath))
        {
            LoadMetadataFromFile(MetadataFilePath);
            return;
        }

        // Check for legacy config and migrate
        if (File.Exists(LegacyMetadataFilePath))
        {
            LoadMetadataFromFile(LegacyMetadataFilePath);
            // Note: Migration to new location happens on next save
            return;
        }

        // No metadata - defaults
        CategoryId = null;
        CurrentVersion = 1;
    }

    private void LoadMetadataFromFile(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            var metadata = System.Text.Json.JsonSerializer.Deserialize<DatasetMetadata>(json);
            if (metadata is not null)
            {
                // Use CategoryOrder (stable), fallback to legacy CategoryId for migration
                CategoryOrder = metadata.CategoryOrder ?? metadata.CategoryId;
                Type = metadata.Type;
                CurrentVersion = metadata.CurrentVersion > 0 ? metadata.CurrentVersion : 1;
                VersionBranchedFrom = metadata.VersionBranchedFrom ?? new();
                VersionDescriptions = metadata.VersionDescriptions ?? new();
                VersionNsfwFlags = metadata.VersionNsfwFlags ?? new();
                
                // Migrate old single Description to V1 if present and no version descriptions exist
                if (!string.IsNullOrWhiteSpace(metadata.Description) && VersionDescriptions.Count == 0)
                {
                    VersionDescriptions[1] = metadata.Description;
                }
                
                // Set the current version's description
                _currentVersionDescription = VersionDescriptions.GetValueOrDefault(CurrentVersion);
                OnPropertyChanged(nameof(Description));
                OnPropertyChanged(nameof(HasDescription));
                
                // Set the current version's NSFW flag
                _isNsfw = VersionNsfwFlags.GetValueOrDefault(CurrentVersion, false);
                OnPropertyChanged(nameof(IsNsfw));
            }
        }
        catch (IOException)
        {
            // File may be locked or inaccessible - use defaults
        }
        catch (System.Text.Json.JsonException)
        {
            // Invalid JSON - use defaults
        }
    }

    /// <summary>
    /// Saves metadata to the .dataset/config.json file.
    /// Creates the .dataset folder if it doesn't exist.
    /// </summary>
    public void SaveMetadata()
    {
        try
        {
            // Ensure .dataset folder exists
            Directory.CreateDirectory(ConfigFolderPath);

            var metadata = new DatasetMetadata
            {
                CategoryOrder = CategoryOrder,
                Type = Type,
                CurrentVersion = CurrentVersion,
                VersionBranchedFrom = VersionBranchedFrom,
                VersionDescriptions = VersionDescriptions,
                VersionNsfwFlags = VersionNsfwFlags
            };
            var json = System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(MetadataFilePath, json);

            // Clean up legacy file if it exists
            if (File.Exists(LegacyMetadataFilePath))
            {
                try 
                { 
                    File.Delete(LegacyMetadataFilePath); 
                }
                catch (IOException)
                {
                    // Legacy file may be locked - ignore, will try again next save
                }
            }
        }
        catch (IOException)
        {
            // File may be locked or folder inaccessible - metadata not saved
        }
        catch (UnauthorizedAccessException)
        {
            // No permission to write - metadata not saved
        }
    }

    /// <summary>
    /// Records that a version was branched from another version.
    /// </summary>
    /// <param name="newVersion">The new version number.</param>
    /// <param name="branchedFromVersion">The version it was branched from.</param>
    public void RecordBranch(int newVersion, int branchedFromVersion)
    {
        VersionBranchedFrom[newVersion] = branchedFromVersion;
    }

    /// <summary>
    /// Gets the version that a specific version was branched from.
    /// </summary>
    /// <param name="version">The version to check.</param>
    /// <returns>The parent version, or null if not tracked (V1 or legacy).</returns>
    public int? GetBranchedFrom(int version)
    {
        return VersionBranchedFrom.TryGetValue(version, out var parent) ? parent : null;
    }

    /// <summary>
    /// Gets the folder path for a specific version.
    /// </summary>
    public string GetVersionFolderPath(int version)
    {
        if (IsTemporaryDataset)
        {
            return Path.Combine(TemporaryDatasetConstants.GenerationGalleryTempRootPath, $"V{version}");
        }

        return Path.Combine(_folderPath, $"V{version}");
    }

    /// <summary>
    /// Gets the next version number.
    /// </summary>
    public int GetNextVersionNumber()
    {
        if (!Directory.Exists(_folderPath))
            return 1;

        var versionFolders = Directory.GetDirectories(_folderPath)
            .Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^V\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();

        if (versionFolders.Count == 0)
            return _isVersionedStructure ? 2 : 1;

        var maxVersion = versionFolders.Max(d => int.Parse(Path.GetFileName(d).Substring(1)));
        return maxVersion + 1;
    }

    /// <summary>
    /// Refreshes the media count and thumbnail from the current version folder.
    /// For version cards (flattened view), only refreshes the specific version's counts.
    /// </summary>
    public void RefreshImageInfo()
    {
        if (IsVersionCard && _displayVersion.HasValue)
        {
            // For version cards, only refresh counts for the specific version
            RefreshVersionCardCounts(_displayVersion.Value);
        }
        else
        {
            // For normal cards, do full detection
            DetectAndLoadMedia();
        }
    }

    /// <summary>
    /// Refreshes just the media counts for a specific version (used by version cards in flattened view).
    /// </summary>
    private void RefreshVersionCardCounts(int version)
    {
        var versionPath = GetVersionFolderPath(version);
        
        if (!Directory.Exists(versionPath))
        {
            ImageCount = 0;
            VideoCount = 0;
            CaptionCount = 0;
            ThumbnailPath = null;
            return;
        }

        var files = Directory.EnumerateFiles(versionPath).ToList();
        
        var videos = files.Where(f => IsVideoFile(f)).ToList();
        var images = files.Where(f => IsImageFile(f) && !IsVideoThumbnailFile(f)).ToList();
        var captions = files.Where(f => IsCaptionFile(f)).ToList();
        
        ImageCount = images.Count;
        VideoCount = videos.Count;
        CaptionCount = captions.Count;
        
        // For version cards, the "all versions" totals show the same as current (they display DetailedCountText)
        TotalImageCountAllVersions = images.Count;
        TotalVideoCountAllVersions = videos.Count;
        TotalCaptionCountAllVersions = captions.Count;
        
        // Update thumbnail
        if (images.Count > 0)
        {
            ThumbnailPath = images.First();
        }
        else if (videos.Count > 0)
        {
            var firstVideo = videos.First();
            var videoThumbnail = GetVideoThumbnailPath(firstVideo);
            ThumbnailPath = File.Exists(videoThumbnail) ? videoThumbnail : firstVideo;
        }
        else
        {
            ThumbnailPath = null;
        }
    }

    private bool IsTemporaryDataset =>
        string.Equals(_folderPath, TemporaryDatasetConstants.GenerationGalleryTempDatasetPath, StringComparison.OrdinalIgnoreCase);

    private void LoadTemporaryDatasetMedia()
    {
        IsVersionedStructure = true;
        TotalVersions = 1;
        CurrentVersion = 1;

        var versionPath = GetVersionFolderPath(1);
        if (!Directory.Exists(versionPath))
        {
            ImageCount = 0;
            VideoCount = 0;
            CaptionCount = 0;
            TotalImageCountAllVersions = 0;
            TotalVideoCountAllVersions = 0;
            TotalCaptionCountAllVersions = 0;
            ThumbnailPath = null;
            return;
        }

        var files = Directory.EnumerateFiles(versionPath).ToList();
        var videos = files.Where(f => IsVideoFile(f)).ToList();
        var images = files.Where(f => IsImageFile(f) && !IsVideoThumbnailFile(f)).ToList();
        var captions = files.Where(f => IsCaptionFile(f)).ToList();

        ImageCount = images.Count;
        VideoCount = videos.Count;
        CaptionCount = captions.Count;
        TotalImageCountAllVersions = images.Count;
        TotalVideoCountAllVersions = videos.Count;
        TotalCaptionCountAllVersions = captions.Count;

        if (images.Count > 0)
        {
            ThumbnailPath = images.First();
        }
        else if (videos.Count > 0)
        {
            var firstVideo = videos.First();
            var videoThumbnail = GetVideoThumbnailPath(firstVideo);
            ThumbnailPath = File.Exists(videoThumbnail) ? videoThumbnail : firstVideo;
        }
        else
        {
            ThumbnailPath = null;
        }
    }

    /// <summary>
    /// Returns a version of this card safe for display in Safe Mode.
    /// If the current card is already safe, returns this.
    /// If the current card is NSFW but has safe versions (Mixed), returns a snapshot of the latest safe version.
    /// If the card is completely NSFW (or is a version card that is NSFW), returns null.
    /// </summary>
    public DatasetCardViewModel? GetSafeSnapshot()
    {
        // 1. If this card is explicitly marked Safe, it is safe to show.
        if (!IsNsfw) return this;

        // 2. If it is a Version Card (Flattened View) and is NSFW, it must be hidden.
        if (IsVersionCard) return null;

        // 3. It is a Collapsed Card (Dataset View) and the CURRENT version is NSFW.
        // We need to check if there is ANY safe version we can display instead.
        
        // Find the latest Safe version number
        var safeVersion = VersionNsfwFlags
            .Where(kvp => !kvp.Value) // IsNsfw == false
            .OrderByDescending(kvp => kvp.Key)
            .Select(kvp => (int?)kvp.Key)
            .FirstOrDefault();
            
        // If we found no safe version explicitly, but V1 exists and isn't flagged NSFW in the dict (e.g. legacy/incomplete metadata), try V1.
        // (Note: VersionNsfwFlags usually contains all keys, but simple safety check).
        if (!safeVersion.HasValue && GetAllVersionNumbers().Contains(1))
        {
             if (!_versionNsfwFlags.ContainsKey(1) || !_versionNsfwFlags[1])
             {
                 safeVersion = 1;
             }
        }

        // If still no safe version, the whole dataset is effectively NSFW for Safe Mode. Hide it.
        if (!safeVersion.HasValue) return null;

        // 4. Create a Safe Snapshot
        // We create a card that represents the Safe Version but masquerades as the Dataset Card (preserving totals).
        var snapshot = CreateVersionCard(safeVersion.Value);
        
        // Adjust to look like a Collapsed Card
        snapshot.DisplayVersion = null; // Don't show "V1" badge, show "X Versions" badge logic
        
        // Copy Totals from 'this' (the main dataset tracker) so the badge says e.g. "3 Versions" / "150 Images"
        snapshot.TotalVersions = TotalVersions; 
        snapshot.TotalImageCountAllVersions = TotalImageCountAllVersions;
        snapshot.TotalVideoCountAllVersions = TotalVideoCountAllVersions;
        snapshot.TotalCaptionCountAllVersions = TotalCaptionCountAllVersions;

        return snapshot;
    }

    /// <summary>
    /// Returns the dataset name for display in editable ComboBox controls.
    /// </summary>
    public override string ToString() => Name;
}

/// <summary>
/// Metadata stored in each dataset folder.
/// </summary>
public class DatasetMetadata
{
    /// <summary>
    /// Category order for this dataset.
    /// This is the stable identifier that persists across database recreations.
    /// Default categories: Character=0, Style=1, Concept=2.
    /// </summary>
    public int? CategoryOrder { get; set; }

    /// <summary>
    /// Optional description for this dataset.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The type of content in this dataset (Image, Video, Instruction).
    /// </summary>
    public DatasetType? Type { get; set; }

    /// <summary>
    /// Current active version number (1-based). Default is 1.
    /// </summary>
    public int CurrentVersion { get; set; } = 1;

    /// <summary>
    /// List of version descriptions/notes.
    /// Key is version number, value is optional description (e.g., "SDXL captions", "Flux NL captions").
    /// </summary>
    public Dictionary<int, string?> VersionDescriptions { get; set; } = new();

    /// <summary>
    /// Tracks which version each version was branched from.
    /// Key is version number, value is the parent version number it was branched from.
    /// V1 has no entry (it's the original).
    /// </summary>
    public Dictionary<int, int> VersionBranchedFrom { get; set; } = new();

    /// <summary>
    /// NSFW flags for each version.
    /// Key is version number, value is whether that version contains NSFW content.
    /// </summary>
    public Dictionary<int, bool> VersionNsfwFlags { get; set; } = new();

    #region Legacy Properties (for migration)

    /// <summary>
    /// Legacy: Category ID. Kept for backward compatibility during migration.
    /// New code should use CategoryOrder.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? CategoryId { get; set; }

    #endregion
}
