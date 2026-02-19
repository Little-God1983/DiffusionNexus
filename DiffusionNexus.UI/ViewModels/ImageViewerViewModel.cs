using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Services;

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
    
    private DatasetImageViewModel? _currentImage;
    private int _currentIndex;
    private bool _disposed;

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
        bool showRatingControls = true)
    {
        _allImages = images ?? throw new ArgumentNullException(nameof(images));
        _eventAggregator = eventAggregator;
        _onSendToImageEditor = onSendToImageEditor;
        _onSendToCaptioning = onSendToCaptioning;
        _onDeleteRequested = onDeleteRequested;
        ShowRatingControls = showRatingControls;

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
            return;
        }

        if (index < 0 || index >= _allImages.Count) return;

        CurrentIndex = index;
        CurrentImage = _allImages[index];
        
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
            _allImages.CollectionChanged -= OnCollectionChanged;
        }

        _disposed = true;
    }
}
