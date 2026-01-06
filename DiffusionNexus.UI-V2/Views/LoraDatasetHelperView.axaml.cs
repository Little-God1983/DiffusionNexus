using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using DiffusionNexus.UI.Controls;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

/// <summary>
/// View for the LoRA Dataset Helper module.
/// </summary>
public partial class LoraDatasetHelperView : UserControl
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".webm", ".avi", ".mkv", ".wmv", ".flv", ".m4v"];
    private static readonly string[] MediaExtensions = [..ImageExtensions, ..VideoExtensions];

    private ImageEditorControl? _imageEditorCanvas;
    private Border? _emptyDatasetDropZone;
    private TextBox? _descriptionTextBox;
    private bool _isInitialized;

    public LoraDatasetHelperView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _imageEditorCanvas = this.FindControl<ImageEditorControl>("ImageEditorCanvas");
        _emptyDatasetDropZone = this.FindControl<Border>("EmptyDatasetDropZone");
        _descriptionTextBox = this.FindControl<TextBox>("DescriptionTextBox");

        // Set up drag-drop handlers for empty dataset drop zone
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

    private void OnDescriptionLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Auto-save description when TextBox loses focus
        if (DataContext is LoraDatasetHelperViewModel vm && vm.ActiveDataset is not null)
        {
            vm.ActiveDataset.SaveMetadata();
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_isInitialized) return;
        _isInitialized = true;

        // Inject DialogService into the ViewModel
        if (VisualRoot is Window window && DataContext is IDialogServiceAware aware)
        {
            aware.DialogService = new DialogService(window);
        }

        // Load datasets on first attach
        if (DataContext is LoraDatasetHelperViewModel vm)
        {
            vm.CheckStorageConfigurationCommand.Execute(null);

            // Wire up image editor events
            WireUpImageEditorEvents(vm);
        }
    }

    private void WireUpImageEditorEvents(LoraDatasetHelperViewModel vm)
    {
        if (_imageEditorCanvas is null) return;

        // Update dimensions and file info when image changes
        _imageEditorCanvas.ImageChanged += (_, _) =>
        {
            vm.ImageEditor.UpdateDimensions(
                _imageEditorCanvas.ImageWidth,
                _imageEditorCanvas.ImageHeight);
            vm.ImageEditor.UpdateFileInfo(
                _imageEditorCanvas.ImageDpi,
                _imageEditorCanvas.FileSizeBytes);
        };

        // Update zoom info when zoom changes
        _imageEditorCanvas.ZoomChanged += (_, _) =>
        {
            vm.ImageEditor.UpdateZoomInfo(
                _imageEditorCanvas.ZoomPercentage,
                _imageEditorCanvas.IsFitMode);
        };

        // Handle crop applied
        _imageEditorCanvas.CropApplied += (_, _) =>
        {
            vm.ImageEditor.OnCropApplied();
        };

        // Handle clear/reset requests from ViewModel
        vm.ImageEditor.ClearRequested += (_, _) =>
        {
            _imageEditorCanvas.ClearImage();
        };

        vm.ImageEditor.ResetRequested += (_, _) =>
        {
            _imageEditorCanvas.ResetToOriginal();
        };

        // Handle crop tool activation/deactivation
        vm.ImageEditor.CropToolActivated += (_, _) =>
        {
            _imageEditorCanvas.ActivateCropTool();
        };

        vm.ImageEditor.CropToolDeactivated += (_, _) =>
        {
            _imageEditorCanvas.DeactivateCropTool();
        };

        // Handle crop apply/cancel requests
        vm.ImageEditor.ApplyCropRequested += (_, _) =>
        {
            if (_imageEditorCanvas.ApplyCrop())
            {
                vm.ImageEditor.OnCropApplied();
            }
        };

        vm.ImageEditor.CancelCropRequested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.CropTool.ClearCropRegion();
            _imageEditorCanvas.DeactivateCropTool();
        };

        // Handle zoom requests
        vm.ImageEditor.ZoomInRequested += (_, _) =>
        {
            _imageEditorCanvas.ZoomIn();
        };

        vm.ImageEditor.ZoomOutRequested += (_, _) =>
        {
            _imageEditorCanvas.ZoomOut();
        };

        vm.ImageEditor.ZoomToFitRequested += (_, _) =>
        {
            _imageEditorCanvas.ZoomToFit();
        };

        vm.ImageEditor.ZoomToActualRequested += (_, _) =>
        {
            _imageEditorCanvas.ZoomToActual();
        };

        // Handle save requests
        vm.ImageEditor.SaveAsNewRequested += async (_, _) =>
        {
            var newPath = _imageEditorCanvas.EditorCore.SaveAsNew();
            if (newPath is not null)
            {
                vm.ImageEditor.OnSaveAsNewCompleted(newPath);
                await vm.RefreshActiveDatasetAsync();
            }
            else
            {
                vm.ImageEditor.StatusMessage = "Failed to save image.";
            }
        };

        vm.ImageEditor.SaveOverwriteConfirmRequested += async () =>
        {
            if (vm.DialogService is not null)
            {
                return await vm.DialogService.ShowConfirmAsync(
                    "Overwrite Image",
                    "Do you really want to overwrite your original image? This cannot be undone.");
            }
            return false;
        };

        vm.ImageEditor.SaveOverwriteRequested += async (_, _) =>
        {
            if (_imageEditorCanvas.EditorCore.SaveOverwrite())
            {
                vm.ImageEditor.OnSaveOverwriteCompleted();
                await vm.RefreshActiveDatasetAsync();
            }
            else
            {
                vm.ImageEditor.StatusMessage = "Failed to save image.";
            }
        };

        // Wire up zoom slider
        var zoomSlider = this.FindControl<Slider>("ZoomSlider");
        if (zoomSlider is not null)
        {
            zoomSlider.PropertyChanged += (_, args) =>
            {
                if (args.Property.Name == nameof(Slider.Value))
                {
                    var percentage = (int)zoomSlider.Value;
                    _imageEditorCanvas.SetZoom(percentage / 100f);
                }
            };
        }
    }

    private void OnEmptyDatasetDragEnter(object? sender, DragEventArgs e)
    {
        // Don't accept drops while file dialog is open
        if (DataContext is LoraDatasetHelperViewModel vm && vm.IsFileDialogOpen)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        
        if (_emptyDatasetDropZone is null) return;

        // Analyze the dragged files
        var (hasValidFiles, hasInvalidFiles) = AnalyzeMediaFilesInDrag(e);
        
        if (hasValidFiles && hasInvalidFiles)
        {
            // Dark yellow/orange border for mixed files (some valid, some invalid)
            _emptyDatasetDropZone.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#DAA520")); // Goldenrod
            _emptyDatasetDropZone.BorderThickness = new Thickness(3);
            e.DragEffects = DragDropEffects.Copy;
        }
        else if (hasValidFiles)
        {
            // Green border for all valid files
            _emptyDatasetDropZone.BorderBrush = Avalonia.Media.Brushes.LimeGreen;
            _emptyDatasetDropZone.BorderThickness = new Thickness(3);
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            // Red border for all invalid/unsupported files
            _emptyDatasetDropZone.BorderBrush = Avalonia.Media.Brushes.Red;
            _emptyDatasetDropZone.BorderThickness = new Thickness(3);
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnEmptyDatasetDragLeave(object? sender, DragEventArgs e)
    {
        if (_emptyDatasetDropZone is not null)
        {
            // Reset to default border
            _emptyDatasetDropZone.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#444"));
            _emptyDatasetDropZone.BorderThickness = new Thickness(3);
        }
    }

    /// <summary>
    /// Analyzes the drag event to determine if it contains valid media files, invalid files, or both.
    /// </summary>
    /// <returns>A tuple of (hasValidFiles, hasInvalidFiles)</returns>
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
                var ext = Path.GetExtension(file.Path.LocalPath).ToLowerInvariant();
                if (MediaExtensions.Contains(ext))
                    hasValid = true;
                else
                    hasInvalid = true;
            }
            else if (item is IStorageFolder folder)
            {
                var (folderHasValid, folderHasInvalid) = AnalyzeMediaFilesInFolder(folder.Path.LocalPath);
                if (folderHasValid) hasValid = true;
                if (folderHasInvalid) hasInvalid = true;
            }

            // Early exit if we've found both types
            if (hasValid && hasInvalid) break;
        }

        return (hasValid, hasInvalid);
    }

    /// <summary>
    /// Analyzes a folder to determine if it contains valid media files, invalid files, or both.
    /// </summary>
    private (bool HasValid, bool HasInvalid) AnalyzeMediaFilesInFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return (false, false);

        var hasValid = false;
        var hasInvalid = false;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (MediaExtensions.Contains(ext))
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

    private async void OnEmptyDatasetDrop(object? sender, DragEventArgs e)
    {
        // Reset border style
        OnEmptyDatasetDragLeave(sender, e);

        if (DataContext is not LoraDatasetHelperViewModel vm || vm.ActiveDataset is null)
            return;

        // Don't accept drops while file dialog is open
        if (vm.IsFileDialogOpen)
            return;

        var files = e.Data.GetFiles();
        if (files is null) return;

        var mediaFiles = new List<string>();

        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                var ext = Path.GetExtension(file.Path.LocalPath).ToLowerInvariant();
                if (MediaExtensions.Contains(ext))
                {
                    mediaFiles.Add(file.Path.LocalPath);
                }
            }
            else if (item is IStorageFolder folder)
            {
                // Add media files from folder
                AddMediaFromFolder(folder.Path.LocalPath, mediaFiles);
            }
        }

        if (mediaFiles.Count == 0)
        {
            vm.StatusMessage = "No valid media files found in the dropped items.";
            return;
        }

        // Copy files to dataset
        await CopyFilesToDatasetAsync(vm, mediaFiles);
    }

    private void AddMediaFromFolder(string folderPath, List<string> mediaFiles)
    {
        if (!Directory.Exists(folderPath)) return;

        foreach (var file in Directory.EnumerateFiles(folderPath))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (MediaExtensions.Contains(ext))
            {
                mediaFiles.Add(file);
            }
        }
    }

    private async Task CopyFilesToDatasetAsync(LoraDatasetHelperViewModel vm, List<string> mediaFiles)
    {
        if (vm.ActiveDataset is null) return;

        vm.IsLoading = true;
        try
        {
            var copied = 0;
            var skipped = 0;
            var destFolderPath = vm.ActiveDataset.CurrentVersionFolderPath;

            // Ensure the folder exists
            Directory.CreateDirectory(destFolderPath);

            foreach (var sourceFile in mediaFiles)
            {
                var fileName = Path.GetFileName(sourceFile);
                var destPath = Path.Combine(destFolderPath, fileName);

                if (!File.Exists(destPath))
                {
                    File.Copy(sourceFile, destPath);
                    copied++;
                }
                else
                {
                    skipped++;
                }
            }

            var fileType = mediaFiles.Any(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())) 
                ? "files" 
                : "images";
            
            vm.StatusMessage = skipped > 0
                ? $"Added {copied} {fileType}, skipped {skipped} duplicates"
                : $"Added {copied} {fileType} to dataset";

            // Refresh the dataset to show new files
            await vm.RefreshActiveDatasetAsync();
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"Error adding files: {ex.Message}";
        }
        finally
        {
            vm.IsLoading = false;
        }
    }
}
