using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Converters;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel representing a single media item in the Generation Gallery.
/// Routes thumbnail loading through <see cref="IThumbnailOrchestrator"/> when available.
/// </summary>
public partial class GenerationGalleryMediaItemViewModel : ObservableObject
{
    private static readonly ThumbnailOwnerToken _defaultOwnerToken = new("GalleryItem");
    private readonly IThumbnailOrchestrator? _thumbnailOrchestrator;
    private readonly ThumbnailOwnerToken? _ownerToken;
    private Bitmap? _thumbnail;
    private bool _isThumbnailLoading;
    private bool _isSelected;

    public GenerationGalleryMediaItemViewModel(
        string filePath,
        bool isVideo,
        DateTime createdAtUtc,
        string folderGroupName,
        IThumbnailOrchestrator? thumbnailOrchestrator = null,
        ThumbnailOwnerToken? ownerToken = null)
    {
        FilePath = filePath;
        IsVideo = isVideo;
        CreatedAtUtc = createdAtUtc;
        FolderGroupName = folderGroupName;
        _thumbnailOrchestrator = thumbnailOrchestrator;
        _ownerToken = ownerToken;
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
    /// The full file name including extension.
    /// </summary>
    public string FullFileName => Path.GetFileName(FilePath);

    /// <summary>
    /// File extension (lowercase).
    /// </summary>
    public string FileExtension => Path.GetExtension(FilePath).ToLowerInvariant();

    /// <summary>
    /// Creation timestamp for sorting.
    /// </summary>
    public DateTime CreatedAtUtc { get; }

    /// <summary>
    /// Group display name for folder grouping.
    /// </summary>
    public string FolderGroupName { get; }

    /// <summary>
    /// Whether this item is selected in the gallery.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// The loaded thumbnail bitmap. Loads asynchronously on first access.
    /// Uses the orchestrator for priority-based loading when available.
    /// </summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (_thumbnail is not null)
                return _thumbnail;

            if (!_isThumbnailLoading)
            {
                _ = LoadThumbnailAsync();
            }

            return null;
        }
        private set => SetProperty(ref _thumbnail, value);
    }

    private async Task LoadThumbnailAsync()
    {
        if (_isThumbnailLoading) return;
        _isThumbnailLoading = true;

        var orchestrator = _thumbnailOrchestrator ?? PathToBitmapConverter.ThumbnailOrchestrator;
        if (orchestrator is null)
        {
            _isThumbnailLoading = false;
            return;
        }

        if (orchestrator.TryGetCached(FilePath, out var cached) && cached is not null)
        {
            Thumbnail = cached;
            _isThumbnailLoading = false;
            return;
        }

        var owner = _ownerToken ?? _defaultOwnerToken;

        try
        {
            var bitmap = await orchestrator.RequestThumbnailAsync(
                FilePath, owner, ThumbnailPriority.Normal).ConfigureAwait(false);

            if (bitmap is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Thumbnail = bitmap;
                    _isThumbnailLoading = false;
                });
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
    /// Notifies the UI that the thumbnail may have been loaded externally (e.g., by the orchestrator's
    /// visible-range preload). Re-evaluates the Thumbnail property getter.
    /// </summary>
    public void NotifyThumbnailAvailable()
    {
        OnPropertyChanged(nameof(Thumbnail));
    }
}
