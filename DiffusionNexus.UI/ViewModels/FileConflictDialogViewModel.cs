using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Represents how to resolve a file naming conflict.
/// </summary>
public enum FileConflictResolution
{
    /// <summary>
    /// Override the existing file with the new one.
    /// </summary>
    Override,

    /// <summary>
    /// Rename the new file to avoid conflict.
    /// </summary>
    Rename,

    /// <summary>
    /// Skip/ignore this file (don't copy it).
    /// </summary>
    Ignore
}

/// <summary>
/// Represents a single file conflict between an existing file and a new file.
/// Can optionally include a paired caption file that should be renamed together.
/// </summary>
public sealed class FileConflictItem : INotifyPropertyChanged
{
    private FileConflictResolution _resolution = FileConflictResolution.Rename;
    private Bitmap? _existingPreview;
    private Bitmap? _newPreview;

    /// <summary>
    /// The conflicting filename (same for both files).
    /// </summary>
    public string ConflictingName { get; init; } = string.Empty;

    /// <summary>
    /// Full path to the existing file in the dataset.
    /// </summary>
    public string ExistingFilePath { get; init; } = string.Empty;

    /// <summary>
    /// Full path to the new file being added.
    /// </summary>
    public string NewFilePath { get; init; } = string.Empty;

    /// <summary>
    /// Full path to the new caption file paired with this media file.
    /// Null if no paired caption exists in the source.
    /// </summary>
    public string? PairedCaptionPath { get; init; }

    /// <summary>
    /// Full path to the existing caption file in the destination (if any).
    /// </summary>
    public string? ExistingCaptionPath { get; init; }

    /// <summary>
    /// Whether this file has a paired caption that needs to be handled together.
    /// </summary>
    public bool HasPairedCaption => !string.IsNullOrEmpty(PairedCaptionPath);

    /// <summary>
    /// Size of the existing file in bytes.
    /// </summary>
    public long ExistingFileSize { get; init; }

    /// <summary>
    /// Size of the new file in bytes.
    /// </summary>
    public long NewFileSize { get; init; }

    /// <summary>
    /// Creation date of the existing file.
    /// </summary>
    public DateTime ExistingCreationDate { get; init; }

    /// <summary>
    /// Creation date of the new file.
    /// </summary>
    public DateTime NewCreationDate { get; init; }

    /// <summary>
    /// Whether this file is an image (supports preview).
    /// </summary>
    public bool IsImage { get; init; }

    /// <summary>
    /// Preview thumbnail for the existing file (if image).
    /// </summary>
    public Bitmap? ExistingPreview
    {
        get => _existingPreview;
        set
        {
            if (_existingPreview != value)
            {
                _existingPreview = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Preview thumbnail for the new file (if image).
    /// </summary>
    public Bitmap? NewPreview
    {
        get => _newPreview;
        set
        {
            if (_newPreview != value)
            {
                _newPreview = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// The selected resolution for this conflict.
    /// </summary>
    public FileConflictResolution Resolution
    {
        get => _resolution;
        set
        {
            if (_resolution != value)
            {
                _resolution = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsOverride));
                OnPropertyChanged(nameof(IsRename));
                OnPropertyChanged(nameof(IsIgnore));
            }
        }
    }

    /// <summary>
    /// Whether Override is selected.
    /// </summary>
    public bool IsOverride
    {
        get => _resolution == FileConflictResolution.Override;
        set { if (value) Resolution = FileConflictResolution.Override; }
    }

    /// <summary>
    /// Whether Rename is selected.
    /// </summary>
    public bool IsRename
    {
        get => _resolution == FileConflictResolution.Rename;
        set { if (value) Resolution = FileConflictResolution.Rename; }
    }

    /// <summary>
    /// Whether Ignore is selected.
    /// </summary>
    public bool IsIgnore
    {
        get => _resolution == FileConflictResolution.Ignore;
        set { if (value) Resolution = FileConflictResolution.Ignore; }
    }

    /// <summary>
    /// Formatted existing file size for display.
    /// </summary>
    public string ExistingFileSizeText => FormatFileSize(ExistingFileSize);

    /// <summary>
    /// Formatted new file size for display.
    /// </summary>
    public string NewFileSizeText => FormatFileSize(NewFileSize);

    /// <summary>
    /// Formatted existing creation date for display.
    /// </summary>
    public string ExistingCreationDateText => ExistingCreationDate.ToString("dd.MM.yyyy");

    /// <summary>
    /// Formatted new creation date for display.
    /// </summary>
    public string NewCreationDateText => NewCreationDate.ToString("dd.MM.yyyy");

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Result from the file conflict resolution dialog.
/// </summary>
public sealed class FileConflictResolutionResult
{
    /// <summary>
    /// Whether the user confirmed the resolution (true) or cancelled (false).
    /// </summary>
    public bool Confirmed { get; init; }

    /// <summary>
    /// The list of conflict items with their selected resolutions.
    /// </summary>
    public IReadOnlyList<FileConflictItem> Conflicts { get; init; } = [];

    /// <summary>
    /// Creates a cancelled result.
    /// </summary>
    public static FileConflictResolutionResult Cancelled() => new() { Confirmed = false };
}

/// <summary>
/// Represents a non-conflicting file being added (for display purposes in the conflict dialog).
/// </summary>
public sealed class NonConflictingFileItem
{
    /// <summary>
    /// The file name.
    /// </summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// Full path to the file.
    /// </summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>
    /// Size of the file in bytes.
    /// </summary>
    public long FileSize { get; init; }

    /// <summary>
    /// Whether this file is an image (supports preview).
    /// </summary>
    public bool IsImage { get; init; }

    /// <summary>
    /// Formatted file size for display.
    /// </summary>
    public string FileSizeText => FormatFileSize(FileSize);

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}

/// <summary>
/// ViewModel for the file conflict resolution dialog.
/// </summary>
public sealed partial class FileConflictDialogViewModel : ObservableObject
{
    /// <summary>
    /// Collection of all file conflicts to resolve.
    /// </summary>
    public ObservableCollection<FileConflictItem> Conflicts { get; } = [];

    /// <summary>
    /// Collection of non-conflicting files that will be added.
    /// </summary>
    public ObservableCollection<NonConflictingFileItem> NonConflictingFiles { get; } = [];

    /// <summary>
    /// Number of conflicts.
    /// </summary>
    public int ConflictCount => Conflicts.Count;

    /// <summary>
    /// Number of non-conflicting files.
    /// </summary>
    public int NonConflictingCount => NonConflictingFiles.Count;

    /// <summary>
    /// Total number of files being processed (conflicts + non-conflicting).
    /// </summary>
    public int TotalFileCount => ConflictCount + NonConflictingCount;

    /// <summary>
    /// Whether there are non-conflicting files to display.
    /// </summary>
    public bool HasNonConflictingFiles => NonConflictingFiles.Count > 0;

    /// <summary>
    /// Whether there are conflicts to resolve.
    /// </summary>
    public bool HasConflicts => Conflicts.Count > 0;

    /// <summary>
    /// Header text describing the conflicts.
    /// </summary>
    public string HeaderText
    {
        get
        {
            if (ConflictCount == 0)
            {
                return NonConflictingCount == 1
                    ? "1 file ready to add"
                    : $"{NonConflictingCount} files ready to add";
            }
            
            return ConflictCount == 1
                ? "1 file already exists in the dataset"
                : $"{ConflictCount} files already exist in the dataset";
        }
    }

    /// <summary>
    /// Number of files set to Override.
    /// </summary>
    [ObservableProperty]
    private int _overrideCount;

    /// <summary>
    /// Number of files set to Rename.
    /// </summary>
    [ObservableProperty]
    private int _renameCount;

    /// <summary>
    /// Number of files set to Ignore.
    /// </summary>
    [ObservableProperty]
    private int _ignoreCount;

    /// <summary>
    /// Summary text for the current resolution selections.
    /// </summary>
    public string SummaryText
    {
        get
        {
            var parts = new List<string>();
            if (NonConflictingCount > 0) parts.Add($"{NonConflictingCount} new");
            if (OverrideCount > 0) parts.Add($"{OverrideCount} override");
            if (RenameCount > 0) parts.Add($"{RenameCount} rename");
            if (IgnoreCount > 0) parts.Add($"{IgnoreCount} ignore");
            
            if (parts.Count == 0) return "No actions selected";
            
            var totalToAdd = NonConflictingCount + OverrideCount + RenameCount;
            return $"{totalToAdd} images: " + string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Creates a new instance with the specified conflicts and non-conflicting files.
    /// </summary>
    public FileConflictDialogViewModel(IEnumerable<FileConflictItem> conflicts, IEnumerable<string>? nonConflictingFilePaths = null)
    {
        foreach (var conflict in conflicts)
        {
            conflict.PropertyChanged += OnConflictPropertyChanged;
            Conflicts.Add(conflict);
        }

        if (nonConflictingFilePaths is not null)
        {
            foreach (var filePath in nonConflictingFilePaths)
            {
                var fileInfo = new FileInfo(filePath);
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                var isImage = ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";
                
                NonConflictingFiles.Add(new NonConflictingFileItem
                {
                    FileName = Path.GetFileName(filePath),
                    FilePath = filePath,
                    FileSize = fileInfo.Exists ? fileInfo.Length : 0,
                    IsImage = isImage
                });
            }
        }

        UpdateCounts();
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public FileConflictDialogViewModel()
    {
    }

    private void OnConflictPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileConflictItem.Resolution))
        {
            if (sender is FileConflictItem changedItem)
            {
                // Sync resolution with other items sharing the same base name
                // This handles syncing image + caption pairs
                SyncResolutionWithPairs(changedItem);
            }

            UpdateCounts();
        }
    }

    private void SyncResolutionWithPairs(FileConflictItem changedItem)
    {
        // Prevent recursive updates if we update other items
        // Since we are iterating and updating directly, and UpdateCounts is called after,
        // we simple need to avoid infinite loops if the property change event fires back.
        // However, FileConflictItem just raises event, logic is here.
        // We can temporarily detach event handler or just check if value is different.

        var baseName = Path.GetFileNameWithoutExtension(changedItem.ConflictingName);
        var targetResolution = changedItem.Resolution;

        foreach (var conflict in Conflicts)
        {
            if (conflict == changedItem) continue;

            var otherBaseName = Path.GetFileNameWithoutExtension(conflict.ConflictingName);
            if (string.Equals(baseName, otherBaseName, StringComparison.OrdinalIgnoreCase))
            {
                if (conflict.Resolution != targetResolution)
                {
                    conflict.Resolution = targetResolution;
                }
            }
        }
    }

    private void UpdateCounts()
    {
        OverrideCount = Conflicts.Count(c => c.Resolution == FileConflictResolution.Override);
        RenameCount = Conflicts.Count(c => c.Resolution == FileConflictResolution.Rename);
        IgnoreCount = Conflicts.Count(c => c.Resolution == FileConflictResolution.Ignore);
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(TotalFileCount));
        OnPropertyChanged(nameof(HasNonConflictingFiles));
        OnPropertyChanged(nameof(HasConflicts));
    }

    /// <summary>
    /// Sets all conflicts to Override.
    /// </summary>
    [RelayCommand]
    private void SetAllOverride()
    {
        foreach (var conflict in Conflicts)
        {
            conflict.Resolution = FileConflictResolution.Override;
        }
    }

    /// <summary>
    /// Sets all conflicts to Rename.
    /// </summary>
    [RelayCommand]
    private void SetAllRename()
    {
        foreach (var conflict in Conflicts)
        {
            conflict.Resolution = FileConflictResolution.Rename;
        }
    }

    /// <summary>
    /// Sets all conflicts to Ignore.
    /// </summary>
    [RelayCommand]
    private void SetAllIgnore()
    {
        foreach (var conflict in Conflicts)
        {
            conflict.Resolution = FileConflictResolution.Ignore;
        }
    }
}
