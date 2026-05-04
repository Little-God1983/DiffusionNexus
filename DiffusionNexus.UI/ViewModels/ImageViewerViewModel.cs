using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Utilities;
using Serilog;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the full-screen image viewer lightbox.
/// Provides navigation between images and access to common actions.
/// 
/// <para>
/// <b>Event Integration:</b>
/// This ViewModel publishes rating change events via <see cref="IDatasetEventAggregator"/>
/// to ensure all components stay synchronized when ratings are modified in the viewer.
/// </para>
/// </summary>
public partial class ImageViewerViewModel : ObservableObject, IDisposable
{
    private readonly ObservableCollection<DatasetImageViewModel> _allImages;
    private readonly IDatasetEventAggregator? _eventAggregator;
    private readonly Action<DatasetImageViewModel>? _onSendToImageEditor;
    private readonly Action<DatasetImageViewModel>? _onSendToCaptioning;
    private readonly Action<DatasetImageViewModel>? _onDeleteRequested;
    private readonly Func<string, Task<bool>>? _onToggleFavorite;
    private readonly Func<string, bool>? _isFavoriteCheck;
    private readonly IVideoThumbnailService? _videoThumbnailService;

    private DatasetImageViewModel? _currentImage;
    private int _currentIndex;
    private bool _disposed;
    private bool _isFavorite;
    private CancellationTokenSource? _thumbnailCts;
    private Bitmap? _videoPosterBitmap;

    /// <summary>Video player for playing video files in the lightbox viewer.</summary>
    public VideoPlayerViewModel VideoPlayer { get; } = new();

    /// <summary>ViewModel for the generation metadata side panel.</summary>
    public ImageMetadataPanelViewModel MetadataPanel { get; } = new();

    /// <summary>Event raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Event raised when the current image changes.</summary>
    public event EventHandler? CurrentImageChanged;

    /// <summary>The currently displayed image.</summary>
    public DatasetImageViewModel? CurrentImage
    {
        get => _currentImage;
        private set
        {
            if (SetProperty(ref _currentImage, value))
            {
                OnPropertyChanged(nameof(HasCurrentImage));
                OnPropertyChanged(nameof(ImagePath));
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(Caption));
                OnPropertyChanged(nameof(HasCaption));
                OnPropertyChanged(nameof(IsApproved));
                OnPropertyChanged(nameof(IsRejected));
                OnPropertyChanged(nameof(IsVideo));
                OnPropertyChanged(nameof(IsImage));
                OnPropertyChanged(nameof(PositionText));
                OnPropertyChanged(nameof(TotalCount));
                MetadataPanel.LoadMetadata(value?.ImagePath);
                CurrentImageChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Current position in the collection (0-based).</summary>
    public int CurrentIndex
    {
        get => _currentIndex;
        private set
        {
            if (SetProperty(ref _currentIndex, value))
            {
                OnPropertyChanged(nameof(PositionText));
                OnPropertyChanged(nameof(CanGoPrevious));
                OnPropertyChanged(nameof(CanGoNext));
            }
        }
    }

    public int TotalCount => _allImages.Count;
    public string PositionText => TotalCount > 0 ? $"{CurrentIndex + 1} / {TotalCount}" : "0 / 0";
    public bool HasCurrentImage => _currentImage is not null;
    public string? ImagePath => _currentImage?.ImagePath;

    public string? FileName => _currentImage?.FullFileName;
    public string? Caption => _currentImage?.Caption;
    public bool HasCaption => !string.IsNullOrWhiteSpace(_currentImage?.Caption);
    public bool IsApproved => _currentImage?.IsApproved ?? false;
    public bool IsRejected => _currentImage?.IsRejected ?? false;
    public bool IsVideo => _currentImage?.IsVideo ?? false;
    public bool IsImage => _currentImage?.IsImage ?? true;
    public bool CanGoPrevious => _currentIndex > 0;
    public bool CanGoNext => _currentIndex < _allImages.Count - 1;
    public bool ShowRatingControls { get; }

    /// <summary>
    /// Poster bitmap for the current video, loaded directly from the thumbnail file.
    /// Bypasses the DatasetImageViewModel.Thumbnail orchestrator chain which uses a
    /// different code path than the gallery and fails silently for video thumbnails.
    /// </summary>
    public Bitmap? VideoPosterBitmap
    {
        get => _videoPosterBitmap;
        private set => SetProperty(ref _videoPosterBitmap, value);
    }

    /// <summary>Whether the current image is marked as a favorite.</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        private set => SetProperty(ref _isFavorite, value);
    }

    /// <summary>Whether favorite controls should be shown (only when a toggle callback is provided).</summary>
    public bool ShowFavoriteControls => _onToggleFavorite is not null;

    #region Commands

    public IRelayCommand PreviousCommand { get; }
    public IRelayCommand NextCommand { get; }
    public IRelayCommand CloseCommand { get; }
    public IRelayCommand MarkApprovedCommand { get; }
    public IRelayCommand MarkRejectedCommand { get; }
    public IRelayCommand ClearRatingCommand { get; }
    public IRelayCommand SendToImageEditorCommand { get; }
    public IRelayCommand SendToCaptioningCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand ToggleFavoriteCommand { get; }

    #endregion

    /// <summary>
    /// Creates a new ImageViewerViewModel with event aggregator support.
    /// </summary>
    /// <param name="images">The collection of all images in the dataset.</param>
    /// <param name="startIndex">The index of the image to display initially.</param>
    /// <param name="eventAggregator">Optional event aggregator for publishing events.</param>
    /// <param name="onSendToImageEditor">Callback when "Send to Image Editor" is requested.</param>
    /// <param name="onSendToCaptioning">Callback when "Send to Captioning" is requested.</param>
    /// <param name="onDeleteRequested">Callback when delete is requested.</param>
    public ImageViewerViewModel(
        ObservableCollection<DatasetImageViewModel> images,
        int startIndex,
        IDatasetEventAggregator? eventAggregator = null,
        Action<DatasetImageViewModel>? onSendToImageEditor = null,
        Action<DatasetImageViewModel>? onSendToCaptioning = null,
        Action<DatasetImageViewModel>? onDeleteRequested = null,
        bool showRatingControls = true,
        Func<string, Task<bool>>? onToggleFavorite = null,
        Func<string, bool>? isFavoriteCheck = null,
        IVideoThumbnailService? videoThumbnailService = null)
    {
        _allImages = images ?? throw new ArgumentNullException(nameof(images));
        _eventAggregator = eventAggregator;
        _onSendToImageEditor = onSendToImageEditor;
        _onSendToCaptioning = onSendToCaptioning;
        _onDeleteRequested = onDeleteRequested;
        _onToggleFavorite = onToggleFavorite;
        ShowRatingControls = showRatingControls;
        _isFavoriteCheck = isFavoriteCheck;
        _videoThumbnailService = videoThumbnailService;

        // Subscribe to collection changes to handle external deletions
        _allImages.CollectionChanged += OnCollectionChanged;

        PreviousCommand = new RelayCommand(GoPrevious, () => CanGoPrevious);
        NextCommand = new RelayCommand(GoNext, () => CanGoNext);
        CloseCommand = new RelayCommand(Close);
        MarkApprovedCommand = new RelayCommand(MarkApproved);
        MarkRejectedCommand = new RelayCommand(MarkRejected);
        ClearRatingCommand = new RelayCommand(ClearRating);
        SendToImageEditorCommand = new RelayCommand(SendToImageEditor);
        SendToCaptioningCommand = new RelayCommand(SendToCaptioning);
        DeleteCommand = new RelayCommand(Delete);
        ToggleFavoriteCommand = new AsyncRelayCommand(ToggleFavoriteAsync);

        NavigateTo(Math.Clamp(startIndex, 0, Math.Max(0, images.Count - 1)));
    }

    /// <summary>Design-time constructor.</summary>
    public ImageViewerViewModel() : this([], 0)
    {
    }

    /// <summary>
    /// Handles collection changes to properly update navigation when images are deleted.
    /// </summary>
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Remove)
        {
            // Collection has changed - update the UI
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(PositionText));

            if (_allImages.Count == 0)
            {
                // No more images - close the viewer
                CurrentImage = null;
                CurrentIndex = 0;
                Close();
                return;
            }

            // If the current image was removed, navigate to appropriate image
            if (_currentImage is not null && !_allImages.Contains(_currentImage))
            {
                // Current image was deleted - navigate to next available
                var newIndex = Math.Min(_currentIndex, _allImages.Count - 1);
                NavigateTo(newIndex);
            }
            else if (_currentImage is not null)
            {
                // Current image still exists but index may have changed
                var actualIndex = _allImages.IndexOf(_currentImage);
                if (actualIndex >= 0 && actualIndex != _currentIndex)
                {
                    CurrentIndex = actualIndex;
                }
            }

            // Update command states
            ((RelayCommand)PreviousCommand).NotifyCanExecuteChanged();
            ((RelayCommand)NextCommand).NotifyCanExecuteChanged();
        }
    }

    /// <summary>Navigates to a specific index in the collection.</summary>
    public void NavigateTo(int index)
    {
        if (_allImages.Count == 0)
        {
            CurrentImage = null;
            CurrentIndex = 0;
            IsFavorite = false;
            return;
        }

        if (index < 0 || index >= _allImages.Count) return;

        CurrentIndex = index;
        CurrentImage = _allImages[index];
        IsFavorite = _isFavoriteCheck is not null && CurrentImage is not null
            && _isFavoriteCheck(CurrentImage.ImagePath);

        // Load or stop video player based on whether the current item is a video
        if (CurrentImage?.IsVideo == true)
        {
            VideoPlayer.LoadVideo(CurrentImage.ImagePath);
            _ = LoadVideoPosterAsync(CurrentImage.ImagePath);
        }
        else
        {
            VideoPlayer.Stop();
            VideoPosterBitmap = null;
        }

        ((RelayCommand)PreviousCommand).NotifyCanExecuteChanged();
        ((RelayCommand)NextCommand).NotifyCanExecuteChanged();
    }

    private void GoPrevious()
    {
        if (CanGoPrevious) NavigateTo(CurrentIndex - 1);
    }

    private void GoNext()
    {
        if (CanGoNext) NavigateTo(CurrentIndex + 1);
    }

    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void MarkApproved()
    {
        if (_currentImage is null) return;
        
        var previousRating = _currentImage.RatingStatus;
        _currentImage.RatingStatus = _currentImage.IsApproved 
            ? ImageRatingStatus.Unrated 
            : ImageRatingStatus.Approved;
        _currentImage.SaveRating();
        
        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(IsRejected));

        // Publish event via aggregator
        _eventAggregator?.PublishImageRatingChanged(new ImageRatingChangedEventArgs
        {
            Image = _currentImage,
            NewRating = _currentImage.RatingStatus,
            PreviousRating = previousRating
        });
    }

    private void MarkRejected()
    {
        if (_currentImage is null) return;
        
        var previousRating = _currentImage.RatingStatus;
        _currentImage.RatingStatus = _currentImage.IsRejected 
            ? ImageRatingStatus.Unrated 
            : ImageRatingStatus.Rejected;
        _currentImage.SaveRating();
        
        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(IsRejected));

        // Publish event via aggregator
        _eventAggregator?.PublishImageRatingChanged(new ImageRatingChangedEventArgs
        {
            Image = _currentImage,
            NewRating = _currentImage.RatingStatus,
            PreviousRating = previousRating
        });
    }

    private void ClearRating()
    {
        if (_currentImage is null) return;
        
        var previousRating = _currentImage.RatingStatus;
        _currentImage.RatingStatus = ImageRatingStatus.Unrated;
        _currentImage.SaveRating();
        
        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(IsRejected));

        // Publish event via aggregator
        _eventAggregator?.PublishImageRatingChanged(new ImageRatingChangedEventArgs
        {
            Image = _currentImage,
            NewRating = ImageRatingStatus.Unrated,
            PreviousRating = previousRating
        });
    }

    private void SendToImageEditor()
    {
        if (_currentImage is null) return;
        _onSendToImageEditor?.Invoke(_currentImage);
        Close();
    }

    private void SendToCaptioning()
    {
        if (_currentImage is null) return;
        _onSendToCaptioning?.Invoke(_currentImage);
        Close();
    }

    private void Delete()
    {
        if (_currentImage is null) return;

        // Simply invoke the delete callback - the collection change handler
        // will take care of navigation when the image is actually removed
        _onDeleteRequested?.Invoke(_currentImage);
    }

    private async Task ToggleFavoriteAsync()
    {
        if (_currentImage is null || _onToggleFavorite is null) return;
        IsFavorite = await _onToggleFavorite(_currentImage.ImagePath);
    }

    /// <summary>
    /// Loads the video poster bitmap directly from the thumbnail file.
    /// Generates the thumbnail on-demand if it doesn't exist yet.
    /// Uses direct file I/O instead of the DatasetImageViewModel.Thumbnail orchestrator
    /// chain, which passes the .webp path and bypasses ThumbnailService's video-special
    /// handling (the gallery passes the .mp4 path, which triggers that handling correctly).
    /// </summary>
    private async Task LoadVideoPosterAsync(string videoPath)
    {
        // Cancel any previous in-flight poster load
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = new CancellationTokenSource();
        var ct = _thumbnailCts.Token;

        VideoPosterBitmap = null;

        try
        {
            var thumbPath = MediaFileExtensions.GetVideoThumbnailPath(videoPath);

            // Generate thumbnail if it doesn't exist yet
            if (!File.Exists(thumbPath) && _videoThumbnailService is not null)
            {
                var result = await _videoThumbnailService.GenerateThumbnailAsync(videoPath, cancellationToken: ct);
                if (ct.IsCancellationRequested || _disposed) return;

                if (!result.Success)
                {
                    Log.Warning("[ImageViewer] Poster generation failed for {Path}: {Error}",
                        videoPath, result.ErrorMessage);
                    return;
                }

                // Re-resolve — generation may have written to legacy or new location
                thumbPath = MediaFileExtensions.GetVideoThumbnailPath(videoPath);
            }

            if (!File.Exists(thumbPath) || ct.IsCancellationRequested || _disposed)
                return;

            // Load bitmap directly on a background thread
            var bitmap = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var stream = File.OpenRead(thumbPath);
                return new Bitmap(stream);
            }, ct);

            if (!ct.IsCancellationRequested && !_disposed)
                VideoPosterBitmap = bitmap;
        }
        catch (OperationCanceledException)
        {
            // Expected when navigating away before loading finishes
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ImageViewer] Failed to load poster for {Path}", videoPath);
        }
    }

    /// <summary>Refreshes the display after external changes.</summary>
    public void RefreshCurrentImage()
    {
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(HasCaption));
        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(IsRejected));
    }

    /// <summary>
    /// Disposes resources and unsubscribes from events.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _thumbnailCts?.Cancel();
            _thumbnailCts?.Dispose();
            _allImages.CollectionChanged -= OnCollectionChanged;
            VideoPlayer.Dispose();
        }

        _disposed = true;
    }
}
