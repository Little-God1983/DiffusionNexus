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

#pragma warning disable CS0618 // Type or member is obsolete - Data property is still required for GetFiles extension
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        // Reset border style
        OnDragLeave(sender, e);

        var files = e.Data.GetFiles();
        if (files is null) return;

        // Collect all dropped files
        var droppedFiles = new List<string>();
        
        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                var filePath = file.Path.LocalPath;
                if (IsZipFile(filePath))
                {
                    // Extract and add media files from ZIP
                    AddFilesFromZip(filePath);
                    // Get the extracted files from SelectedFiles (they were added by AddFilesFromZip)
                }
                else
                {
                    droppedFiles.Add(filePath);
                }
            }
            else if (item is IStorageFolder folder)
            {
                CollectFilesFromFolder(folder.Path.LocalPath, droppedFiles);
            }
        }

        // Filter by allowed extensions
        var filteredFiles = droppedFiles.Where(IsFileAllowed).ToList();

        // Check for conflicts if conflict detection is enabled
        if (_existingFileNames is not null && _onConflictsDetected is not null && _destinationFolder is not null)
        {
            var conflictResult = FileConflictDetector.DetectConflicts(
                filteredFiles,
                _existingFileNames,
                _destinationFolder);

            // If there are any conflicts OR non-conflicting files, invoke the callback
            if (conflictResult.Conflicts.Count > 0 || conflictResult.NonConflictingFiles.Count > 0)
            {
                var result = await _onConflictsDetected(conflictResult.Conflicts, conflictResult.NonConflictingFiles);
                
                if (result is null || !result.Confirmed)
                {
                    // User cancelled - don't add any files
                    return;
                }

                // Process based on user selections - close this dialog and return
                ResultFiles = ProcessConflictResolution(result, conflictResult.NonConflictingFiles);
                Close(true);
                return;
            }
        }
        else
        {
            // No conflict detection - add files normally
            foreach (var filePath in filteredFiles)
            {
                AddFile(filePath);
            }
        }

        NotifyPropertiesChanged();
    }
#pragma warning restore CS0618

    /// <summary>
    /// Processes the conflict resolution result and returns the final list of files to import.
    /// </summary>
    private List<string> ProcessConflictResolution(FileConflictResolutionResult result, List<string> nonConflictingFiles)
    {
        var filesToReturn = new List<string>();

        // Add all non-conflicting files
        filesToReturn.AddRange(nonConflictingFiles);

        // Add conflicting files based on resolution
        foreach (var conflict in result.Conflicts)
        {
            switch (conflict.Resolution)
            {
                case FileConflictResolution.Override:
                case FileConflictResolution.Rename:
                    // These files will be handled by the caller
                    filesToReturn.Add(conflict.NewFilePath);
                    break;
                case FileConflictResolution.Ignore:
                    // Skip this file
                    break;
            }
        }

        return filesToReturn;
    }

    /// <summary>
    /// Collects all files from a folder.
    /// </summary>
    private static void CollectFilesFromFolder(string folderPath, List<string> files)
    {
        if (!Directory.Exists(folderPath)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath))
            {
                files.Add(file);
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
        var files = e.Data.GetFiles();
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

        foreach (var file in result)
        {
            AddFile(file.Path.LocalPath);
        }

        NotifyPropertiesChanged();
    }

    private void OnRemoveFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is SelectedFileItem item)
        {
            SelectedFiles.Remove(item);
            NotifyPropertiesChanged();
        }
    }

    private void OnDoneClick(object? sender, RoutedEventArgs e)
    {
        ResultFiles = SelectedFiles.Select(f => f.FilePath).ToList();
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        ResultFiles = null;
        Close(false);
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

    /// <summary>
    /// Extracts and adds files from a ZIP archive.
    /// Only media files (not .zip files) are extracted.
    /// </summary>
    private void AddFilesFromZip(string zipPath)
    {
        try
        {
            // Create a temporary directory for extraction
            var tempDir = Path.Combine(Path.GetTempPath(), "DiffusionNexus_ZipExtract_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);

            var extractedCount = 0;
            var allowedWithoutZip = _allowedExtensions.Where(e => !DefaultArchiveExtensions.Contains(e)).ToArray();

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    // Skip directories and empty entries
                    if (string.IsNullOrEmpty(entry.Name))
                        continue;

                    var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    
                    // Check if this file type is allowed (excluding zip)
                    if (allowedWithoutZip.Length > 0 && !allowedWithoutZip.Contains(ext))
                        continue;

                    // Extract to temp directory with flat structure (just filename)
                    var destPath = Path.Combine(tempDir, entry.Name);
                    
                    // Handle duplicate filenames by adding a suffix
                    var counter = 1;
                    while (File.Exists(destPath))
                    {
                        var nameWithoutExt = Path.GetFileNameWithoutExtension(entry.Name);
                        destPath = Path.Combine(tempDir, $"{nameWithoutExt}_{counter}{ext}");
                        counter++;
                    }

                    entry.ExtractToFile(destPath);
                    
                    // Add the extracted file (AddFile will handle duplicate checking in SelectedFiles)
                    if (SelectedFiles.All(f => !f.FilePath.Equals(destPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        SelectedFiles.Add(new SelectedFileItem(destPath));
                        extractedCount++;
                    }
                }
            }

            // If no files were extracted, clean up the temp directory
            if (extractedCount == 0 && Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
        catch
        {
            // Ignore extraction errors
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
