using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.Tabs;
using System.IO.Compression;

namespace DiffusionNexus.UI.Views.Tabs;

/// <summary>
/// View for the Dataset Management tab in the LoRA Dataset Helper.
/// Handles drag-drop operations, keyboard shortcuts, and pointer events for image selection.
/// 
/// <para>
/// <b>DRY Compliance:</b>
/// This view uses <see cref="MediaFileExtensions"/> for all file type detection
/// to maintain a single source of truth for supported file extensions.
/// </para>
/// </summary>
public partial class DatasetManagementView : UserControl
{
    // Archive extensions are kept here as they are view-specific (drag-drop behavior)
    // and not part of the core media file handling in MediaFileExtensions
    private static readonly string[] ArchiveExtensions = [".zip"];

    private Border? _emptyDatasetDropZone;
    private TextBox? _descriptionTextBox;
    private bool _isInitialized;

    public DatasetManagementView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        KeyDown += OnKeyDown;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _emptyDatasetDropZone = this.FindControl<Border>("EmptyDatasetDropZone");
        _descriptionTextBox = this.FindControl<TextBox>("DescriptionTextBox");

        // Set up drag-drop handlers
        if (_emptyDatasetDropZone is not null)
        {
            _emptyDatasetDropZone.AddHandler(DragDrop.DropEvent, OnEmptyDatasetDrop);
            _emptyDatasetDropZone.AddHandler(DragDrop.DragEnterEvent, OnEmptyDatasetDragEnter);
            _emptyDatasetDropZone.AddHandler(DragDrop.DragLeaveEvent, OnEmptyDatasetDragLeave);
        }

        // Set up auto-save for description TextBox
        if (_descriptionTextBox is not null)
        {
            _descriptionTextBox.LostFocus += OnDescriptionLostFocus;
        }
    }

    private void OnDescriptionLostFocus(object? sender, RoutedEventArgs e)
    {
        // Auto-save description when TextBox loses focus
        if (DataContext is DatasetManagementViewModel vm && vm.ActiveDataset is not null)
        {
            vm.ActiveDataset.SaveMetadata();
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_isInitialized) return;
        _isInitialized = true;

        // Note: DialogService is injected by the parent LoraDatasetHelper
        // and forwarded via OnDialogServiceSet()
        
        // Note: CheckStorageConfigurationCommand is called by the parent shell
        // after DialogService is set up
    }

    private void OnEmptyDatasetDragEnter(object? sender, DragEventArgs e)
    {
        if (DataContext is DatasetManagementViewModel vm && vm.IsFileDialogOpen)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        if (_emptyDatasetDropZone is null) return;

        var (hasValidFiles, hasInvalidFiles) = AnalyzeMediaFilesInDrag(e);

        if (hasValidFiles && hasInvalidFiles)
        {
            _emptyDatasetDropZone.BorderBrush = new SolidColorBrush(Color.Parse("#DAA520"));
            _emptyDatasetDropZone.BorderThickness = new Thickness(3);
            e.DragEffects = DragDropEffects.Copy;
        }
        else if (hasValidFiles)
        {
            _emptyDatasetDropZone.BorderBrush = Brushes.LimeGreen;
            _emptyDatasetDropZone.BorderThickness = new Thickness(3);
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            _emptyDatasetDropZone.BorderBrush = Brushes.Red;
            _emptyDatasetDropZone.BorderThickness = new Thickness(3);
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnEmptyDatasetDragLeave(object? sender, DragEventArgs e)
    {
        if (_emptyDatasetDropZone is not null)
        {
            _emptyDatasetDropZone.BorderBrush = new SolidColorBrush(Color.Parse("#444"));
            _emptyDatasetDropZone.BorderThickness = new Thickness(3);
        }
    }

    private (bool HasValid, bool HasInvalid) AnalyzeMediaFilesInDrag(DragEventArgs e)
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
                
                // Use centralized MediaFileExtensions for file type detection
                // Accept both media files and caption files as valid
                if (MediaFileExtensions.IsMediaFile(filePath) || MediaFileExtensions.IsCaptionFile(filePath))
                {
                    hasValid = true;
                }
                else if (IsZipFile(filePath))
                {
                    var (zipHasValid, zipHasInvalid) = AnalyzeMediaFilesInZip(filePath);
                    if (zipHasValid) hasValid = true;
                    if (zipHasInvalid) hasInvalid = true;
                }
                else
                {
                    hasInvalid = true;
                }
            }
            else if (item is IStorageFolder folder)
            {
                var (folderHasValid, folderHasInvalid) = AnalyzeMediaFilesInFolder(folder.Path.LocalPath);
                if (folderHasValid) hasValid = true;
                if (folderHasInvalid) hasInvalid = true;
            }

            if (hasValid && hasInvalid) break;
        }

        return (hasValid, hasInvalid);
    }

    private static (bool HasValid, bool HasInvalid) AnalyzeMediaFilesInFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return (false, false);

        var hasValid = false;
        var hasInvalid = false;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath))
            {
                // Use centralized MediaFileExtensions for file type detection
                // Accept both media files and caption files as valid
                if (MediaFileExtensions.IsMediaFile(file) || MediaFileExtensions.IsCaptionFile(file))
                    hasValid = true;
                else
                    hasInvalid = true;

                if (hasValid && hasInvalid) break;
            }
        }
        catch (IOException)
        {
            // Directory access error - treat as having invalid files
            hasInvalid = true;
        }
        catch (UnauthorizedAccessException)
        {
            // Permission denied - treat as having invalid files
            hasInvalid = true;
        }

        return (hasValid, hasInvalid);
    }

    private async void OnEmptyDatasetDrop(object? sender, DragEventArgs e)
    {
        OnEmptyDatasetDragLeave(sender, e);

        if (DataContext is not DatasetManagementViewModel vm || vm.ActiveDataset is null)
            return;

        if (vm.IsFileDialogOpen) return;

        var files = e.Data.GetFiles();
        if (files is null) return;

        // Collect all file paths from dropped items (files, folders, ZIPs)
        var allFilePaths = new List<string>();

        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                var filePath = file.Path.LocalPath;

                if (IsZipFile(filePath))
                {
                    // Extract media files from ZIP to temp directory
                    var extracted = ExtractMediaFromZip(filePath);
                    allFilePaths.AddRange(extracted);
                }
                else
                {
                    // Add individual file (dialog will filter by allowed extensions)
                    allFilePaths.Add(filePath);
                }
            }
            else if (item is IStorageFolder folder)
            {
                // Add all files from folder (dialog will filter by allowed extensions)
                CollectFilesFromFolder(folder.Path.LocalPath, allFilePaths);
            }
        }

        if (allFilePaths.Count == 0)
        {
            vm.StatusMessage = "No files found in the dropped items.";
            return;
        }

        // Open the Add Files dialog pre-populated with the dropped files
        if (vm.DialogService is null)
        {
            vm.StatusMessage = "Dialog service not available.";
            return;
        }

        vm.IsFileDialogOpen = true;
        try
        {
            var selectedFiles = await vm.DialogService.ShowFileDropDialogAsync(
                $"Add Media to: {vm.ActiveDataset.Name}", 
                allFilePaths);

            if (selectedFiles is null || selectedFiles.Count == 0)
            {
                // User cancelled or removed all files - cleanup only temp files from ZIP extraction
                CleanupTempFiles(allFilePaths.Where(f => f.Contains("DiffusionNexus_ZipExtract_")));
                return;
            }

            // Copy selected files to dataset
            await CopyFilesToDatasetAsync(vm, selectedFiles);

            // Cleanup temp files (those from ZIP extraction)
            CleanupTempFiles(allFilePaths.Where(f => f.Contains("DiffusionNexus_ZipExtract_")));
        }
        finally
        {
            vm.IsFileDialogOpen = false;
        }
    }

    /// <summary>
    /// Collects all file paths from a folder (non-recursive).
    /// </summary>
    private static void CollectFilesFromFolder(string folderPath, List<string> filePaths)
    {
        if (!Directory.Exists(folderPath)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath))
            {
                filePaths.Add(file);
            }
        }
        catch (IOException) { /* Directory access error */ }
        catch (UnauthorizedAccessException) { /* Permission denied */ }
    }

    /// <summary>
    /// Cleans up temporary files extracted from ZIP archives.
    /// </summary>
    private static void CleanupTempFiles(IEnumerable<string> tempFiles)
    {
        var tempDirs = new HashSet<string>();

        foreach (var tempFile in tempFiles)
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                    var dir = Path.GetDirectoryName(tempFile);
                    if (dir is not null)
                    {
                        tempDirs.Add(dir);
                    }
                }
            }
            catch (IOException) { /* File in use */ }
            catch (UnauthorizedAccessException) { /* Permission denied */ }
        }

        // Cleanup empty temp directories
        foreach (var dir in tempDirs)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (IOException) { /* Directory in use or not empty */ }
            catch (UnauthorizedAccessException) { /* Permission denied */ }
        }
    }

    private static void AddMediaFromFolder(string folderPath, List<string> mediaFiles)
    {
        if (!Directory.Exists(folderPath)) return;
        
        foreach (var file in Directory.EnumerateFiles(folderPath))
        {
            // Use centralized MediaFileExtensions for file type detection
            if (MediaFileExtensions.IsMediaFile(file))
            {
                mediaFiles.Add(file);
            }
        }
    }

    private static List<string> ExtractMediaFromZip(string zipPath)
    {
        var extractedFiles = new List<string>();

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "DiffusionNexus_ZipExtract_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    // Use centralized MediaFileExtensions for file type detection
                    // Extract both media files and caption files
                    if (MediaFileExtensions.IsMediaFile(entry.Name) || MediaFileExtensions.IsCaptionFile(entry.Name))
                    {
                        var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                        var destPath = Path.Combine(tempDir, entry.Name);
                        var counter = 1;
                        while (File.Exists(destPath))
                        {
                            var nameWithoutExt = Path.GetFileNameWithoutExtension(entry.Name);
                            destPath = Path.Combine(tempDir, $"{nameWithoutExt}_{counter}{ext}");
                            counter++;
                        }
                        entry.ExtractToFile(destPath);
                        extractedFiles.Add(destPath);
                    }
                }
            }

            if (extractedFiles.Count == 0 && Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch (IOException) { /* Directory in use */ }
                catch (UnauthorizedAccessException) { /* Permission denied */ }
            }
        }
        catch (InvalidDataException)
        {
            // Corrupt or invalid ZIP file - return empty list
        }
        catch (IOException)
        {
            // File access error
        }

        return extractedFiles;
    }

    private static bool IsZipFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ArchiveExtensions.Contains(ext);
    }

    private static (bool HasValid, bool HasInvalid) AnalyzeMediaFilesInZip(string zipPath)
    {
        var hasValid = false;
        var hasInvalid = false;

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                // Use centralized MediaFileExtensions for file type detection
                // Accept both media files and caption files as valid
                if (MediaFileExtensions.IsMediaFile(entry.Name) || MediaFileExtensions.IsCaptionFile(entry.Name))
                    hasValid = true;
                else
                    hasInvalid = true;

                if (hasValid && hasInvalid) break;
            }
        }
        catch (InvalidDataException)
        {
            // Corrupt or invalid ZIP file
            hasInvalid = true;
        }
        catch (IOException)
        {
            // File access error
            hasInvalid = true;
        }

        return (hasValid, hasInvalid);
    }

    private async Task CopyFilesToDatasetAsync(DatasetManagementViewModel vm, List<string> filesToCopy)
    {
        if (vm.ActiveDataset is null) return;

        vm.IsLoading = true;
        try
        {
            var copied = 0;
            var captionsCopied = 0;
            var skipped = 0;
            var destFolderPath = vm.ActiveDataset.CurrentVersionFolderPath;

            Directory.CreateDirectory(destFolderPath);

            foreach (var sourceFile in filesToCopy)
            {
                var fileName = Path.GetFileName(sourceFile);
                var destPath = Path.Combine(destFolderPath, fileName);

                if (!File.Exists(destPath))
                {
                    File.Copy(sourceFile, destPath);
                    
                    // Use centralized MediaFileExtensions for file type detection
                    if (MediaFileExtensions.IsCaptionFile(sourceFile))
                        captionsCopied++;
                    else
                        copied++;
                }
                else
                {
                    skipped++;
                }
            }

            // Use centralized MediaFileExtensions to check for videos
            var hasVideos = filesToCopy.Any(MediaFileExtensions.IsVideoFile);
            var fileType = hasVideos ? "files" : "images";

            var parts = new List<string>();
            if (copied > 0) parts.Add($"{copied} {fileType}");
            if (captionsCopied > 0) parts.Add($"{captionsCopied} captions");

            if (parts.Count > 0)
            {
                var addedText = $"Added {string.Join(", ", parts)}";
                vm.StatusMessage = skipped > 0 ? $"{addedText}, skipped {skipped} duplicates" : $"{addedText} to dataset";
            }
            else if (skipped > 0)
            {
                vm.StatusMessage = $"Skipped {skipped} duplicates (files already exist)";
            }

            await vm.RefreshActiveDatasetAsync();
        }
        catch (IOException ex)
        {
            vm.StatusMessage = $"Error adding files: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            vm.StatusMessage = $"Permission denied: {ex.Message}";
        }
        finally
        {
            vm.IsLoading = false;
        }
    }

    /// <summary>
    /// Handles pointer press on image cards for Ctrl+Click and Shift+Click selection.
    /// </summary>
    private void OnImageCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.DataContext is not DatasetImageViewModel image) return;
        if (DataContext is not DatasetManagementViewModel vm) return;

        var props = e.GetCurrentPoint(border).Properties;
        if (!props.IsLeftButtonPressed) return;

        var isCtrlPressed = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var isShiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (isShiftPressed || isCtrlPressed)
        {
            vm.SelectWithModifiers(image, isShiftPressed, isCtrlPressed);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Handles keyboard shortcuts for selection.
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not DatasetManagementViewModel vm) return;
        if (!vm.IsViewingDataset) return;

        if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.SelectAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.HasSelection)
        {
            vm.ClearSelectionCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Handles double-tap on an image to open the full-screen viewer.
    /// </summary>
    private void OnImageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border) return;

        var parent = border.Parent;
        while (parent is not null)
        {
            if (parent.DataContext is DatasetImageViewModel image)
            {
                if (DataContext is DatasetManagementViewModel vm)
                {
                    vm.OpenImageViewerCommand.Execute(image);
                }
                e.Handled = true;
                return;
            }
            parent = parent.Parent as Control;
        }
    }
}
