using System.Collections.ObjectModel;
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
public partial class ImageViewerViewModel : ObservableObject
{
    private readonly ObservableCollection<DatasetImageViewModel> _allImages;
    private readonly IDatasetEventAggregator? _eventAggregator;
    private readonly Action<DatasetImageViewModel>? _onSendToImageEditor;
    private readonly Action<DatasetImageViewModel>? _onDeleteRequested;
    
    private DatasetImageViewModel? _currentImage;
    private int _currentIndex;

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
    public string PositionText => $"{CurrentIndex + 1} / {TotalCount}";
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

    #region Commands

    public IRelayCommand PreviousCommand { get; }
    public IRelayCommand NextCommand { get; }
    public IRelayCommand CloseCommand { get; }
    public IRelayCommand MarkApprovedCommand { get; }
    public IRelayCommand MarkRejectedCommand { get; }
    public IRelayCommand ClearRatingCommand { get; }
    public IRelayCommand SendToImageEditorCommand { get; }
    public IRelayCommand DeleteCommand { get; }

    #endregion

    /// <summary>
    /// Creates a new ImageViewerViewModel with event aggregator support.
    /// </summary>
    /// <param name="images">The collection of all images in the dataset.</param>
    /// <param name="startIndex">The index of the image to display initially.</param>
    /// <param name="eventAggregator">Optional event aggregator for publishing events.</param>
    /// <param name="onSendToImageEditor">Callback when "Send to Image Editor" is requested.</param>
    /// <param name="onDeleteRequested">Callback when delete is requested.</param>
    public ImageViewerViewModel(
        ObservableCollection<DatasetImageViewModel> images,
        int startIndex,
        IDatasetEventAggregator? eventAggregator = null,
        Action<DatasetImageViewModel>? onSendToImageEditor = null,
        Action<DatasetImageViewModel>? onDeleteRequested = null)
    {
        _allImages = images ?? throw new ArgumentNullException(nameof(images));
        _eventAggregator = eventAggregator;
        _onSendToImageEditor = onSendToImageEditor;
        _onDeleteRequested = onDeleteRequested;

        PreviousCommand = new RelayCommand(GoPrevious, () => CanGoPrevious);
        NextCommand = new RelayCommand(GoNext, () => CanGoNext);
        CloseCommand = new RelayCommand(Close);
        MarkApprovedCommand = new RelayCommand(MarkApproved);
        MarkRejectedCommand = new RelayCommand(MarkRejected);
        ClearRatingCommand = new RelayCommand(ClearRating);
        SendToImageEditorCommand = new RelayCommand(SendToImageEditor);
        DeleteCommand = new RelayCommand(Delete);

        NavigateTo(Math.Clamp(startIndex, 0, Math.Max(0, images.Count - 1)));
    }

    /// <summary>Design-time constructor.</summary>
    public ImageViewerViewModel() : this([], 0)
    {
    }

    /// <summary>Navigates to a specific index in the collection.</summary>
    public void NavigateTo(int index)
    {
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

    private void Delete()
    {
        if (_currentImage is null) return;
        
        var imageToDelete = _currentImage;
        var deletedIndex = CurrentIndex;
        
        if (CanGoNext)
        {
            var nextIndex = CurrentIndex;
            _onDeleteRequested?.Invoke(imageToDelete);
            if (_allImages.Count > 0)
                NavigateTo(Math.Min(nextIndex, _allImages.Count - 1));
            else
                Close();
        }
        else if (CanGoPrevious)
        {
            GoPrevious();
            _onDeleteRequested?.Invoke(imageToDelete);
        }
        else
        {
            _onDeleteRequested?.Invoke(imageToDelete);
            Close();
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
}
