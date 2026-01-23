using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Controls;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels.Tabs;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.Views.Tabs;

/// <summary>
/// View for the Image Edit tab in the LoRA Dataset Helper.
/// Handles wiring up the ImageEditorControl events to the ViewModel.
/// </summary>
public partial class ImageEditView : UserControl
{
    private ImageEditorControl? _imageEditorCanvas;
    private bool _eventsWired;

    public ImageEditView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _imageEditorCanvas = this.FindControl<ImageEditorControl>("ImageEditorCanvas");
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_eventsWired) return;

        if (DataContext is ImageEditTabViewModel vm)
        {
            TryWireUpImageEditorEvents(vm);
        }
    }

    private void TryWireUpImageEditorEvents(ImageEditTabViewModel vm)
    {
        if (_eventsWired) return;

        _imageEditorCanvas ??= this.FindControl<ImageEditorControl>("ImageEditorCanvas");

        if (_imageEditorCanvas is not null)
        {
            WireUpImageEditorEvents(vm);
        }
        else
        {
            LayoutUpdated += OnLayoutUpdatedForEditorInit;
        }
    }

    private void OnLayoutUpdatedForEditorInit(object? sender, EventArgs e)
    {
        if (_eventsWired) return;

        _imageEditorCanvas ??= this.FindControl<ImageEditorControl>("ImageEditorCanvas");

        if (_imageEditorCanvas is not null && DataContext is ImageEditTabViewModel vm)
        {
            LayoutUpdated -= OnLayoutUpdatedForEditorInit;
            WireUpImageEditorEvents(vm);
        }
    }

    private void WireUpImageEditorEvents(ImageEditTabViewModel vm)
    {
        if (_imageEditorCanvas is null || _eventsWired) return;

        _eventsWired = true;
        var imageEditor = vm.ImageEditor;

        // Update dimensions and file info when image changes
        _imageEditorCanvas.ImageChanged += (_, _) =>
        {
            imageEditor.UpdateDimensions(
                _imageEditorCanvas.ImageWidth,
                _imageEditorCanvas.ImageHeight);
            imageEditor.UpdateFileInfo(
                _imageEditorCanvas.ImageDpi,
                _imageEditorCanvas.FileSizeBytes);
        };

        // Update zoom info when zoom changes
        _imageEditorCanvas.ZoomChanged += (_, _) =>
        {
            imageEditor.UpdateZoomInfo(
                _imageEditorCanvas.ZoomPercentage,
                _imageEditorCanvas.IsFitMode);
        };

        // Handle crop applied
        _imageEditorCanvas.CropApplied += (_, _) =>
        {
            imageEditor.OnCropApplied();
        };

        // Handle clear/reset requests from ViewModel
        imageEditor.ClearRequested += (_, _) =>
        {
            _imageEditorCanvas.ClearImage();
        };

        imageEditor.ResetRequested += (_, _) =>
        {
            _imageEditorCanvas.ResetToOriginal();
        };

        // Handle crop tool activation/deactivation
        imageEditor.CropToolActivated += (_, _) =>
        {
            _imageEditorCanvas.ActivateCropTool();
        };

        imageEditor.CropToolDeactivated += (_, _) =>
        {
            _imageEditorCanvas.DeactivateCropTool();
        };

        // Handle crop apply/cancel requests
        imageEditor.ApplyCropRequested += (_, _) =>
        {
            if (_imageEditorCanvas.ApplyCrop())
            {
                imageEditor.OnCropApplied();
            }
        };

        imageEditor.CancelCropRequested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.CropTool.ClearCropRegion();
            _imageEditorCanvas.DeactivateCropTool();
        };

        // Handle zoom requests
        imageEditor.ZoomInRequested += (_, _) =>
        {
            _imageEditorCanvas.ZoomIn();
        };

        imageEditor.ZoomOutRequested += (_, _) =>
        {
            _imageEditorCanvas.ZoomOut();
        };

        imageEditor.ZoomToFitRequested += (_, _) =>
        {
            _imageEditorCanvas.ZoomToFit();
        };

        imageEditor.ZoomToActualRequested += (_, _) =>
        {
            _imageEditorCanvas.ZoomToActual();
        };

        // Handle transform requests
        imageEditor.RotateLeftRequested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.RotateLeft();
        };

        imageEditor.RotateRightRequested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.RotateRight();
        };

        imageEditor.Rotate180Requested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.Rotate180();
        };

        imageEditor.FlipHorizontalRequested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.FlipHorizontal();
        };

        imageEditor.FlipVerticalRequested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.FlipVertical();
        };

        // Handle color balance requests
        imageEditor.ApplyColorBalanceRequested += (_, settings) =>
        {
            // Clear preview first, then apply to working bitmap
            _imageEditorCanvas.EditorCore.ClearPreview();
            if (_imageEditorCanvas.EditorCore.ApplyColorBalance(settings))
            {
                imageEditor.OnColorBalanceApplied();
            }
            else
            {
                imageEditor.StatusMessage = "Failed to apply color balance.";
            }
        };

        // Handle color balance preview requests (live preview)
        imageEditor.ColorBalancePreviewRequested += (_, settings) =>
        {
            _imageEditorCanvas.EditorCore.SetColorBalancePreview(settings);
        };

        // Handle color balance preview cancel
        imageEditor.CancelColorBalancePreviewRequested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.ClearPreview();
        };

        // Handle brightness/contrast requests
        imageEditor.ApplyBrightnessContrastRequested += (_, settings) =>
        {
            // Clear preview first, then apply to working bitmap
            _imageEditorCanvas.EditorCore.ClearPreview();
            if (_imageEditorCanvas.EditorCore.ApplyBrightnessContrast(settings))
            {
                imageEditor.OnBrightnessContrastApplied();
            }
            else
            {
                imageEditor.StatusMessage = "Failed to apply brightness/contrast.";
            }
        };

        // Handle brightness/contrast preview requests (live preview)
        imageEditor.BrightnessContrastPreviewRequested += (_, settings) =>
        {
            _imageEditorCanvas.EditorCore.SetBrightnessContrastPreview(settings);
        };

        // Handle brightness/contrast preview cancel
        imageEditor.CancelBrightnessContrastPreviewRequested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.ClearPreview();
        };

        // Handle background removal requests
        imageEditor.RemoveBackgroundRequested += async (_, _) =>
        {
            var imageData = _imageEditorCanvas.EditorCore.GetWorkingBitmapData();
            if (imageData is null)
            {
                imageEditor.StatusMessage = "No image loaded";
                return;
            }

            await imageEditor.ProcessBackgroundRemovalAsync(
                imageData.Value.Data,
                imageData.Value.Width,
                imageData.Value.Height);
        };

        // Handle background removal completed
        imageEditor.BackgroundRemovalCompleted += (_, result) =>
        {
            if (result.Success && result.MaskData is not null)
            {
                // Apply the mask directly to the working bitmap
                if (_imageEditorCanvas.EditorCore.ApplyBackgroundMask(result.MaskData, result.Width, result.Height))
                {
                    imageEditor.OnBackgroundRemovalApplied();
                }
                else
                {
                    imageEditor.StatusMessage = "Failed to apply background removal mask";
                }
            }
        };

        // Handle background fill preview requests (live preview)
        imageEditor.BackgroundFillPreviewRequested += (_, settings) =>
        {
            _imageEditorCanvas.EditorCore.SetBackgroundFillPreview(settings);
        };

        // Handle background fill preview cancel
        imageEditor.CancelBackgroundFillPreviewRequested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.ClearPreview();
        };

        // Handle apply background fill
        imageEditor.ApplyBackgroundFillRequested += (_, settings) =>
        {
            // Clear preview first, then apply to working bitmap
            _imageEditorCanvas.EditorCore.ClearPreview();
            if (_imageEditorCanvas.EditorCore.ApplyBackgroundFill(settings))
            {
                imageEditor.OnBackgroundFillApplied();
            }
            else
            {
                imageEditor.StatusMessage = "Failed to apply background fill";
            }
        };

        // Handle upscaling requests
        imageEditor.UpscaleImageRequested += async (_, _) =>
        {
            var imageData = _imageEditorCanvas.EditorCore.GetWorkingBitmapData();
            if (imageData is null)
            {
                imageEditor.StatusMessage = "No image loaded";
                return;
            }

            await imageEditor.ProcessUpscalingAsync(
                imageData.Value.Data,
                imageData.Value.Width,
                imageData.Value.Height);
        };

        // Handle upscaling completed
        imageEditor.UpscalingCompleted += (_, result) =>
        {
            if (result.Success && result.ImageData is not null)
            {
                // Load the upscaled image (PNG bytes) into the editor
                if (_imageEditorCanvas.EditorCore.LoadImage(result.ImageData))
                {
                    imageEditor.OnUpscalingApplied();
                    // Update dimensions in ViewModel
                    imageEditor.UpdateDimensions(
                        _imageEditorCanvas.ImageWidth,
                        _imageEditorCanvas.ImageHeight);
                }
                else
                {
                    imageEditor.StatusMessage = "Failed to load upscaled image";
                }
            }
        };

        // Handle save as dialog request
        imageEditor.SaveAsDialogRequested += async () =>
        {
            if (vm.DialogService is null || imageEditor.CurrentImagePath is null)
            {
                return SaveAsResult.Cancelled();
            }

            var sourcePath = GetEffectiveSourcePath(imageEditor.CurrentImagePath);
            return await vm.DialogService.ShowSaveAsDialogAsync(sourcePath);
        };

        // Handle actual save after dialog confirmation
        imageEditor.SaveAsRequested += (_, result) =>
        {
            if (result.IsCancelled || string.IsNullOrWhiteSpace(result.FileName) || imageEditor.CurrentImagePath is null)
            {
                return;
            }

            var basePath = GetEffectiveSourcePath(imageEditor.CurrentImagePath);

            // Build the new file path
            var directory = Path.GetDirectoryName(basePath);
            var extension = Path.GetExtension(basePath);
            var newPath = Path.Combine(directory ?? string.Empty, result.FileName + extension);

            // Safety check: file existence is validated in dialog, but check again for race conditions
            if (File.Exists(newPath))
            {
                imageEditor.StatusMessage = $"File '{result.FileName}{extension}' already exists.";
                return;
            }

            // Save the image
            if (_imageEditorCanvas.EditorCore.SaveImage(newPath))
            {
                // Save rating to .rating file if not Unrated
                SaveRatingToFile(newPath, result.Rating);
                
                imageEditor.OnSaveAsNewCompleted(newPath, result.Rating);
            }
            else
            {
                imageEditor.StatusMessage = "Failed to save image.";
            }
        };

        imageEditor.SaveOverwriteConfirmRequested += async () =>
        {
            if (vm.DialogService is not null)
            {
                return await vm.DialogService.ShowConfirmAsync(
                    "Overwrite Image",
                    "Do you really want to overwrite your original image? This cannot be undone.");
            }
            return false;
        };

        imageEditor.SaveOverwriteRequested += (_, _) =>
        {
            if (!_imageEditorCanvas.EditorCore.SaveOverwrite())
            {
                imageEditor.StatusMessage = "Failed to save image.";
                return;
            }

            if (imageEditor.CurrentImagePath is null)
            {
                imageEditor.StatusMessage = "Failed to locate original image.";
                return;
            }

            if (TryResolveTemporarySourcePath(imageEditor.CurrentImagePath, out var sourcePath) &&
                !_imageEditorCanvas.EditorCore.SaveImage(sourcePath))
            {
                imageEditor.StatusMessage = "Failed to save image to original location.";
                return;
            }

            imageEditor.OnSaveOverwriteCompleted();
        };

        // Handle export requests
        imageEditor.ExportRequested += async (_, args) =>
        {
            if (vm.DialogService is null)
            {
                imageEditor.StatusMessage = "Export not available";
                return;
            }

            var exportPath = await vm.DialogService.ShowSaveFileDialogAsync(
                "Export Image",
                args.SuggestedFileName,
                $"*{args.FileExtension}");

            if (string.IsNullOrEmpty(exportPath))
            {
                return; // User cancelled
            }

            if (_imageEditorCanvas.EditorCore.SaveImage(exportPath))
            {
                imageEditor.OnExportCompleted(exportPath);
            }
            else
            {
                imageEditor.StatusMessage = "Failed to export image.";
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

    /// <summary>
    /// Saves the rating to a .rating file next to the image.
    /// </summary>
    private static void SaveRatingToFile(string imagePath, ImageRatingStatus rating)
    {
        try
        {
            var ratingFilePath = Path.ChangeExtension(imagePath, ".rating");
            
            if (rating == ImageRatingStatus.Unrated)
            {
                // Delete rating file if it exists and rating is Unrated
                if (File.Exists(ratingFilePath))
                {
                    File.Delete(ratingFilePath);
                }
            }
            else
            {
                // Write rating to file
                File.WriteAllText(ratingFilePath, rating.ToString());
            }
        }
        catch (IOException)
        {
            // File may be in use or read-only - rating will be lost on reload
        }
        catch (UnauthorizedAccessException)
        {
            // No permission to write - rating will be lost on reload
        }
    }

    private static bool TryResolveTemporarySourcePath(string imagePath, out string sourcePath)
    {
        sourcePath = string.Empty;

        if (!imagePath.StartsWith(TemporaryDatasetConstants.GenerationGalleryTempRootPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var metadataPath = imagePath + TemporaryDatasetConstants.SourceMetadataExtension;
        if (!File.Exists(metadataPath))
        {
            return false;
        }

        try
        {
            var metadataValue = File.ReadAllText(metadataPath);
            if (string.IsNullOrWhiteSpace(metadataValue))
            {
                return false;
            }

            sourcePath = metadataValue;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string GetEffectiveSourcePath(string imagePath)
    {
        return TryResolveTemporarySourcePath(imagePath, out var sourcePath)
            ? sourcePath
            : imagePath;
    }
}
