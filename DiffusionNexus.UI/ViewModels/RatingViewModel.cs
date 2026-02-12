using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Sub-ViewModel managing image rating state and commands (Approved/Rejected/Unrated).
/// Extracted from <see cref="ImageEditorViewModel"/> to reduce its size.
/// </summary>
public partial class RatingViewModel : ObservableObject
{
    private readonly Func<bool> _hasImage;
    private readonly IDatasetEventAggregator? _eventAggregator;

    private DatasetImageViewModel? _selectedDatasetImage;

    public RatingViewModel(Func<bool> hasImage, IDatasetEventAggregator? eventAggregator)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        _hasImage = hasImage;
        _eventAggregator = eventAggregator;

        MarkApprovedCommand = new RelayCommand(ExecuteMarkApproved, () => _hasImage() && _selectedDatasetImage is not null);
        MarkRejectedCommand = new RelayCommand(ExecuteMarkRejected, () => _hasImage() && _selectedDatasetImage is not null);
        ClearRatingCommand = new RelayCommand(ExecuteClearRating, () => _hasImage() && _selectedDatasetImage is not null && !IsUnrated);
    }

    #region Properties

    /// <summary>The currently selected DatasetImageViewModel being edited.</summary>
    public DatasetImageViewModel? SelectedDatasetImage
    {
        get => _selectedDatasetImage;
        set
        {
            if (SetProperty(ref _selectedDatasetImage, value))
            {
                OnPropertyChanged(nameof(IsApproved));
                OnPropertyChanged(nameof(IsRejected));
                OnPropertyChanged(nameof(IsUnrated));
                OnPropertyChanged(nameof(HasRating));
                RefreshCommandStates();
            }
        }
    }

    /// <summary>Whether the current image is marked as approved/production-ready.</summary>
    public bool IsApproved => _selectedDatasetImage?.IsApproved ?? false;

    /// <summary>Whether the current image is marked as rejected/failed.</summary>
    public bool IsRejected => _selectedDatasetImage?.IsRejected ?? false;

    /// <summary>Whether the current image has not been rated yet.</summary>
    public bool IsUnrated => _selectedDatasetImage?.IsUnrated ?? true;

    /// <summary>Whether the current image has any rating (approved or rejected).</summary>
    public bool HasRating => !IsUnrated;

    #endregion

    #region Commands

    public IRelayCommand MarkApprovedCommand { get; }
    public IRelayCommand MarkRejectedCommand { get; }
    public IRelayCommand ClearRatingCommand { get; }

    #endregion

    #region Events

    /// <summary>Event raised when a status message should be shown.</summary>
    public event EventHandler<string?>? StatusMessageChanged;

    #endregion

    #region Public Methods

    /// <summary>Notifies all commands that their CanExecute state may have changed.</summary>
    public void RefreshCommandStates()
    {
        MarkApprovedCommand.NotifyCanExecuteChanged();
        MarkRejectedCommand.NotifyCanExecuteChanged();
        ClearRatingCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Refreshes the rating display properties after an external rating change.</summary>
    public void RefreshRatingDisplay()
    {
        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(IsRejected));
        OnPropertyChanged(nameof(IsUnrated));
        OnPropertyChanged(nameof(HasRating));
        RefreshCommandStates();
    }

    #endregion

    #region Command Implementations

    private void ExecuteMarkApproved()
    {
        if (_selectedDatasetImage is null) return;

        var previousRating = _selectedDatasetImage.RatingStatus;
        _selectedDatasetImage.RatingStatus = _selectedDatasetImage.IsApproved
            ? ImageRatingStatus.Unrated
            : ImageRatingStatus.Approved;
        _selectedDatasetImage.SaveRating();

        NotifyRatingProperties();

        _eventAggregator?.PublishImageRatingChanged(new ImageRatingChangedEventArgs
        {
            Image = _selectedDatasetImage,
            NewRating = _selectedDatasetImage.RatingStatus,
            PreviousRating = previousRating
        });

        StatusMessageChanged?.Invoke(this, _selectedDatasetImage.IsApproved ? "Marked as Ready" : "Rating cleared");
    }

    private void ExecuteMarkRejected()
    {
        if (_selectedDatasetImage is null) return;

        var previousRating = _selectedDatasetImage.RatingStatus;
        _selectedDatasetImage.RatingStatus = _selectedDatasetImage.IsRejected
            ? ImageRatingStatus.Unrated
            : ImageRatingStatus.Rejected;
        _selectedDatasetImage.SaveRating();

        NotifyRatingProperties();

        _eventAggregator?.PublishImageRatingChanged(new ImageRatingChangedEventArgs
        {
            Image = _selectedDatasetImage,
            NewRating = _selectedDatasetImage.RatingStatus,
            PreviousRating = previousRating
        });

        StatusMessageChanged?.Invoke(this, _selectedDatasetImage.IsRejected ? "Marked as Failed" : "Rating cleared");
    }

    private void ExecuteClearRating()
    {
        if (_selectedDatasetImage is null) return;

        var previousRating = _selectedDatasetImage.RatingStatus;
        _selectedDatasetImage.RatingStatus = ImageRatingStatus.Unrated;
        _selectedDatasetImage.SaveRating();

        NotifyRatingProperties();

        _eventAggregator?.PublishImageRatingChanged(new ImageRatingChangedEventArgs
        {
            Image = _selectedDatasetImage,
            NewRating = ImageRatingStatus.Unrated,
            PreviousRating = previousRating
        });

        StatusMessageChanged?.Invoke(this, "Rating cleared");
    }

    private void NotifyRatingProperties()
    {
        OnPropertyChanged(nameof(IsApproved));
        OnPropertyChanged(nameof(IsRejected));
        OnPropertyChanged(nameof(IsUnrated));
        OnPropertyChanged(nameof(HasRating));
        RefreshCommandStates();
    }

    #endregion
}
