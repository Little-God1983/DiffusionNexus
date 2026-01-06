using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel representing a single dataset folder as a card.
/// Displays folder name, image count, and thumbnail preview.
/// 
/// Folder structure:
/// - Legacy: DatasetName/images+captions (flat structure)
/// - Versioned: DatasetName/.dataset/config.json + DatasetName/V1/images+captions
/// </summary>
public class DatasetCardViewModel : ObservableObject
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];
    
    private string _name = string.Empty;
    private string _folderPath = string.Empty;
    private int _imageCount;
    private string? _thumbnailPath;
    private bool _isSelected;
    private int? _categoryId;
    private string? _categoryName;
    private int _currentVersion = 1;
    private int _totalVersions = 1;
    private bool _isVersionedStructure;
    private int? _displayVersion;
    private Dictionary<int, int> _versionBranchedFrom = new();

    /// <summary>
    /// Name of the dataset (folder name).
    /// </summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>
    /// Full path to the dataset folder (root).
    /// </summary>
    public string FolderPath
    {
        get => _folderPath;
        set => SetProperty(ref _folderPath, value);
    }

    /// <summary>
    /// Number of images in the current version.
    /// </summary>
    public int ImageCount
    {
        get => _imageCount;
        set
        {
            if (SetProperty(ref _imageCount, value))
            {
                OnPropertyChanged(nameof(ImageCountText));
                OnPropertyChanged(nameof(CanIncrementVersion));
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
    /// Current active version number.
    /// </summary>
    public int CurrentVersion
    {
        get => _currentVersion;
        set
        {
            if (SetProperty(ref _currentVersion, value))
            {
                OnPropertyChanged(nameof(VersionDisplayText));
                OnPropertyChanged(nameof(CurrentVersionFolderPath));
                OnPropertyChanged(nameof(VersionBadgeText));
            }
        }
    }

    /// <summary>
    /// Total number of versions available.
    /// </summary>
    public int TotalVersions
    {
        get => _totalVersions;
        set
        {
            if (SetProperty(ref _totalVersions, value))
            {
                OnPropertyChanged(nameof(VersionDisplayText));
                OnPropertyChanged(nameof(HasMultipleVersions));
                OnPropertyChanged(nameof(VersionBadgeText));
                OnPropertyChanged(nameof(ShowVersionBadge));
            }
        }
    }

    /// <summary>
    /// Whether this dataset uses the versioned folder structure.
    /// </summary>
    public bool IsVersionedStructure
    {
        get => _isVersionedStructure;
        set => SetProperty(ref _isVersionedStructure, value);
    }

    /// <summary>
    /// When set, this card represents a specific version in flattened view.
    /// When null, this is a normal dataset card (collapsed view).
    /// </summary>
    public int? DisplayVersion
    {
        get => _displayVersion;
        set
        {
            if (SetProperty(ref _displayVersion, value))
            {
                OnPropertyChanged(nameof(IsVersionCard));
                OnPropertyChanged(nameof(VersionBadgeText));
                OnPropertyChanged(nameof(ShowVersionBadge));
            }
        }
    }

    /// <summary>
    /// Tracks which version each version was branched from.
    /// </summary>
    public Dictionary<int, int> VersionBranchedFrom
    {
        get => _versionBranchedFrom;
        set => SetProperty(ref _versionBranchedFrom, value);
    }

    /// <summary>
    /// Whether this card represents a specific version (flattened view).
    /// </summary>
    public bool IsVersionCard => _displayVersion.HasValue;

    /// <summary>
    /// Whether this dataset has a category assigned.
    /// </summary>
    public bool HasCategory => _categoryId.HasValue;

    /// <summary>
    /// Display text showing image count.
    /// </summary>
    public string ImageCountText => _imageCount == 1 ? "1 image" : $"{_imageCount} images";

    /// <summary>
    /// Display text for version (e.g., "V1" or "V2 of 3").
    /// </summary>
    public string VersionDisplayText => _totalVersions > 1 
        ? $"V{_currentVersion} of {_totalVersions}" 
        : $"V{_currentVersion}";

    /// <summary>
    /// Badge text for card display.
    /// In flattened view: shows "V1", "V2", etc.
    /// In collapsed view: shows "3 Versions" for multi-version datasets.
    /// </summary>
    public string VersionBadgeText => _displayVersion.HasValue 
        ? $"V{_displayVersion}" 
        : (_totalVersions > 1 ? $"{_totalVersions} Versions" : string.Empty);

    /// <summary>
    /// Whether to show the version badge on the card.
    /// </summary>
    public bool ShowVersionBadge => _displayVersion.HasValue || _totalVersions > 1;

    /// <summary>
    /// Whether there are multiple versions.
    /// </summary>
    public bool HasMultipleVersions => _totalVersions > 1;

    /// <summary>
    /// Whether this dataset has images and can have its version incremented.
    /// </summary>
    public bool CanIncrementVersion => _imageCount > 0;

    /// <summary>
    /// Whether this dataset has a thumbnail to display.
    /// </summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(_thumbnailPath);

    /// <summary>
    /// Path to the .dataset config folder.
    /// </summary>
    public string ConfigFolderPath => Path.Combine(_folderPath, ".dataset");

    /// <summary>
    /// Path to the metadata file for this dataset.
    /// For versioned: DatasetName/.dataset/config.json
    /// For legacy: DatasetName/.dataset.json (will be migrated)
    /// </summary>
    public string MetadataFilePath => Path.Combine(ConfigFolderPath, "config.json");

    /// <summary>
    /// Legacy metadata file path (for migration).
    /// </summary>
    public string LegacyMetadataFilePath => Path.Combine(_folderPath, ".dataset.json");

    /// <summary>
    /// Path to the current version's image folder.
    /// For versioned: DatasetName/V1, DatasetName/V2, etc.
    /// For legacy (no versions yet): DatasetName/ (root folder)
    /// </summary>
    public string CurrentVersionFolderPath => _isVersionedStructure 
        ? Path.Combine(_folderPath, $"V{_currentVersion}") 
        : _folderPath;

    /// <summary>
    /// Event raised when category changes.
    /// </summary>
    public event EventHandler? CategoryChanged;

    /// <summary>
    /// Creates a DatasetCardViewModel from a folder path.
    /// Detects whether it's a legacy or versioned structure.
    /// </summary>
    public static DatasetCardViewModel FromFolder(string folderPath)
    {
        var name = Path.GetFileName(folderPath) ?? folderPath;
        
        var vm = new DatasetCardViewModel
        {
            Name = name,
            FolderPath = folderPath
        };

        // Load metadata (will detect and migrate legacy structure if needed)
        vm.LoadMetadata();
        
        // Detect folder structure and load images
        vm.DetectAndLoadImages();

        return vm;
    }

    /// <summary>
    /// Creates a version-specific card for the flattened view.
    /// </summary>
    /// <param name="version">The version number to display.</param>
    /// <returns>A new card representing this specific version.</returns>
    public DatasetCardViewModel CreateVersionCard(int version)
    {
        var versionPath = GetVersionFolderPath(version);
        
        var card = new DatasetCardViewModel
        {
            Name = Name,
            FolderPath = FolderPath,
            CategoryId = CategoryId,
            CategoryName = CategoryName,
            IsVersionedStructure = true,
            CurrentVersion = version,
            TotalVersions = TotalVersions,
            DisplayVersion = version
        };

        // Load images from this specific version folder
        if (Directory.Exists(versionPath))
        {
            var images = Directory.EnumerateFiles(versionPath)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            card.ImageCount = images.Count;
            card.ThumbnailPath = images.FirstOrDefault();
        }

        return card;
    }

    /// <summary>
    /// Gets all version numbers for this dataset.
    /// </summary>
    public List<int> GetAllVersionNumbers()
    {
        if (!Directory.Exists(_folderPath))
            return [1];

        var versionFolders = Directory.GetDirectories(_folderPath)
            .Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^V\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Select(d => int.Parse(Path.GetFileName(d).Substring(1)))
            .OrderBy(v => v)
            .ToList();

        return versionFolders.Count > 0 ? versionFolders : [1];
    }

    /// <summary>
    /// Detects the folder structure (versioned or legacy) and loads image info.
    /// </summary>
    private void DetectAndLoadImages()
    {
        if (!Directory.Exists(_folderPath))
        {
            ImageCount = 0;
            ThumbnailPath = null;
            return;
        }

        // Check for versioned structure (V1, V2, etc. folders)
        var versionFolders = Directory.GetDirectories(_folderPath)
            .Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^V\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .OrderBy(d => int.Parse(Path.GetFileName(d).Substring(1)))
            .ToList();

        if (versionFolders.Count > 0)
        {
            // Versioned structure
            IsVersionedStructure = true;
            TotalVersions = versionFolders.Count;
            
            // If current version from metadata doesn't exist, use highest version
            var maxVersion = versionFolders.Max(d => int.Parse(Path.GetFileName(d).Substring(1)));
            if (CurrentVersion > maxVersion || CurrentVersion < 1)
            {
                CurrentVersion = maxVersion;
            }
        }
        else
        {
            // Legacy or empty structure - check if there are images in root
            IsVersionedStructure = false;
            TotalVersions = 1;
            CurrentVersion = 1;
        }

        // Load images from current version folder
        var imagesPath = CurrentVersionFolderPath;
        if (Directory.Exists(imagesPath))
        {
            var images = Directory.EnumerateFiles(imagesPath)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            ImageCount = images.Count;
            ThumbnailPath = images.FirstOrDefault();
        }
        else
        {
            ImageCount = 0;
            ThumbnailPath = null;
        }
    }

    /// <summary>
    /// Loads metadata from the config file.
    /// Handles migration from legacy .dataset.json to .dataset/config.json.
    /// </summary>
    public void LoadMetadata()
    {
        // Check for versioned config first
        if (File.Exists(MetadataFilePath))
        {
            LoadMetadataFromFile(MetadataFilePath);
            return;
        }

        // Check for legacy config and migrate
        if (File.Exists(LegacyMetadataFilePath))
        {
            LoadMetadataFromFile(LegacyMetadataFilePath);
            // Note: Migration to new location happens on next save
            return;
        }

        // No metadata - defaults
        CategoryId = null;
        CurrentVersion = 1;
    }

    private void LoadMetadataFromFile(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            var metadata = System.Text.Json.JsonSerializer.Deserialize<DatasetMetadata>(json);
            if (metadata is not null)
            {
                CategoryId = metadata.CategoryId;
                CurrentVersion = metadata.CurrentVersion > 0 ? metadata.CurrentVersion : 1;
                VersionBranchedFrom = metadata.VersionBranchedFrom ?? new();
            }
        }
        catch
        {
            // Ignore errors reading metadata
        }
    }

    /// <summary>
    /// Saves metadata to the .dataset/config.json file.
    /// Creates the .dataset folder if it doesn't exist.
    /// </summary>
    public void SaveMetadata()
    {
        try
        {
            // Ensure .dataset folder exists
            Directory.CreateDirectory(ConfigFolderPath);

            var metadata = new DatasetMetadata
            {
                CategoryId = CategoryId,
                CurrentVersion = CurrentVersion,
                VersionBranchedFrom = VersionBranchedFrom
            };
            var json = System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(MetadataFilePath, json);

            // Clean up legacy file if it exists
            if (File.Exists(LegacyMetadataFilePath))
            {
                try { File.Delete(LegacyMetadataFilePath); } catch { }
            }
        }
        catch
        {
            // Ignore errors writing metadata
        }
    }

    /// <summary>
    /// Records that a version was branched from another version.
    /// </summary>
    /// <param name="newVersion">The new version number.</param>
    /// <param name="branchedFromVersion">The version it was branched from.</param>
    public void RecordBranch(int newVersion, int branchedFromVersion)
    {
        VersionBranchedFrom[newVersion] = branchedFromVersion;
    }

    /// <summary>
    /// Gets the version that a specific version was branched from.
    /// </summary>
    /// <param name="version">The version to check.</param>
    /// <returns>The parent version, or null if not tracked (V1 or legacy).</returns>
    public int? GetBranchedFrom(int version)
    {
        return VersionBranchedFrom.TryGetValue(version, out var parent) ? parent : null;
    }

    /// <summary>
    /// Gets the folder path for a specific version.
    /// </summary>
    public string GetVersionFolderPath(int version) => Path.Combine(_folderPath, $"V{version}");

    /// <summary>
    /// Gets the next version number.
    /// </summary>
    public int GetNextVersionNumber()
    {
        if (!Directory.Exists(_folderPath))
            return 1;

        var versionFolders = Directory.GetDirectories(_folderPath)
            .Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^V\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();

        if (versionFolders.Count == 0)
            return _isVersionedStructure ? 2 : 1;

        var maxVersion = versionFolders.Max(d => int.Parse(Path.GetFileName(d).Substring(1)));
        return maxVersion + 1;
    }

    /// <summary>
    /// Refreshes the image count and thumbnail from the current version folder.
    /// </summary>
    public void RefreshImageInfo()
    {
        DetectAndLoadImages();
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

    /// <summary>
    /// Current active version number (1-based). Default is 1.
    /// </summary>
    public int CurrentVersion { get; set; } = 1;

    /// <summary>
    /// List of version descriptions/notes.
    /// Key is version number, value is optional description (e.g., "SDXL captions", "Flux NL captions").
    /// </summary>
    public Dictionary<int, string?> VersionDescriptions { get; set; } = new();

    /// <summary>
    /// Tracks which version each version was branched from.
    /// Key is version number, value is the parent version number it was branched from.
    /// V1 has no entry (it's the original).
    /// </summary>
    public Dictionary<int, int> VersionBranchedFrom { get; set; } = new();
}
