using System.ComponentModel;
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

    public SaveAsDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

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
    /// Gets whether the filename already exists in the directory.
    /// </summary>
    public bool IsFilenameExists
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FileName))
                return false;
            
            var trimmedName = FileName.Trim();
            
            // Don't flag as "exists" if it's the original file
            if (string.Equals(trimmedName, OriginalFileName, StringComparison.OrdinalIgnoreCase))
                return false;
            
            return _existingFileNames.Contains(trimmedName);
        }
    }

    /// <summary>
    /// Gets whether there's any validation error to display.
    /// </summary>
    public bool HasValidationError => 
        IsFilenameUnchanged || 
        IsFilenameExists || 
        HasInvalidCharacters ||
        string.IsNullOrWhiteSpace(FileName);

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

            if (IsFilenameExists)
                return $"A file named '{FileName.Trim()}{FileExtension}' already exists in this folder.";
            
            if (IsFilenameUnchanged)
                return "Filename must be different from the original to save as a new file.";
            
            return string.Empty;
        }
    }

    /// <summary>
    /// Gets whether the Save button should be enabled.
    /// Disabled when filename is empty, unchanged from original, contains invalid chars, or already exists.
    /// </summary>
    public bool CanSave =>
        !string.IsNullOrWhiteSpace(FileName) &&
        !HasInvalidCharacters &&
        !IsFilenameUnchanged &&
        !IsFilenameExists;

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
        var fileName = Path.GetFileNameWithoutExtension(originalFilePath);
        var extension = Path.GetExtension(originalFilePath);
        var directory = Path.GetDirectoryName(originalFilePath) ?? string.Empty;

        OriginalFileName = fileName;
        FileName = fileName;
        FileExtension = extension;
        _directoryPath = directory;

        // Load all existing filenames in the directory (without extensions)
        LoadExistingFileNames(directory, extension);

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

    private void OnRatingChanged(object? sender, ImageRatingStatus newRating)
    {
        Rating = newRating;
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        Result = SaveAsResult.Success(FileName.Trim(), Rating);
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = SaveAsResult.Cancelled();
        Close(false);
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
