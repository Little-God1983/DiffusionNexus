using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Converters;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel representing a single media item in the Generation Gallery.
/// </summary>
public partial class GenerationGalleryMediaItemViewModel : ObservableObject
{
    private Bitmap? _thumbnail;
    private bool _isThumbnailLoading;

    public GenerationGalleryMediaItemViewModel(string filePath, bool isVideo, DateTime createdAtUtc)
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

    /// <summary>
    /// The loaded thumbnail bitmap. Loads asynchronously on first access.
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

        var thumbnailService = PathToBitmapConverter.ThumbnailService;
        if (thumbnailService is null)
        {
            _isThumbnailLoading = false;
            return;
        }

        // Try direct cache access first
        if (thumbnailService.TryGetCached(FilePath, out var cached) && cached is not null)
        {
             Thumbnail = cached;
             _isThumbnailLoading = false;
             return;
        }

        try
        {
            // Load async (using default target width from service which is usually matched to card size)
            // Note: Viewer tile width is adjustable, but we use the standard thumbnail size for consistency/caching
            var bitmap = await thumbnailService.LoadThumbnailAsync(FilePath).ConfigureAwait(false);

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
}
