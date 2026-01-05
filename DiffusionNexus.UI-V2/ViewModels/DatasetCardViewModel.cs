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
    /// Display text showing image count.
    /// </summary>
    public string ImageCountText => _imageCount == 1 ? "1 image" : $"{_imageCount} images";

    /// <summary>
    /// Whether this dataset has a thumbnail to display.
    /// </summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(_thumbnailPath);

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

        return new DatasetCardViewModel
        {
            Name = name,
            FolderPath = folderPath,
            ImageCount = images.Count,
            ThumbnailPath = images.FirstOrDefault()
        };
    }
}
