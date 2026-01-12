using System.Collections.ObjectModel;
using System.ComponentModel;
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
/// ViewModel for the file conflict resolution dialog.
/// </summary>
public sealed partial class FileConflictDialogViewModel : ObservableObject
{
    /// <summary>
    /// Collection of all file conflicts to resolve.
    /// </summary>
    public ObservableCollection<FileConflictItem> Conflicts { get; } = [];

    /// <summary>
    /// Number of conflicts.
    /// </summary>
    public int ConflictCount => Conflicts.Count;

    /// <summary>
    /// Header text describing the conflicts.
    /// </summary>
    public string HeaderText => ConflictCount == 1
        ? "1 file already exists in the dataset"
        : $"{ConflictCount} files already exist in the dataset";

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
            if (OverrideCount > 0) parts.Add($"{OverrideCount} override");
            if (RenameCount > 0) parts.Add($"{RenameCount} rename");
            if (IgnoreCount > 0) parts.Add($"{IgnoreCount} ignore");
            return parts.Count > 0 ? string.Join(", ", parts) : "No actions selected";
        }
    }

    /// <summary>
    /// Creates a new instance with the specified conflicts.
    /// </summary>
    public FileConflictDialogViewModel(IEnumerable<FileConflictItem> conflicts)
    {
        foreach (var conflict in conflicts)
        {
            conflict.PropertyChanged += OnConflictPropertyChanged;
            Conflicts.Add(conflict);
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
            UpdateCounts();
        }
    }

    private void UpdateCounts()
    {
        OverrideCount = Conflicts.Count(c => c.Resolution == FileConflictResolution.Override);
        RenameCount = Conflicts.Count(c => c.Resolution == FileConflictResolution.Rename);
        IgnoreCount = Conflicts.Count(c => c.Resolution == FileConflictResolution.Ignore);
        OnPropertyChanged(nameof(SummaryText));
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
