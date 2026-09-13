using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Utilities;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// A reusable drag-and-drop file picker dialog.
/// Supports dragging files onto the dialog or clicking to browse.
/// Can detect conflicts with existing files and trigger immediate conflict resolution.
/// </summary>
public partial class FileDropDialog : Window, INotifyPropertyChanged
{
    private static readonly string[] DefaultImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];
    private static readonly string[] DefaultVideoExtensions = [".mp4", ".mov", ".webm", ".avi", ".mkv", ".wmv", ".flv", ".m4v"];
    private static readonly string[] DefaultTextExtensions = [".txt", ".caption"];
    private static readonly string[] DefaultArchiveExtensions = [".zip"];
    private static readonly string[] DefaultMediaExtensions = [..DefaultImageExtensions, ..DefaultVideoExtensions];
    
    private string[] _allowedExtensions = [];
    private HashSet<string>? _existingFileNames;
    private Func<IEnumerable<FileConflictItem>, IEnumerable<string>, Task<FileConflictResolutionResult?>>? _onConflictsDetected;
    private string? _destinationFolder;
    private readonly List<string> _tempDirectories = [];
    private readonly Dictionary<string, ArchiveOrigin> _archiveOrigins = new(StringComparer.OrdinalIgnoreCase);
    private bool _resolvingConflicts;

    public FileDropDialog()
    {
        InitializeComponent();
        DataContext = this;
        
        // Set up drag-drop handlers
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    #region Properties

    /// <summary>
    /// Title displayed at the top of the dialog.
    /// </summary>
    public string DialogTitle { get; set; } = "Add Files";

    /// <summary>
    /// Collection of selected files.
    /// </summary>
    public ObservableCollection<SelectedFileItem> SelectedFiles { get; } = [];

    /// <summary>
    /// Whether any files have been selected.
    /// </summary>
    public bool HasFiles => SelectedFiles.Count > 0;

    /// <summary>
    /// Text showing file count.
    /// </summary>
    public string FileCountText => SelectedFiles.Count == 1 
        ? "1 file selected" 
        : $"{SelectedFiles.Count} files selected";

    /// <summary>
    /// Result file paths after dialog closes. Null if cancelled.
    /// </summary>
    public List<string>? ResultFiles { get; private set; }

    /// <summary>
    /// Conflict resolution data from the conflict dialog, if conflicts were detected and resolved.
    /// </summary>
    public FileConflictResolutionResult? ConflictResolutionData { get; private set; }

    /// <summary>
    /// Files that had no conflicts and can be copied directly.
    /// </summary>
    public List<string>? NonConflictingResultFiles { get; private set; }

    /// <summary>
    /// Temporary directories created while expanding dropped ZIP archives. Files in
    /// <see cref="ResultFiles"/> may live here, so the caller must delete these directories
    /// after it has imported the files. They are removed automatically when the dialog is cancelled.
    /// </summary>
    public IReadOnlyList<string> TemporaryDirectories => _tempDirectories;

    #endregion

    #region Configuration

    /// <summary>
    /// Configures the dialog to accept image files only.
    /// </summary>
    public FileDropDialog ForImages()
    {
        _allowedExtensions = DefaultImageExtensions;
        return this;
    }

    /// <summary>
    /// Configures the dialog to accept video files only.
    /// </summary>
    public FileDropDialog ForVideos()
    {
        _allowedExtensions = DefaultVideoExtensions;
        return this;
    }

    /// <summary>
    /// Configures the dialog to accept media files (images and videos).
    /// </summary>
    public FileDropDialog ForMedia()
    {
        _allowedExtensions = DefaultMediaExtensions;
        return this;
    }

    /// <summary>
    /// Configures the dialog to accept media and text/caption files.
    /// This is the recommended mode for dataset file drops.
    /// </summary>
    public FileDropDialog ForMediaAndText()
    {
        _allowedExtensions = [..DefaultMediaExtensions, ..DefaultTextExtensions, ..DefaultArchiveExtensions];
        return this;
    }

    /// <summary>
    /// Configures the dialog to accept image and text/caption files.
    /// </summary>
    [Obsolete("Use ForMediaAndText() to also support video files")]
    public FileDropDialog ForImagesAndText()
    {
        _allowedExtensions = [..DefaultImageExtensions, ..DefaultTextExtensions];
        return this;
    }

    /// <summary>
    /// Configures the dialog with custom allowed extensions.
    /// </summary>
    public FileDropDialog WithExtensions(params string[] extensions)
    {
        _allowedExtensions = extensions;
        return this;
    }

    /// <summary>
    /// Sets the dialog title.
    /// </summary>
    public FileDropDialog WithTitle(string title)
    {
        DialogTitle = title;
        return this;
    }

    /// <summary>
    /// Pre-populates the dialog with initial file paths.
    /// Files are validated against allowed extensions before being added.
    /// </summary>
    /// <param name="filePaths">File paths to pre-populate.</param>
    public FileDropDialog WithInitialFiles(IEnumerable<string> filePaths)
    {
        foreach (var filePath in filePaths)
        {
            AddFile(filePath);
        }
        NotifyPropertiesChanged();
        return this;
    }

    /// <summary>
    /// Configures the dialog with existing file names for conflict detection.
    /// When files are dropped that conflict with these names, the conflict callback is invoked immediately.
    /// </summary>
    /// <param name="existingFileNames">Set of existing file names (just the filename, not full path).</param>
    /// <param name="destinationFolder">The destination folder path for building conflict items.</param>
    /// <param name="onConflictsDetected">Callback invoked when conflicts are detected. Returns resolution result or null if cancelled.</param>
    public FileDropDialog WithConflictDetection(
        IEnumerable<string> existingFileNames,
        string destinationFolder,
        Func<IEnumerable<FileConflictItem>, IEnumerable<string>, Task<FileConflictResolutionResult?>> onConflictsDetected)
    {
        _existingFileNames = new HashSet<string>(existingFileNames, StringComparer.OrdinalIgnoreCase);
        _destinationFolder = destinationFolder;
        _onConflictsDetected = onConflictsDetected;
        return this;
    }

    #endregion

    #region Drag and Drop Handlers

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var dropZone = this.FindControl<Border>("DropZone");
        if (dropZone is null) return;

        // Check the dragged files
        var (hasValidFiles, hasInvalidFiles) = AnalyzeFilesInDrag(e);
        
        if (hasValidFiles && hasInvalidFiles)
        {
            // Dark yellow/orange border for mixed files (some valid, some invalid)
            dropZone.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#DAA520")); // Goldenrod
            dropZone.BorderThickness = new Avalonia.Thickness(3);
            e.DragEffects = DragDropEffects.Copy;
        }
        else if (hasValidFiles)
        {
            // Green border for all valid files
            dropZone.BorderBrush = Avalonia.Media.Brushes.LimeGreen;
            dropZone.BorderThickness = new Avalonia.Thickness(3);
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            // Red border for all invalid/unsupported files
            dropZone.BorderBrush = Avalonia.Media.Brushes.Red;
            dropZone.BorderThickness = new Avalonia.Thickness(3);
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        var dropZone = this.FindControl<Border>("DropZone");
        if (dropZone is null) return;
        
        // Reset to default border
        dropZone.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#666"));
        dropZone.BorderThickness = new Avalonia.Thickness(2);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        // Reset border style
        OnDragLeave(sender, e);

        var files = GetFilesFromEvent(e);
        if (files is null) return;

        var incoming = new List<string>();
        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                CollectIncomingFile(file.Path.LocalPath, incoming);
            }
            else if (item is IStorageFolder folder)
            {
                CollectFilesFromFolder(folder.Path.LocalPath, incoming);
            }
        }

        await AcceptIncomingFilesAsync(incoming);
    }

    /// <summary>
    /// Adds a single user-supplied path to <paramref name="incoming"/>. ZIP archives are expanded
    /// into a temporary directory and contribute their media entries instead of the archive itself,
    /// so archive content flows through exactly the same filter and conflict detection as loose files.
    /// </summary>
    private void CollectIncomingFile(string filePath, List<string> incoming)
    {
        if (IsZipFile(filePath))
        {
            var extraction = ZipMediaExtractor.Extract(filePath, _allowedExtensions);
            if (extraction.TempDirectory is not null)
                _tempDirectories.Add(extraction.TempDirectory);
            foreach (var (extractedPath, origin) in extraction.Origins)
                _archiveOrigins[extractedPath] = origin;
            incoming.AddRange(extraction.ExtractedFiles);
        }
        else
        {
            incoming.Add(filePath);
        }
    }

    /// <summary>
    /// Filters <paramref name="incoming"/> by the allowed extensions, runs conflict detection over the
    /// whole selection (already selected + incoming) when configured, and either closes the dialog
    /// with a resolved result or appends the files to the visible selection.
    /// </summary>
    private async Task AcceptIncomingFilesAsync(IEnumerable<string> incoming)
    {
        var filtered = incoming.Where(IsFileAllowed).ToList();
        if (filtered.Count == 0)
        {
            NotifyPropertiesChanged();
            return;
        }

        var candidates = FileDropSelectionHelper.MergeDistinct(
            SelectedFiles.Select(f => f.FilePath), filtered);

        var outcome = await TryResolveConflictsAndCloseAsync(candidates);
        if (outcome != ConflictCheckOutcome.NoConflicts)
            return;

        foreach (var filePath in filtered)
        {
            AddFile(filePath);
        }

        NotifyPropertiesChanged();
    }

    private enum ConflictCheckOutcome
    {
        /// <summary>Nothing conflicts (or detection is not configured); the caller continues normally.</summary>
        NoConflicts,
        /// <summary>Conflicts were resolved and the dialog has been closed with a result.</summary>
        Closed,
        /// <summary>The user dismissed the conflict dialog; nothing was added and the dialog stays open.</summary>
        Cancelled,
    }

    /// <summary>
    /// Runs conflict detection over <paramref name="candidates"/>. When conflicts exist the conflict
    /// callback is invoked and, if confirmed, the dialog closes with the resolved result populated.
    /// </summary>
    private async Task<ConflictCheckOutcome> TryResolveConflictsAndCloseAsync(List<string> candidates)
    {
        if (_existingFileNames is null || _onConflictsDetected is null || _destinationFolder is null)
            return ConflictCheckOutcome.NoConflicts;

        // The comparer is modal to the main window, not to this dialog, so a second Done click or
        // drop while it is open would start a second resolution over the same selection.
        if (_resolvingConflicts)
            return ConflictCheckOutcome.Cancelled;

        var detection = FileConflictDetector.DetectConflicts(
            candidates, _existingFileNames, _destinationFolder, _archiveOrigins);
        if (detection.Conflicts.Count == 0)
            return ConflictCheckOutcome.NoConflicts;

        _resolvingConflicts = true;
        FileConflictResolutionResult? resolution;
        try
        {
            resolution = await _onConflictsDetected(detection.Conflicts, detection.NonConflictingFiles);
        }
        finally
        {
            _resolvingConflicts = false;
        }

        if (resolution is null || !resolution.Confirmed)
            return ConflictCheckOutcome.Cancelled;

        ConflictResolutionData = resolution;
        NonConflictingResultFiles = detection.NonConflictingFiles.ToList();
        ResultFiles = FileDropSelectionHelper.BuildFinalFileList(resolution, detection.NonConflictingFiles);
        Close(true);
        return ConflictCheckOutcome.Closed;
    }

    /// <summary>
    /// Collects all files from a folder.
    /// </summary>
    private void CollectFilesFromFolder(string folderPath, List<string> files)
    {
        if (!Directory.Exists(folderPath)) return;

        try
        {
            // Same route as a directly dropped file, so an archive inside the folder is expanded
            // instead of being copied into the dataset as if it were media.
            foreach (var file in Directory.EnumerateFiles(folderPath))
            {
                CollectIncomingFile(file, files);
            }
        }
        catch (IOException) { /* Directory access error */ }
        catch (UnauthorizedAccessException) { /* Permission denied */ }
    }

    /// <summary>
    /// Checks if a file is an image file.
    /// </summary>
    private static bool IsImageFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return DefaultImageExtensions.Contains(ext);
    }

    /// <summary>
    /// Analyzes the drag event to determine if it contains valid files, invalid files, or both.
    /// </summary>
    /// <returns>A tuple of (hasValidFiles, hasInvalidFiles)</returns>
    private (bool HasValid, bool HasInvalid) AnalyzeFilesInDrag(DragEventArgs e)
    {
        var files = GetFilesFromEvent(e);
        if (files is null) return (false, false);

        var hasValid = false;
        var hasInvalid = false;

        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                var filePath = file.Path.LocalPath;
                if (IsZipFile(filePath))
                {
                    // Analyze ZIP contents
                    var (zipHasValid, zipHasInvalid) = AnalyzeFilesInZip(filePath);
                    if (zipHasValid) hasValid = true;
                    if (zipHasInvalid) hasInvalid = true;
                }
                else if (IsFileAllowed(filePath))
                {
                    hasValid = true;
                }
                else
                {
                    hasInvalid = true;
                }
            }
            else if (item is IStorageFolder folder)
            {
                var (folderHasValid, folderHasInvalid) = AnalyzeFilesInFolder(folder.Path.LocalPath);
                if (folderHasValid) hasValid = true;
                if (folderHasInvalid) hasInvalid = true;
            }

            // Early exit if we've found both types
            if (hasValid && hasInvalid) break;
        }

        return (hasValid, hasInvalid);
    }

    private static IEnumerable<IStorageItem>? GetFilesFromEvent(DragEventArgs e)
    {
#pragma warning disable CS0618 // Type or member is obsolete
        return e.Data.GetFiles();
#pragma warning restore CS0618
    }

    /// <summary>
    /// Analyzes a folder to determine if it contains valid files, invalid files, or both.
    /// </summary>
    private (bool HasValid, bool HasInvalid) AnalyzeFilesInFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return (false, false);

        var hasValid = false;
        var hasInvalid = false;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath))
            {
                if (IsFileAllowed(file))
                    hasValid = true;
                else
                    hasInvalid = true;

                // Early exit if we've found both types
                if (hasValid && hasInvalid) break;
            }
        }
        catch
        {
            // Ignore access errors
        }

        return (hasValid, hasInvalid);
    }

    /// <summary>
    /// Checks if a file is allowed based on configured extensions.
    /// </summary>
    private bool IsFileAllowed(string filePath)
    {
        if (_allowedExtensions.Length == 0)
            return true; // No restrictions

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return _allowedExtensions.Contains(ext);
    }

    /// <summary>
    /// Checks if a file is a ZIP archive.
    /// </summary>
    private static bool IsZipFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return DefaultArchiveExtensions.Contains(ext);
    }

    /// <summary>
    /// Analyzes a ZIP file to determine if it contains valid files based on allowed extensions.
    /// </summary>
    private (bool HasValid, bool HasInvalid) AnalyzeFilesInZip(string zipPath)
    {
        var hasValid = false;
        var hasInvalid = false;

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                // Skip directories
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                // Check if the file inside ZIP is allowed (excluding .zip itself)
                var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                var allowedWithoutZip = _allowedExtensions.Where(e => !DefaultArchiveExtensions.Contains(e)).ToArray();
                
                if (allowedWithoutZip.Length == 0 || allowedWithoutZip.Contains(ext))
                    hasValid = true;
                else
                    hasInvalid = true;

                // Early exit if we've found both types
                if (hasValid && hasInvalid) break;
            }
        }
        catch
        {
            // If we can't read the ZIP, treat it as invalid
            hasInvalid = true;
        }

        return (hasValid, hasInvalid);
    }

    #endregion

    #region Event Handlers

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var filters = new List<FilePickerFileType>();
        
        if (_allowedExtensions.Length > 0)
        {
            filters.Add(new FilePickerFileType("Allowed Files")
            {
                Patterns = _allowedExtensions.Select(ext => $"*{ext}").ToList()
            });
        }
        
        filters.Add(new FilePickerFileType("All Files") { Patterns = ["*.*"] });

        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Files",
            AllowMultiple = true,
            FileTypeFilter = filters
        });

        var incoming = new List<string>();
        foreach (var file in result)
        {
            CollectIncomingFile(file.Path.LocalPath, incoming);
        }

        await AcceptIncomingFilesAsync(incoming);
    }

    private void OnRemoveFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is SelectedFileItem item)
        {
            SelectedFiles.Remove(item);
            NotifyPropertiesChanged();
        }
    }

    private async void OnDoneClick(object? sender, RoutedEventArgs e)
    {
        var selected = SelectedFiles.Select(f => f.FilePath).ToList();

        // Files that reached the selection without passing conflict detection (e.g. pre-populated
        // initial files) get their final check here.
        var outcome = await TryResolveConflictsAndCloseAsync(selected);
        if (outcome != ConflictCheckOutcome.NoConflicts)
            return;

        ResultFiles = selected;
        NonConflictingResultFiles = ResultFiles;
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        ResultFiles = null;
        Close(false);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // On cancel (Cancel button or window close) nothing will consume the extracted files,
        // so every temporary directory is ours to remove. On success the caller owns the ones
        // holding returned files; the rest (archive removed from the selection, or expanded but
        // rejected by the filter) would otherwise leak.
        if (ResultFiles is null)
        {
            DeleteTemporaryDirectories(_ => true);
        }
        else
        {
            var returned = ResultFiles;
            DeleteTemporaryDirectories(dir => !returned.Any(f => IsInside(f, dir)));
        }
    }

    private static bool IsInside(string filePath, string directory)
    {
        var dirFull = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(filePath).StartsWith(dirFull, StringComparison.OrdinalIgnoreCase);
    }

    private void DeleteTemporaryDirectories(Func<string, bool> shouldDelete)
    {
        foreach (var dir in _tempDirectories.Where(shouldDelete).ToList())
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best effort cleanup of our own temp folders.
            }

            _tempDirectories.Remove(dir);
        }
    }

    #endregion

    #region Helper Methods

    private void AddFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        
        if (_allowedExtensions.Length > 0)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (!_allowedExtensions.Contains(ext)) return;
        }

        if (SelectedFiles.Any(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
            return;

        SelectedFiles.Add(new SelectedFileItem(filePath));
    }

    private void AddFilesFromFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return;

        var files = Directory.EnumerateFiles(folderPath);
        foreach (var file in files)
        {
            AddFile(file);
        }
    }

    private void NotifyPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(FileCountText));
    }

    #endregion

    #region INotifyPropertyChanged

    public new event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #endregion
}

/// <summary>
/// Represents a selected file in the file drop dialog.
/// </summary>
public class SelectedFileItem
{
    public SelectedFileItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
    }

    public string FilePath { get; }
    public string FileName { get; }
}
