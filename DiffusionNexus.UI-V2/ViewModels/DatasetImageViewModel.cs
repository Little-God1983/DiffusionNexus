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
    private string _originalCaption = string.Empty;
    
    private string _imagePath = string.Empty;
    private string _caption = string.Empty;
    private bool _hasUnsavedChanges;
    private bool _isSelected;

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

    public DatasetImageViewModel() : this(null, null)
    {
    }

    public DatasetImageViewModel(Action<DatasetImageViewModel>? onDeleteRequested, Action<DatasetImageViewModel>? onCaptionChanged)
    {
        _onDeleteRequested = onDeleteRequested;
        _onCaptionChanged = onCaptionChanged;
        
        SaveCaptionCommand = new RelayCommand(SaveCaption);
        RevertCaptionCommand = new RelayCommand(RevertCaption);
        DeleteCommand = new RelayCommand(Delete);
    }

    /// <summary>
    /// Creates a DatasetImageViewModel from an image file path.
    /// </summary>
    public static DatasetImageViewModel FromFile(
        string imagePath,
        Action<DatasetImageViewModel>? onDeleteRequested = null,
        Action<DatasetImageViewModel>? onCaptionChanged = null)
    {
        var vm = new DatasetImageViewModel(onDeleteRequested, onCaptionChanged)
        {
            ImagePath = imagePath
        };
        vm.LoadCaption();
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
}
