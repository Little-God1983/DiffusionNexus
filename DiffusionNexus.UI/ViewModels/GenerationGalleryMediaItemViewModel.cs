using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Converters;
using DiffusionNexus.UI.Services;
using Serilog;

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
    private bool _isFavorite;
    private double _aspectRatio = 1.0;

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
    /// The width-to-height aspect ratio of the original image.
    /// Defaults to 1.0 (square) before the thumbnail loads.
    /// </summary>
    public double AspectRatio
    {
        get => _aspectRatio;
        private set => SetProperty(ref _aspectRatio, value);
    }

    /// <summary>
    /// Whether this item is selected in the gallery.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Whether this item is marked as a favorite.
    /// </summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetProperty(ref _isFavorite, value);
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
            Log.Warning("[MediaItem] No orchestrator available for {Path}", FilePath);
            _isThumbnailLoading = false;
            return;
        }

        if (orchestrator.TryGetCached(FilePath, out var cached) && cached is not null)
        {
            Thumbnail = cached;
            if (cached.PixelSize.Height > 0)
            {
                AspectRatio = (double)cached.PixelSize.Width / cached.PixelSize.Height;
            }
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
                    if (IsVideo)
                        Log.Debug("[MediaItem] Video thumbnail loaded for {Path} ({W}x{H})",
                            FilePath, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        Thumbnail = bitmap;
                        if (bitmap.PixelSize.Height > 0)
                        {
                            AspectRatio = (double)bitmap.PixelSize.Width / bitmap.PixelSize.Height;
                        }
                        _isThumbnailLoading = false;
                    });
            }
            else if (IsVideo)
            {
                Log.Debug("[MediaItem] Orchestrator returned null for video {Path} — awaiting generation", FilePath);
            }
            // else: keep _isThumbnailLoading true to prevent repeated load attempts
            // for items without thumbnails (e.g., videos awaiting generation).
            // ReloadThumbnail() resets the flag when a thumbnail becomes available.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[MediaItem] LoadThumbnailAsync failed for {Path}", FilePath);
            _isThumbnailLoading = false;
        }
    }

    /// <summary>
    /// Resets the thumbnail so it will be reloaded on next access.
    /// Called after a video thumbnail has been generated in the background.
    /// </summary>
    internal void ReloadThumbnail()
    {
        Log.Debug("[MediaItem] ReloadThumbnail called for {Path}", FilePath);
        _isThumbnailLoading = false;
        _thumbnail = null;

        // Raise PropertyChanged on UI thread so the binding re-evaluates the getter
        if (Dispatcher.UIThread.CheckAccess())
        {
            OnPropertyChanged(nameof(Thumbnail));
        }
        else
        {
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(Thumbnail)));
        }
    }
}
