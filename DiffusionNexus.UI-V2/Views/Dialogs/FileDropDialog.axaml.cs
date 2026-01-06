using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// A reusable drag-and-drop file picker dialog.
/// Supports dragging files onto the dialog or clicking to browse.
/// </summary>
public partial class FileDropDialog : Window, INotifyPropertyChanged
{
    private static readonly string[] DefaultImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];
    private static readonly string[] DefaultVideoExtensions = [".mp4", ".mov", ".webm", ".avi", ".mkv", ".wmv", ".flv", ".m4v"];
    private static readonly string[] DefaultTextExtensions = [".txt", ".caption"];
    private static readonly string[] DefaultMediaExtensions = [..DefaultImageExtensions, ..DefaultVideoExtensions];
    
    private string[] _allowedExtensions = [];

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
        _allowedExtensions = [..DefaultMediaExtensions, ..DefaultTextExtensions];
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

    #endregion

    #region Drag and Drop Handlers

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var dropZone = this.FindControl<Border>("DropZone");
        if (dropZone is null) return;

        // Check if any of the dragged files are valid
        var hasValidFiles = HasValidFilesInDrag(e);
        
        if (hasValidFiles)
        {
            // Green border for valid files
            dropZone.BorderBrush = Avalonia.Media.Brushes.LimeGreen;
            dropZone.BorderThickness = new Avalonia.Thickness(3);
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            // Red border for invalid/unsupported files
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

    private void OnDrop(object? sender, DragEventArgs e)
    {
        // Reset border style
        OnDragLeave(sender, e);

        var files = e.Data.GetFiles();
        if (files is null) return;

        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                AddFile(file.Path.LocalPath);
            }
            else if (item is IStorageFolder folder)
            {
                AddFilesFromFolder(folder.Path.LocalPath);
            }
        }

        NotifyPropertiesChanged();
    }

    /// <summary>
    /// Checks if the drag event contains any valid files based on allowed extensions.
    /// </summary>
    private bool HasValidFilesInDrag(DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        if (files is null) return false;

        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                if (IsFileAllowed(file.Path.LocalPath))
                    return true;
            }
            else if (item is IStorageFolder folder)
            {
                // For folders, check if any files inside would be valid
                if (HasValidFilesInFolder(folder.Path.LocalPath))
                    return true;
            }
        }

        return false;
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
    /// Checks if a folder contains any valid files.
    /// </summary>
    private bool HasValidFilesInFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return false;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath))
            {
                if (IsFileAllowed(file))
                    return true;
            }
        }
        catch
        {
            // Ignore access errors
        }

        return false;
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
