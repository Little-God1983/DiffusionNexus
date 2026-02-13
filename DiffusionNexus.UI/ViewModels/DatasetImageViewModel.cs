using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Converters;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Utilities;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel representing a single media item (image or video) in a dataset with its editable caption.
/// 
/// <para>
/// <b>Event Integration:</b>
/// This ViewModel can publish events via <see cref="IDatasetEventAggregator"/> when:
/// <list type="bullet">
/// <item>Rating changes (approved/rejected/unrated)</item>
/// <item>Caption is saved</item>
/// <item>Selection state changes</item>
/// </list>
/// </para>
/// 
/// <para>
/// <b>Factory Method:</b>
/// Use <see cref="FromFile(string, IDatasetEventAggregator?)"/> to create instances
/// with proper event aggregator integration.
/// </para>
/// </summary>
public class DatasetImageViewModel : ObservableObject
{
    private readonly IDatasetEventAggregator? _eventAggregator;
    private readonly IThumbnailOrchestrator? _thumbnailOrchestrator;
    private readonly ThumbnailOwnerToken? _ownerToken;
    private readonly Stack<string> _undoStack = new();
    private readonly Stack<string> _redoStack = new();
    private bool _isUndoingOrRedoing;
    private string _originalCaption = string.Empty;
    
    private string _imagePath = string.Empty;
    private string _caption = string.Empty;
    private bool _hasUnsavedChanges;
    private bool _isSelected;
    private ImageRatingStatus _ratingStatus = ImageRatingStatus.Unrated;
    private string? _thumbnailPath;
    private bool _isEditorSelected;
    private bool _isVideo;
    private Bitmap? _thumbnail;
    private bool _isThumbnailLoading;

    /// <summary>Full path to the media file (image or video).</summary>
    public string ImagePath
    {
        get => _imagePath;
        set
        {
            if (SetProperty(ref _imagePath, value))
            {
                OnPropertyChanged(nameof(IsVideo));
                OnPropertyChanged(nameof(IsImage));
                OnPropertyChanged(nameof(MediaType));
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(FullFileName));
                OnPropertyChanged(nameof(FileExtension));
                OnPropertyChanged(nameof(HasThumbnail));
                // Reset thumbnail when path changes
                _thumbnail = null;
                OnPropertyChanged(nameof(Thumbnail));
            }
        }
    }

    /// <summary>
    /// Path to the thumbnail for this media item.
    /// For images, this is the same as ImagePath.
    /// For videos, this is the generated thumbnail (.webp) if available.
    /// </summary>
    public string? ThumbnailPath
    {
        get => _thumbnailPath ?? (_isVideo ? GetVideoThumbnailPath() : _imagePath);
        set
        {
            if (SetProperty(ref _thumbnailPath, value))
            {
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }
    }

    /// <summary>
    /// Whether this media item has a thumbnail to display.
    /// </summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailPath);

    /// <summary>
    /// The loaded thumbnail bitmap. Loads asynchronously on first access.
    /// Bind to this property for efficient async thumbnail display.
    /// Routes through <see cref="IThumbnailOrchestrator"/> when available for priority-based loading.
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

            // Prefer orchestrator for cache check, fall back to legacy static service
            Bitmap? cached = null;
            var cacheHit = false;

            if (_thumbnailOrchestrator is not null)
            {
                cacheHit = _thumbnailOrchestrator.TryGetCached(path, out cached);
            }

            if (!cacheHit)
            {
                cacheHit = PathToBitmapConverter.ThumbnailService?.TryGetCached(path, out cached) == true;
            }

            if (cacheHit && cached is not null)
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
    /// Loads the thumbnail asynchronously via the orchestrator (priority-based) or legacy service.
    /// </summary>
    private async Task LoadThumbnailAsync(string path)
    {
        try
        {
            Bitmap? bitmap = null;

            if (_thumbnailOrchestrator is not null && _ownerToken is not null)
            {
                bitmap = await _thumbnailOrchestrator.RequestThumbnailAsync(
                    path, _ownerToken, ThumbnailPriority.Normal).ConfigureAwait(false);
            }
            else
            {
                // Legacy fallback: direct service call
                var thumbnailService = PathToBitmapConverter.ThumbnailService;
                if (thumbnailService is null)
                {
                    _isThumbnailLoading = false;
                    return;
                }

                bitmap = await thumbnailService.LoadThumbnailAsync(path).ConfigureAwait(false);
            }

            if (bitmap is not null)
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    _thumbnail = bitmap;
                    _isThumbnailLoading = false;
                    OnPropertyChanged(nameof(Thumbnail));
                }
                else
                {
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
    /// Forces a refresh of the thumbnail by clearing the local and global cache and notifying observers.
    /// Call this after the underlying file has been modified.
    /// </summary>
    public void RefreshThumbnail()
    {
        if (!string.IsNullOrEmpty(ThumbnailPath))
        {
            if (_thumbnailOrchestrator is not null)
            {
                _thumbnailOrchestrator.Invalidate(ThumbnailPath);
            }
            else
            {
                // Legacy fallback
                PathToBitmapConverter.ThumbnailService?.Invalidate(ThumbnailPath);
            }
        }
        
        // Clear local cache
        _thumbnail = null;
        _isThumbnailLoading = false;
        OnPropertyChanged(nameof(Thumbnail));
    }

    /// <summary>Whether this media item is a video file.</summary>
    public bool IsVideo => _isVideo;

    /// <summary>Whether this media item is an image file.</summary>
    public bool IsImage => !_isVideo;

    /// <summary>Display text for the media type.</summary>
    public string MediaType => _isVideo ? "Video" : "Image";

    /// <summary>File extension of the media file.</summary>
    public string FileExtension => Path.GetExtension(_imagePath).ToUpperInvariant().TrimStart('.');

    /// <summary>Caption text (loaded from .txt file with same name).</summary>
    public string Caption
    {
        get => _caption;
        set
        {
            if (_caption != value && !_isUndoingOrRedoing)
            {
                _undoStack.Push(_caption);
                _redoStack.Clear();
                UndoCaptionCommand.NotifyCanExecuteChanged();
                RedoCaptionCommand.NotifyCanExecuteChanged();
            }

            if (SetProperty(ref _caption, value))
            {
                HasUnsavedChanges = value != _originalCaption;
            }
        }
    }

    /// <summary>Whether the caption has been modified.</summary>
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        set => SetProperty(ref _hasUnsavedChanges, value);
    }

    /// <summary>Whether this item is currently selected.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _eventAggregator?.PublishImageSelectionChanged(new ImageSelectionChangedEventArgs
                {
                    Image = this,
                    IsSelected = value
                });
            }
        }
    }

    /// <summary>Whether this image is currently selected in the Image Editor.</summary>
    public bool IsEditorSelected
    {
        get => _isEditorSelected;
        set => SetProperty(ref _isEditorSelected, value);
    }

    /// <summary>The quality rating status of this media item.</summary>
    public ImageRatingStatus RatingStatus
    {
        get => _ratingStatus;
        set
        {
            if (SetProperty(ref _ratingStatus, value))
            {
                OnPropertyChanged(nameof(IsApproved));
                OnPropertyChanged(nameof(IsRejected));
                OnPropertyChanged(nameof(IsUnrated));
            }
        }
    }

    /// <summary>Whether the item is marked as approved/production-ready.</summary>
    public bool IsApproved => _ratingStatus == ImageRatingStatus.Approved;

    /// <summary>Whether the item is marked as rejected/failed.</summary>
    public bool IsRejected => _ratingStatus == ImageRatingStatus.Rejected;

    /// <summary>Whether the item has not been rated yet.</summary>
    public bool IsUnrated => _ratingStatus == ImageRatingStatus.Unrated;

    /// <summary>File name without extension for display.</summary>
    public string FileName => Path.GetFileNameWithoutExtension(_imagePath);

    /// <summary>Full file name with extension.</summary>
    public string FullFileName => Path.GetFileName(_imagePath);

    /// <summary>Path to the caption text file.</summary>
    public string CaptionFilePath => Path.ChangeExtension(_imagePath, ".txt");

    /// <summary>Path to the rating metadata file.</summary>
    public string RatingFilePath => Path.ChangeExtension(_imagePath, ".rating");

    #region Commands

    public IRelayCommand SaveCaptionCommand { get; }
    public IRelayCommand RevertCaptionCommand { get; }
    public IRelayCommand UndoCaptionCommand { get; }
    public IRelayCommand RedoCaptionCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IRelayCommand MarkApprovedCommand { get; }
    public IRelayCommand MarkRejectedCommand { get; }
    public IRelayCommand ClearRatingCommand { get; }

    #endregion

    /// <summary>
    /// Creates a new DatasetImageViewModel with optional event aggregator and orchestrator integration.
    /// </summary>
    /// <param name="eventAggregator">Optional event aggregator for publishing events.</param>
    /// <param name="thumbnailOrchestrator">Optional orchestrator for priority-based thumbnail loading.</param>
    /// <param name="ownerToken">Owner token identifying the parent view (required when orchestrator is provided).</param>
    public DatasetImageViewModel(
        IDatasetEventAggregator? eventAggregator = null,
        IThumbnailOrchestrator? thumbnailOrchestrator = null,
        ThumbnailOwnerToken? ownerToken = null)
    {
        _eventAggregator = eventAggregator;
        _thumbnailOrchestrator = thumbnailOrchestrator;
        _ownerToken = ownerToken;
        
        SaveCaptionCommand = new RelayCommand(SaveCaption);
        RevertCaptionCommand = new RelayCommand(RevertCaption);
        UndoCaptionCommand = new RelayCommand(UndoCaption, () => _undoStack.Count > 0);
        RedoCaptionCommand = new RelayCommand(RedoCaption, () => _redoStack.Count > 0);
        DeleteCommand = new RelayCommand(Delete);
        MarkApprovedCommand = new RelayCommand(MarkApproved);
        MarkRejectedCommand = new RelayCommand(MarkRejected);
        ClearRatingCommand = new RelayCommand(ClearRating);
    }

    /// <summary>
    /// Creates a DatasetImageViewModel from a media file path.
    /// </summary>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <param name="eventAggregator">Optional event aggregator for publishing events.</param>
    /// <param name="thumbnailOrchestrator">Optional orchestrator for priority-based thumbnail loading.</param>
    /// <param name="ownerToken">Owner token identifying the parent view.</param>
    public static DatasetImageViewModel FromFile(
        string mediaPath,
        IDatasetEventAggregator? eventAggregator = null,
        IThumbnailOrchestrator? thumbnailOrchestrator = null,
        ThumbnailOwnerToken? ownerToken = null)
    {
        var vm = new DatasetImageViewModel(eventAggregator, thumbnailOrchestrator, ownerToken)
        {
            ImagePath = mediaPath,
            _isVideo = IsVideoFile(mediaPath)
        };
        vm.LoadCaption();
        vm.LoadRating();
        return vm;
    }

    /// <summary>Checks if a file is a video file based on its extension.</summary>
    public static bool IsVideoFile(string filePath) 
        => MediaFileExtensions.IsVideoFile(filePath);

    private string? GetVideoThumbnailPath()
    {
        if (string.IsNullOrWhiteSpace(_imagePath))
            return null;
        
        var thumbnailPath = MediaFileExtensions.GetVideoThumbnailPath(_imagePath);
        return File.Exists(thumbnailPath) ? thumbnailPath : null;
    }

    /// <summary>Loads the caption from the associated .txt file.</summary>
    public void LoadCaption()
    {
        if (File.Exists(CaptionFilePath))
        {
            try
            {
                _caption = File.ReadAllText(CaptionFilePath);
                _originalCaption = _caption;
                OnPropertyChanged(nameof(Caption));
            }
            catch (IOException)
            {
                // File may be locked or inaccessible
                _caption = string.Empty;
                _originalCaption = string.Empty;
                OnPropertyChanged(nameof(Caption));
            }
            catch (UnauthorizedAccessException)
            {
                // No permission to read
                _caption = string.Empty;
                _originalCaption = string.Empty;
                OnPropertyChanged(nameof(Caption));
            }
        }
        else
        {
            _caption = string.Empty;
            _originalCaption = string.Empty;
            OnPropertyChanged(nameof(Caption));
        }
        HasUnsavedChanges = false;
    }

    /// <summary>Loads the rating from the associated .rating file.</summary>
    public void LoadRating()
    {
        if (File.Exists(RatingFilePath))
        {
            try
            {
                var content = File.ReadAllText(RatingFilePath).Trim();
                if (Enum.TryParse<ImageRatingStatus>(content, out var status))
                {
                    _ratingStatus = status;
                    OnPropertyChanged(nameof(RatingStatus));
                    OnPropertyChanged(nameof(IsApproved));
                    OnPropertyChanged(nameof(IsRejected));
                    OnPropertyChanged(nameof(IsUnrated));
                }
            }
            catch (IOException)
            {
                // File may be locked or inaccessible
                _ratingStatus = ImageRatingStatus.Unrated;
            }
            catch (UnauthorizedAccessException)
            {
                // No permission to read
                _ratingStatus = ImageRatingStatus.Unrated;
            }
        }
        else
        {
            _ratingStatus = ImageRatingStatus.Unrated;
        }
    }

    /// <summary>Saves the rating to the associated .rating file.</summary>
    public void SaveRating()
    {
        try
        {
            if (_ratingStatus == ImageRatingStatus.Unrated)
            {
                if (File.Exists(RatingFilePath))
                    File.Delete(RatingFilePath);
            }
            else
            {
                File.WriteAllText(RatingFilePath, _ratingStatus.ToString());
            }
        }
        catch (IOException)
        {
            // File may be in use or read-only - rating will be lost on reload
        }
        catch (UnauthorizedAccessException)
        {
            // No permission to write - rating will be lost on reload
        }
    }

    private void SaveCaption()
    {
        try
        {
            File.WriteAllText(CaptionFilePath, _caption);
            _originalCaption = _caption;
            HasUnsavedChanges = false;
            
            // Clear undo/redo stacks on save? 
            // Usually we don't clear undo stack on save, so user can still undo after save.
            // But HasUnsavedChanges becomes false.
            
            // Publish caption saved event
            _eventAggregator?.PublishCaptionChanged(new CaptionChangedEventArgs
            {
                Image = this,
                WasSaved = true
            });
        }
        catch (IOException)
        {
            // File may be in use or read-only - caption not saved
        }
        catch (UnauthorizedAccessException)
        {
            // No permission to write - caption not saved
        }
    }

    private void RevertCaption()
    {
        Caption = _originalCaption;
    }

    private void UndoCaption()
    {
        if (_undoStack.Count == 0) return;
        
        _isUndoingOrRedoing = true;
        try
        {
            var previous = _undoStack.Pop();
            _redoStack.Push(_caption);
            if (SetProperty(ref _caption, previous, nameof(Caption)))
            {
                HasUnsavedChanges = _caption != _originalCaption;
            }
        }
        finally
        {
            _isUndoingOrRedoing = false;
            UndoCaptionCommand.NotifyCanExecuteChanged();
            RedoCaptionCommand.NotifyCanExecuteChanged();
        }
    }

    private void RedoCaption()
    {
        if (_redoStack.Count == 0) return;

        _isUndoingOrRedoing = true;
        try
        {
            var next = _redoStack.Pop();
            _undoStack.Push(_caption);
            if (SetProperty(ref _caption, next, nameof(Caption)))
            {
                HasUnsavedChanges = _caption != _originalCaption;
            }
        }
        finally
        {
            _isUndoingOrRedoing = false;
            UndoCaptionCommand.NotifyCanExecuteChanged();
            RedoCaptionCommand.NotifyCanExecuteChanged();
        }
    }

    private void Delete()
    {
        // Deletion is handled by the parent ViewModel which has access to file operations
        // This command is typically bound to a delete button that triggers a confirmation dialog
        // The actual deletion is performed via the event aggregator or parent callback
    }

    private void MarkApproved() 
        => SetRatingAndPublish(IsApproved ? ImageRatingStatus.Unrated : ImageRatingStatus.Approved);

    private void MarkRejected() 
        => SetRatingAndPublish(IsRejected ? ImageRatingStatus.Unrated : ImageRatingStatus.Rejected);

    private void ClearRating() 
        => SetRatingAndPublish(ImageRatingStatus.Unrated);

    /// <summary>
    /// Sets the rating status, saves to file, and publishes the change event.
    /// </summary>
    private void SetRatingAndPublish(ImageRatingStatus newRating)
    {
        var previousRating = _ratingStatus;
        RatingStatus = newRating;
        SaveRating();

        _eventAggregator?.PublishImageRatingChanged(new ImageRatingChangedEventArgs
        {
            Image = this,
            NewRating = newRating,
            PreviousRating = previousRating
        });
    }
}
