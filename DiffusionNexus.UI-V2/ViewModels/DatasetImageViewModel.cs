using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel representing a single image in a dataset with its editable caption.
/// </summary>
public class DatasetImageViewModel : ObservableObject
{
    private readonly Action<DatasetImageViewModel>? _onDeleteRequested;
    private readonly Action<DatasetImageViewModel>? _onCaptionChanged;
    private readonly Action<DatasetImageViewModel>? _onRatingChanged;
    private string _originalCaption = string.Empty;
    
    private string _imagePath = string.Empty;
    private string _caption = string.Empty;
    private bool _hasUnsavedChanges;
    private bool _isSelected;
    private ImageRatingStatus _ratingStatus = ImageRatingStatus.Unrated;

    /// <summary>
    /// Full path to the image file.
    /// </summary>
    public string ImagePath
    {
        get => _imagePath;
        set => SetProperty(ref _imagePath, value);
    }

    /// <summary>
    /// Caption text (loaded from .txt file with same name).
    /// </summary>
    public string Caption
    {
        get => _caption;
        set
        {
            if (SetProperty(ref _caption, value))
            {
                HasUnsavedChanges = value != _originalCaption;
                _onCaptionChanged?.Invoke(this);
            }
        }
    }

    /// <summary>
    /// Whether the caption has been modified.
    /// </summary>
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        set => SetProperty(ref _hasUnsavedChanges, value);
    }

    /// <summary>
    /// Whether this image is currently selected.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// The quality rating status of this image.
    /// </summary>
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
                _onRatingChanged?.Invoke(this);
            }
        }
    }

    /// <summary>
    /// Whether the image is marked as approved/production-ready.
    /// </summary>
    public bool IsApproved => _ratingStatus == ImageRatingStatus.Approved;

    /// <summary>
    /// Whether the image is marked as rejected/failed.
    /// </summary>
    public bool IsRejected => _ratingStatus == ImageRatingStatus.Rejected;

    /// <summary>
    /// Whether the image has not been rated yet.
    /// </summary>
    public bool IsUnrated => _ratingStatus == ImageRatingStatus.Unrated;

    /// <summary>
    /// File name without extension for display.
    /// </summary>
    public string FileName => Path.GetFileNameWithoutExtension(_imagePath);

    /// <summary>
    /// Full file name with extension.
    /// </summary>
    public string FullFileName => Path.GetFileName(_imagePath);

    /// <summary>
    /// Path to the caption text file.
    /// </summary>
    public string CaptionFilePath => Path.ChangeExtension(_imagePath, ".txt");

    /// <summary>
    /// Path to the rating metadata file.
    /// </summary>
    public string RatingFilePath => Path.ChangeExtension(_imagePath, ".rating");

    /// <summary>
    /// Command to save the caption.
    /// </summary>
    public IRelayCommand SaveCaptionCommand { get; }

    /// <summary>
    /// Command to revert caption to last saved state.
    /// </summary>
    public IRelayCommand RevertCaptionCommand { get; }

    /// <summary>
    /// Command to delete this image.
    /// </summary>
    public IRelayCommand DeleteCommand { get; }

    /// <summary>
    /// Command to mark the image as approved (production-ready).
    /// </summary>
    public IRelayCommand MarkApprovedCommand { get; }

    /// <summary>
    /// Command to mark the image as rejected (failed).
    /// </summary>
    public IRelayCommand MarkRejectedCommand { get; }

    /// <summary>
    /// Command to clear the rating (set to unrated).
    /// </summary>
    public IRelayCommand ClearRatingCommand { get; }

    public DatasetImageViewModel() : this(null, null, null)
    {
    }

    public DatasetImageViewModel(
        Action<DatasetImageViewModel>? onDeleteRequested, 
        Action<DatasetImageViewModel>? onCaptionChanged,
        Action<DatasetImageViewModel>? onRatingChanged = null)
    {
        _onDeleteRequested = onDeleteRequested;
        _onCaptionChanged = onCaptionChanged;
        _onRatingChanged = onRatingChanged;
        
        SaveCaptionCommand = new RelayCommand(SaveCaption);
        RevertCaptionCommand = new RelayCommand(RevertCaption);
        DeleteCommand = new RelayCommand(Delete);
        MarkApprovedCommand = new RelayCommand(MarkApproved);
        MarkRejectedCommand = new RelayCommand(MarkRejected);
        ClearRatingCommand = new RelayCommand(ClearRating);
    }

    /// <summary>
    /// Creates a DatasetImageViewModel from an image file path.
    /// </summary>
    public static DatasetImageViewModel FromFile(
        string imagePath,
        Action<DatasetImageViewModel>? onDeleteRequested = null,
        Action<DatasetImageViewModel>? onCaptionChanged = null,
        Action<DatasetImageViewModel>? onRatingChanged = null)
    {
        var vm = new DatasetImageViewModel(onDeleteRequested, onCaptionChanged, onRatingChanged)
        {
            ImagePath = imagePath
        };
        vm.LoadCaption();
        vm.LoadRating();
        return vm;
    }

    /// <summary>
    /// Loads the caption from the associated .txt file.
    /// </summary>
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
            catch
            {
                _caption = string.Empty;
                _originalCaption = string.Empty;
            }
        }
        else
        {
            _caption = string.Empty;
            _originalCaption = string.Empty;
        }
        HasUnsavedChanges = false;
    }

    /// <summary>
    /// Loads the rating from the associated .rating file.
    /// </summary>
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
            catch
            {
                _ratingStatus = ImageRatingStatus.Unrated;
            }
        }
        else
        {
            _ratingStatus = ImageRatingStatus.Unrated;
        }
    }

    /// <summary>
    /// Saves the rating to the associated .rating file.
    /// </summary>
    public void SaveRating()
    {
        try
        {
            if (_ratingStatus == ImageRatingStatus.Unrated)
            {
                // Delete the file if unrated
                if (File.Exists(RatingFilePath))
                {
                    File.Delete(RatingFilePath);
                }
            }
            else
            {
                File.WriteAllText(RatingFilePath, _ratingStatus.ToString());
            }
        }
        catch
        {
            // Ignore errors
        }
    }

    private void SaveCaption()
    {
        try
        {
            File.WriteAllText(CaptionFilePath, _caption);
            _originalCaption = _caption;
            HasUnsavedChanges = false;
        }
        catch
        {
            // TODO: Handle error
        }
    }

    private void RevertCaption()
    {
        Caption = _originalCaption;
        HasUnsavedChanges = false;
    }

    private void Delete()
    {
        _onDeleteRequested?.Invoke(this);
    }

    private void MarkApproved()
    {
        // Toggle: if already approved, clear it
        RatingStatus = _ratingStatus == ImageRatingStatus.Approved 
            ? ImageRatingStatus.Unrated 
            : ImageRatingStatus.Approved;
        SaveRating();
    }

    private void MarkRejected()
    {
        // Toggle: if already rejected, clear it
        RatingStatus = _ratingStatus == ImageRatingStatus.Rejected 
            ? ImageRatingStatus.Unrated 
            : ImageRatingStatus.Rejected;
        SaveRating();
    }

    private void ClearRating()
    {
        RatingStatus = ImageRatingStatus.Unrated;
        SaveRating();
    }
}
