using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel representing a single dataset folder as a card.
/// Displays folder name, image count, and thumbnail preview.
/// </summary>
public class DatasetCardViewModel : ObservableObject
{
    private string _name = string.Empty;
    private string _folderPath = string.Empty;
    private int _imageCount;
    private string? _thumbnailPath;
    private bool _isSelected;
    private int? _categoryId;
    private string? _categoryName;

    /// <summary>
    /// Name of the dataset (folder name).
    /// </summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>
    /// Full path to the dataset folder.
    /// </summary>
    public string FolderPath
    {
        get => _folderPath;
        set => SetProperty(ref _folderPath, value);
    }

    /// <summary>
    /// Number of images in this dataset.
    /// </summary>
    public int ImageCount
    {
        get => _imageCount;
        set
        {
            if (SetProperty(ref _imageCount, value))
            {
                OnPropertyChanged(nameof(ImageCountText));
            }
        }
    }

    /// <summary>
    /// Path to the first image for thumbnail preview.
    /// </summary>
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set
        {
            if (SetProperty(ref _thumbnailPath, value))
            {
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }
    }

    /// <summary>
    /// Whether this card is currently selected.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Category ID for this dataset.
    /// </summary>
    public int? CategoryId
    {
        get => _categoryId;
        set
        {
            if (SetProperty(ref _categoryId, value))
            {
                OnPropertyChanged(nameof(HasCategory));
                CategoryChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Category name for display.
    /// </summary>
    public string? CategoryName
    {
        get => _categoryName;
        set => SetProperty(ref _categoryName, value);
    }

    /// <summary>
    /// Whether this dataset has a category assigned.
    /// </summary>
    public bool HasCategory => _categoryId.HasValue;

    /// <summary>
    /// Display text showing image count.
    /// </summary>
    public string ImageCountText => _imageCount == 1 ? "1 image" : $"{_imageCount} images";

    /// <summary>
    /// Whether this dataset has a thumbnail to display.
    /// </summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(_thumbnailPath);

    /// <summary>
    /// Path to the metadata file for this dataset.
    /// </summary>
    public string MetadataFilePath => Path.Combine(_folderPath, ".dataset.json");

    /// <summary>
    /// Event raised when category changes.
    /// </summary>
    public event EventHandler? CategoryChanged;

    /// <summary>
    /// Creates a DatasetCardViewModel from a folder path.
    /// </summary>
    public static DatasetCardViewModel FromFolder(string folderPath)
    {
        var name = Path.GetFileName(folderPath) ?? folderPath;
        var imageExtensions = new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };
        
        var images = Directory.Exists(folderPath)
            ? Directory.EnumerateFiles(folderPath)
                .Where(f => imageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList()
            : [];

        var vm = new DatasetCardViewModel
        {
            Name = name,
            FolderPath = folderPath,
            ImageCount = images.Count,
            ThumbnailPath = images.FirstOrDefault()
        };

        // Load metadata if exists
        vm.LoadMetadata();

        return vm;
    }

    /// <summary>
    /// Loads metadata from the .dataset.json file.
    /// </summary>
    public void LoadMetadata()
    {
        if (!File.Exists(MetadataFilePath))
            return;

        try
        {
            var json = File.ReadAllText(MetadataFilePath);
            var metadata = System.Text.Json.JsonSerializer.Deserialize<DatasetMetadata>(json);
            if (metadata is not null)
            {
                CategoryId = metadata.CategoryId;
            }
        }
        catch
        {
            // Ignore errors reading metadata
        }
    }

    /// <summary>
    /// Saves metadata to the .dataset.json file.
    /// </summary>
    public void SaveMetadata()
    {
        try
        {
            var metadata = new DatasetMetadata
            {
                CategoryId = CategoryId
            };
            var json = System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(MetadataFilePath, json);
        }
        catch
        {
            // Ignore errors writing metadata
        }
    }
}

/// <summary>
/// Metadata stored in each dataset folder.
/// </summary>
public class DatasetMetadata
{
    /// <summary>
    /// Category ID for this dataset.
    /// </summary>
    public int? CategoryId { get; set; }
}
