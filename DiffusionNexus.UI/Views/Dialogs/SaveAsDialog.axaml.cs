using System.ComponentModel;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog for saving an image with a new name and optional rating.
/// </summary>
public partial class SaveAsDialog : Window, INotifyPropertyChanged
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    
    private string _fileName = string.Empty;
    private string _originalFileName = string.Empty;
    private string _fileExtension = string.Empty;
    private string _directoryPath = string.Empty;
    private HashSet<string> _existingFileNames = new(StringComparer.OrdinalIgnoreCase);
    private ImageRatingStatus _rating = ImageRatingStatus.Unrated;
    
    private SaveAsDestination _destination = SaveAsDestination.OriginFolder;
    private DatasetCardViewModel? _selectedDataset;
    private int? _selectedVersion;

    public SaveAsDialog()
    {
        FileLogger.Log("SaveAsDialog constructor called");
        InitializeComponent();
        DataContext = this;
        
        this.Opened += (s, e) => FileLogger.Log("SaveAsDialog Opened event fired");
        this.Closing += (s, e) => FileLogger.Log($"SaveAsDialog Closing event fired (Cancel={e.Cancel})");
        this.Closed += (s, e) => FileLogger.Log("SaveAsDialog Closed event fired");
        
        FileLogger.Log("SaveAsDialog constructor completed");
    }

    private void InitializeComponent()
    {
        FileLogger.Log("SaveAsDialog.InitializeComponent called");
        AvaloniaXamlLoader.Load(this);
        FileLogger.Log("SaveAsDialog.InitializeComponent completed");
    }

    public string OriginFolderPath => _directoryPath;

    public bool IsExistingDatasetEnabled => AvailableDatasets.Count > 0;

    /// <summary>
    /// Gets or sets the filename (without extension).

    /// </summary>
    public string FileName
    {
        get => _fileName;
        set
        {
            if (_fileName != value)
            {
                _fileName = value;
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(IsFilenameUnchanged));
                OnPropertyChanged(nameof(IsFilenameExists));
                OnPropertyChanged(nameof(HasInvalidCharacters));
                OnPropertyChanged(nameof(InvalidCharactersFound));
                OnPropertyChanged(nameof(ValidationMessage));
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    /// <summary>
    /// Gets or sets the original filename (without extension).
    /// Used to determine if the name has changed.
    /// </summary>
    public string OriginalFileName
    {
        get => _originalFileName;
        set
        {
            _originalFileName = value;
            OnPropertyChanged(nameof(OriginalFileName));
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(IsFilenameUnchanged));
            OnPropertyChanged(nameof(IsFilenameExists));
            OnPropertyChanged(nameof(ValidationMessage));
            OnPropertyChanged(nameof(HasValidationError));
        }
    }

    /// <summary>
    /// Gets or sets the file extension (e.g., ".png").
    /// </summary>
    public string FileExtension
    {
        get => _fileExtension;
        set
        {
            _fileExtension = value;
            OnPropertyChanged(nameof(FileExtension));
        }
    }

    /// <summary>
    /// Gets or sets the selected rating.
    /// </summary>
    public ImageRatingStatus Rating
    {
        get => _rating;
        set
        {
            if (_rating != value)
            {
                _rating = value;
                OnPropertyChanged(nameof(Rating));
            }
        }
    }

    /// <summary>
    /// Gets whether the filename is unchanged from the original.
    /// </summary>
    public bool IsFilenameUnchanged =>
        !string.IsNullOrWhiteSpace(FileName) &&
        string.Equals(FileName.Trim(), OriginalFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether the filename contains invalid characters.
    /// </summary>
    public bool HasInvalidCharacters =>
        !string.IsNullOrWhiteSpace(FileName) &&
        FileName.IndexOfAny(InvalidFileNameChars) >= 0;

    /// <summary>
    /// Gets the invalid characters found in the filename.
    /// </summary>
    public string InvalidCharactersFound
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FileName))
                return string.Empty;

            var invalidChars = FileName
                .Where(c => InvalidFileNameChars.Contains(c))
                .Distinct()
                .Select(c => c switch
                {
                    '<' => "<",
                    '>' => ">",
                    ':' => ":",
                    '"' => "\"",
                    '/' => "/",
                    '\\' => "\\",
                    '|' => "|",
                    '?' => "?",
                    '*' => "*",
                    _ when char.IsControl(c) => $"[control char]",
                    _ => c.ToString()
                });

            return string.Join(" ", invalidChars);
        }
    }

    /// <summary>
    /// Gets whether the filename already exists in the target directory.
    /// </summary>
    public bool IsFilenameExists
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FileName))
                return false;
            
            var trimmedName = FileName.Trim();
            
            // When saving to the origin folder, don't flag the original file as "exists"
            if (IsOriginSelected &&
                string.Equals(trimmedName, OriginalFileName, StringComparison.OrdinalIgnoreCase))
                return false;
            
            return _existingFileNames.Contains(trimmedName);
        }
    }

    /// <summary>
    /// Gets whether there's any validation error to display.
    /// </summary>
    public bool HasValidationError => 
        (IsOriginSelected && (IsFilenameUnchanged || IsFilenameExists || HasInvalidCharacters || string.IsNullOrWhiteSpace(FileName))) ||
        (IsDatasetSelected && (SelectedDataset == null || SelectedVersion == null || string.IsNullOrWhiteSpace(FileName) || HasInvalidCharacters || IsFilenameExists));

    /// <summary>
    /// Gets the validation message to display.
    /// </summary>
    public string ValidationMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FileName))
                return "Please enter a filename.";

            if (HasInvalidCharacters)
                return $"Filename contains invalid characters: {InvalidCharactersFound}";

            if (IsOriginSelected)
            {
                if (IsFilenameExists)
                    return $"A file named '{FileName.Trim()}{FileExtension}' already exists in this folder.";
            
                if (IsFilenameUnchanged)
                    return "Filename must be different from the original to save as a new file.";
            }
            else if (IsDatasetSelected)
            {
                if (SelectedDataset == null)
                    return "Please select a destination dataset.";
                if (SelectedVersion == null)
                    return "Please select a version.";
                if (IsFilenameExists)
                    return $"A file named '{FileName.Trim()}{FileExtension}' already exists in the selected dataset.";
            }
            
            return string.Empty;
        }
    }

    /// <summary>
    /// Gets whether the Save button should be enabled.
    /// </summary>
    public bool CanSave =>
        !string.IsNullOrWhiteSpace(FileName) &&
        !HasInvalidCharacters &&
        !IsFilenameExists &&
        (IsOriginSelected ? !IsFilenameUnchanged : (SelectedDataset != null && SelectedVersion != null));


    /// <summary>
    /// Gets the result after dialog closes.
    /// </summary>
    public SaveAsResult? Result { get; private set; }

    /// <summary>
    /// Configures the dialog with the original filename.
    /// </summary>
    /// <param name="originalFilePath">Full path to the original file.</param>
    /// <returns>This dialog for fluent configuration.</returns>
    public SaveAsDialog WithOriginalFile(string originalFilePath)
    {
        FileLogger.LogEntry($"originalFilePath={originalFilePath}");
        
        try
        {
            var fileName = Path.GetFileNameWithoutExtension(originalFilePath);
            var extension = Path.GetExtension(originalFilePath);
            var directory = Path.GetDirectoryName(originalFilePath) ?? string.Empty;
            
            FileLogger.Log($"Parsed: fileName={fileName}, extension={extension}, directory={directory}");

            OriginalFileName = fileName;
            FileName = fileName;
            FileExtension = extension;
            _directoryPath = directory;

            // Load all existing filenames in the directory (without extensions)
            FileLogger.Log("Loading existing file names...");
            LoadExistingFileNames(directory, extension);
            FileLogger.Log($"Loaded {_existingFileNames.Count} existing file names");
            
            FileLogger.LogExit();
            return this;
        }
        catch (Exception ex)
        {
            FileLogger.LogError("Exception in WithOriginalFile", ex);
            throw;
        }
    }

    /// <summary>
    /// Configures the dialog with available datasets.
    /// </summary>
    public SaveAsDialog WithDatasets(IEnumerable<DatasetCardViewModel> availableDatasets)
    {
        AvailableDatasets.Clear();
        foreach (var dataset in availableDatasets)
        {
            AvailableDatasets.Add(dataset);
        }

        if (AvailableDatasets.Count > 0)
        {
            SelectedDataset = AvailableDatasets[0];
            Destination = SaveAsDestination.ExistingDataset;
        }
        else
        {
            Destination = SaveAsDestination.OriginFolder;
        }

        OnPropertyChanged(nameof(IsExistingDatasetEnabled));
        return this;
    }

    /// <summary>
    /// Pre-selects a specific dataset and version in the dialog.
    /// Must be called after <see cref="WithDatasets"/>.
    /// </summary>
    /// <param name="datasetName">The name of the dataset to pre-select.</param>
    /// <param name="version">The version number to pre-select, or null for the latest.</param>
    public SaveAsDialog WithPreselectedDataset(string? datasetName, int? version)
    {
        if (string.IsNullOrWhiteSpace(datasetName))
            return this;

        var match = AvailableDatasets.FirstOrDefault(
            d => string.Equals(d.Name, datasetName, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            SelectedDataset = match;

            if (version.HasValue && AvailableVersions.Contains(version.Value))
            {
                SelectedVersion = version.Value;
            }
        }

        return this;
    }






    /// <summary>
    /// Loads all existing filenames in the directory that have the same extension.
    /// </summary>
    private void LoadExistingFileNames(string directory, string extension)
    {
        _existingFileNames.Clear();

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        try
        {
            // Get all files with the same extension
            var pattern = $"*{extension}";
            var files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);

            foreach (var file in files)
            {
                var nameWithoutExt = Path.GetFileNameWithoutExtension(file);
                _existingFileNames.Add(nameWithoutExt);
            }
        }
        catch (IOException)
        {
            // Directory access issues - proceed without validation
        }
        catch (UnauthorizedAccessException)
        {
            // Permission issues - proceed without validation
        }
    }

    /// <summary>
    /// Reloads the existing file name set from the currently targeted directory
    /// (origin folder or selected dataset version folder).
    /// </summary>
    private void ReloadExistingFileNames()
    {
        if (IsDatasetSelected && _selectedDataset is not null && _selectedVersion is not null)
        {
            var targetDir = _selectedDataset.IsVersionedStructure
                ? _selectedDataset.GetVersionFolderPath(_selectedVersion.Value)
                : _selectedDataset.FolderPath;
            LoadExistingFileNames(targetDir, _fileExtension);
        }
        else
        {
            LoadExistingFileNames(_directoryPath, _fileExtension);
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (IsOriginSelected)
        {
            Result = SaveAsResult.Success(FileName.Trim(), Rating);
        }
        else
        {
            Result = SaveAsResult.SuccessToDataset(FileName.Trim(), Rating, SelectedDataset!, SelectedVersion);
        }
        
        FileLogger.Log($"OnSaveClick: Closing dialog with result: {Result}");
        Close(Result);
    }


    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = SaveAsResult.Cancelled();
        FileLogger.Log("OnCancelClick: Closing dialog with Cancelled result");
        Close(Result);
    }

    public ObservableCollection<DatasetCardViewModel> AvailableDatasets { get; } = [];
    public ObservableCollection<int> AvailableVersions { get; } = [];



    /// <summary>
    /// Gets or sets the save destination.
    /// </summary>
    public SaveAsDestination Destination
    {
        get => _destination;
        set
        {
            if (_destination != value)
            {
                _destination = value;
                ReloadExistingFileNames();
                OnPropertyChanged(nameof(Destination));
                OnPropertyChanged(nameof(IsOriginSelected));
                OnPropertyChanged(nameof(IsDatasetSelected));
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(IsFilenameExists));
                OnPropertyChanged(nameof(ValidationMessage));
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public bool IsOriginSelected
    {
        get => Destination == SaveAsDestination.OriginFolder;
        set { if (value) Destination = SaveAsDestination.OriginFolder; }
    }

    public bool IsDatasetSelected
    {
        get => Destination == SaveAsDestination.ExistingDataset;
        set { if (value) Destination = SaveAsDestination.ExistingDataset; }
    }

    public DatasetCardViewModel? SelectedDataset
    {
        get => _selectedDataset;
        set
        {
            if (_selectedDataset != value)
            {
                _selectedDataset = value;
                OnPropertyChanged(nameof(SelectedDataset));
                UpdateAvailableVersions();
                ReloadExistingFileNames();
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(IsFilenameExists));
                OnPropertyChanged(nameof(ValidationMessage));
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public int? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (_selectedVersion != value)
            {
                _selectedVersion = value;
                OnPropertyChanged(nameof(SelectedVersion));
                ReloadExistingFileNames();
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(IsFilenameExists));
                OnPropertyChanged(nameof(ValidationMessage));
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    private void UpdateAvailableVersions()
    {
        AvailableVersions.Clear();
        if (_selectedDataset != null)
        {
            var versions = _selectedDataset.GetAllVersionNumbers();
            foreach (var version in versions)
            {
                AvailableVersions.Add(version);
            }
            SelectedVersion = AvailableVersions.LastOrDefault();
        }
        else
        {
            SelectedVersion = null;
        }
    }


    public new event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
