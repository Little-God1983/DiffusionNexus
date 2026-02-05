using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.Controls;
using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.Tabs;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.Views.Tabs;

/// <summary>
/// View for the Image Edit tab in the LoRA Dataset Helper.
/// Handles wiring up the ImageEditorControl events to the ViewModel.
/// </summary>
public partial class ImageEditView : UserControl
{
    private static int _instanceCounter;
    private readonly int _instanceId;
    private ImageEditorControl? _imageEditorCanvas;
    private bool _eventsWired;


    public ImageEditView()
    {
        _instanceId = Interlocked.Increment(ref _instanceCounter);
        FileLogger.Log($"ImageEditView instance #{_instanceId} created - Stack: {Environment.StackTrace}");
        
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
        FileLogger.Log($"[Instance #{_instanceId}] OnDataContextChanged called, _eventsWired={_eventsWired}, DataContext type={(DataContext?.GetType().Name ?? "null")}");
        
        if (_eventsWired)
        {
            FileLogger.Log($"[Instance #{_instanceId}] Already wired, skipping");
            return;
        }

        if (DataContext is ImageEditTabViewModel vm)
        {
            TryWireUpImageEditorEvents(vm);
        }
    }

    private void TryWireUpImageEditorEvents(ImageEditTabViewModel vm)
    {
        FileLogger.Log($"[Instance #{_instanceId}] TryWireUpImageEditorEvents called, _eventsWired={_eventsWired}");
        
        if (_eventsWired)
        {
            FileLogger.Log($"[Instance #{_instanceId}] Already wired in TryWireUp, skipping");
            return;
        }

        _imageEditorCanvas ??= this.FindControl<ImageEditorControl>("ImageEditorCanvas");

        if (_imageEditorCanvas is not null)
        {
            WireUpImageEditorEvents(vm);
        }
        else
        {
            FileLogger.Log($"[Instance #{_instanceId}] Canvas not found, subscribing to LayoutUpdated");
            LayoutUpdated += OnLayoutUpdatedForEditorInit;
        }
    }

    private void OnLayoutUpdatedForEditorInit(object? sender, EventArgs e)
    {
        FileLogger.Log($"[Instance #{_instanceId}] OnLayoutUpdatedForEditorInit called, _eventsWired={_eventsWired}");
        
        if (_eventsWired)
        {
            LayoutUpdated -= OnLayoutUpdatedForEditorInit;
            return;
        }

        _imageEditorCanvas ??= this.FindControl<ImageEditorControl>("ImageEditorCanvas");

        if (_imageEditorCanvas is not null && DataContext is ImageEditTabViewModel vm)
        {
            LayoutUpdated -= OnLayoutUpdatedForEditorInit;
            WireUpImageEditorEvents(vm);
        }
    }

    private void WireUpImageEditorEvents(ImageEditTabViewModel vm)
    {
        FileLogger.Log($">>> WireUpImageEditorEvents ENTRY - Instance #{_instanceId}, _eventsWired={_eventsWired}");
        
        // Double-check guard with logging
        if (_eventsWired)
        {
            FileLogger.LogWarning($"[Instance #{_instanceId}] WireUpImageEditorEvents called but already wired! Skipping.");
            return;
        }
        
        if (_imageEditorCanvas is null)
        {
            FileLogger.LogWarning($"[Instance #{_instanceId}] WireUpImageEditorEvents called but canvas is null! Skipping.");
            return;
        }
        
        // Set flag FIRST before doing anything else
        _eventsWired = true;
        
        var imageEditor = vm.ImageEditor;
        
        FileLogger.Log($"[Instance #{_instanceId}] Wiring events for CurrentImagePath={imageEditor.CurrentImagePath ?? "(null)"}");
        FileLogger.Log($"[Instance #{_instanceId}] _imageEditorCanvas is valid: {_imageEditorCanvas is not null}");


        // Update dimensions and file info when image changes
        _imageEditorCanvas.ImageChanged += (_, _) =>
        {
            imageEditor.UpdateDimensions(
                _imageEditorCanvas.ImageWidth,
                _imageEditorCanvas.ImageHeight);
            imageEditor.UpdateFileInfo(
                _imageEditorCanvas.ImageDpi,
                _imageEditorCanvas.FileSizeBytes);
            
            // Sync layer state when image changes (e.g., after load)
            imageEditor.IsLayerMode = _imageEditorCanvas.EditorCore.IsLayerMode;
            imageEditor.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
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

        // Handle drawing tool activation/deactivation and settings changes
        imageEditor.DrawingToolActivated += (_, isActive) =>
        {
            var drawingTool = _imageEditorCanvas.EditorCore.DrawingTool;
            drawingTool.IsActive = isActive;
            
            if (isActive)
            {
                // Apply current settings to the drawing tool
                ApplyDrawingSettingsToTool(imageEditor, drawingTool);
            }
        };

        // Handle drawing settings changes (color, size, shape)
        imageEditor.DrawingSettingsChanged += (_, settings) =>
        {
            var drawingTool = _imageEditorCanvas.EditorCore.DrawingTool;
            drawingTool.BrushColor = new SkiaSharp.SKColor(
                imageEditor.DrawingBrushRed,
                imageEditor.DrawingBrushGreen,
                imageEditor.DrawingBrushBlue);
            drawingTool.BrushSize = imageEditor.DrawingBrushSize;
            drawingTool.BrushShape = imageEditor.DrawingBrushShape;
        };


        // Handle save as dialog request
        imageEditor.SaveAsDialogRequested += async () =>
        {
            FileLogger.Log($"[Instance #{_instanceId}] SaveAsDialogRequested handler invoked");
            FileLogger.Log($"[Instance #{_instanceId}] CurrentImagePath={imageEditor.CurrentImagePath ?? "(null)"}");
            FileLogger.Log($"[Instance #{_instanceId}] About to show dialog...");
            
            if (vm.DialogService is null || imageEditor.CurrentImagePath is null)
            {
                FileLogger.LogWarning($"[Instance #{_instanceId}] DialogService or CurrentImagePath is null, returning Cancelled");
                return SaveAsResult.Cancelled();
            }
            var result = await vm.DialogService.ShowSaveAsDialogAsync(
                imageEditor.CurrentImagePath, 
                vm.EditorDatasets.Where(d => !d.IsTemporary));
            FileLogger.LogExit($"IsCancelled={result.IsCancelled}, FileName={result.FileName ?? "(null)"}");
            return result;


        };


        // Handle actual save after dialog confirmation
        imageEditor.SaveAsRequested += (_, result) =>
        {
            FileLogger.LogEntry($"IsCancelled={result.IsCancelled}, FileName={result.FileName ?? "(null)"}, Rating={result.Rating}, Destination={result.Destination}");
            FileLogger.Log($"CurrentImagePath={imageEditor.CurrentImagePath ?? "(null)"}");
            
            if (result.IsCancelled || string.IsNullOrWhiteSpace(result.FileName) || imageEditor.CurrentImagePath is null)
            {
                FileLogger.Log("Result is cancelled or invalid, returning");
                return;
            }

            // Verify the canvas control is available
            if (_imageEditorCanvas is null)
            {
                FileLogger.LogError("_imageEditorCanvas is null");
                imageEditor.StatusMessage = "Image editor not initialized.";
                return;
            }
            
            string newPath;
            var extension = Path.GetExtension(imageEditor.CurrentImagePath);

            if (result.Destination == SaveAsDestination.OriginFolder)
            {
                var directory = Path.GetDirectoryName(imageEditor.CurrentImagePath);
                if (string.IsNullOrEmpty(directory))
                {
                    FileLogger.LogError("Cannot determine save location - directory is null/empty");
                    imageEditor.StatusMessage = "Cannot determine save location.";
                    return;
                }
                newPath = Path.Combine(directory, result.FileName + extension);
            }
            else
            {
                var dataset = result.SelectedDataset;
                if (dataset == null)
                {
                    FileLogger.LogError("SelectedDataset is null for ExistingDataset destination");
                    imageEditor.StatusMessage = "No dataset selected.";
                    return;
                }
                var version = result.SelectedVersion ?? 1;
                var datasetFolderPath = dataset.GetVersionFolderPath(version);
                
                try
                {
                    if (!Directory.Exists(datasetFolderPath))
                    {
                        Directory.CreateDirectory(datasetFolderPath);
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.LogError($"Failed to create dataset directory: {datasetFolderPath}", ex);
                    imageEditor.StatusMessage = "Failed to create dataset directory.";
                    return;
                }
                
                newPath = Path.Combine(datasetFolderPath, result.FileName + extension);
            }

            FileLogger.Log($"New path to save: {newPath}");

            // Safety check: file existence is validated in dialog, but check again for race conditions
            if (File.Exists(newPath))
            {
                FileLogger.LogWarning($"File already exists: {newPath}");
                imageEditor.StatusMessage = $"File '{result.FileName}{extension}' already exists.";
                return;
            }

            // Save the image
            try
            {
                FileLogger.Log("Calling EditorCore.SaveImage...");
                if (_imageEditorCanvas.EditorCore.SaveImage(newPath))
                {
                    FileLogger.Log("SaveImage succeeded");
                    // Save rating to .rating file if not Unrated
                    SaveRatingToFile(newPath, result.Rating);
                    
                    FileLogger.Log("Calling OnSaveAsNewCompleted...");
                    imageEditor.OnSaveAsNewCompleted(newPath, result.Rating);
                    FileLogger.Log("OnSaveAsNewCompleted returned");
                }
                else
                {
                    FileLogger.LogError("SaveImage returned false");
                    imageEditor.StatusMessage = "Failed to save image.";
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("Exception during save", ex);
                imageEditor.StatusMessage = $"Error saving image: {ex.Message}";
            }
            
            FileLogger.LogExit();
        };


        imageEditor.SaveOverwriteConfirmRequested += async () =>
        {
            FileLogger.LogEntry();
            if (vm.DialogService is not null)
            {
                var result = await vm.DialogService.ShowConfirmAsync(
                    "Overwrite Image",
                    "Do you really want to overwrite your original image? This cannot be undone.");
                FileLogger.LogExit(result.ToString());
                return result;
            }
            FileLogger.LogExit("false (DialogService is null)");
            return false;
        };

        imageEditor.SaveOverwriteRequested += (_, _) =>
        {
            FileLogger.LogEntry();
            
            if (_imageEditorCanvas is null)
            {
                FileLogger.LogError("_imageEditorCanvas is null");
                imageEditor.StatusMessage = "Image editor not initialized.";
                return;
            }

            try
            {
                FileLogger.Log("Calling EditorCore.SaveOverwrite...");
                if (_imageEditorCanvas.EditorCore.SaveOverwrite())
                {
                    FileLogger.Log("SaveOverwrite succeeded");
                    FileLogger.Log("Calling OnSaveOverwriteCompleted...");
                    imageEditor.OnSaveOverwriteCompleted();
                    FileLogger.Log("OnSaveOverwriteCompleted returned");
                }
                else
                {
                    FileLogger.LogError("SaveOverwrite returned false");
                    imageEditor.StatusMessage = "Failed to save image.";
                }
            }
            catch (Exception ex)
            {
                FileLogger.LogError("Exception during save overwrite", ex);
                imageEditor.StatusMessage = $"Error saving image: {ex.Message}";
            }
            
            FileLogger.LogExit();
        };

        // Handle export requests
        imageEditor.ExportRequested += async (_, args) =>
        {
            FileLogger.LogEntry($"SuggestedFileName={args.SuggestedFileName}, FileExtension={args.FileExtension}");
            
            if (vm.DialogService is null)
            {
                FileLogger.LogError("DialogService is null");
                imageEditor.StatusMessage = "Export not available";
                return;
            }

            var exportPath = await vm.DialogService.ShowSaveFileDialogAsync(
                "Export Image",
                args.SuggestedFileName,
                $"*{args.FileExtension}");

            FileLogger.Log($"Export path from dialog: {exportPath ?? "(null/cancelled)"}");

            if (string.IsNullOrEmpty(exportPath))
            {
                FileLogger.Log("User cancelled export");
                return; // User cancelled
            }

            if (_imageEditorCanvas is null)
            {
                imageEditor.StatusMessage = "Image editor not initialized.";
                return;
            }

            try
            {
                if (_imageEditorCanvas.EditorCore.SaveImage(exportPath))
                {
                    imageEditor.OnExportCompleted(exportPath);
                }
                else
                {
                    imageEditor.StatusMessage = "Failed to export image.";
                }
            }
            catch (Exception ex)
            {
                imageEditor.StatusMessage = $"Error exporting image: {ex.Message}";
            }
        };

        // Layer mode event handlers
        imageEditor.EnableLayerModeRequested += (_, enable) =>
        {
            if (_imageEditorCanvas is null) return;
            
            if (enable)
            {
                _imageEditorCanvas.EditorCore.EnableLayerMode();
            }
            else
            {
                _imageEditorCanvas.EditorCore.DisableLayerMode();
            }
            
            // Sync layers with ViewModel
            imageEditor.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.AddLayerRequested += (_, _) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.AddLayer();
            imageEditor.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.DeleteLayerRequested += (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.RemoveLayer(layer);
            imageEditor.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.DuplicateLayerRequested += (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.DuplicateLayer(layer);
            imageEditor.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };


        imageEditor.MoveLayerUpRequested += (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            // UI "up" means towards front (higher index in LayerStack)
            _imageEditorCanvas.EditorCore.MoveLayerUp(layer);
            imageEditor.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.MoveLayerDownRequested += (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            // UI "down" means towards back (lower index in LayerStack)
            _imageEditorCanvas.EditorCore.MoveLayerDown(layer);
            imageEditor.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.MergeLayerDownRequested += (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.MergeLayerDown(layer);
            imageEditor.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.MergeVisibleLayersRequested += (_, _) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.MergeVisibleLayers();
            imageEditor.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.FlattenLayersRequested += (_, _) =>
        {
            if (_imageEditorCanvas is null) return;
            // Flatten all layers into one layer (keeps layer mode active)
            _imageEditorCanvas.EditorCore.FlattenAllLayers();
            imageEditor.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.LayerSelectionChanged += (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.ActiveLayer = layer;
        };

        imageEditor.SaveLayeredTiffRequested += async (suggestedPath) =>
        {
            if (_imageEditorCanvas is null) return false;
            
            if (vm.DialogService is null) return false;
            
            var savePath = await vm.DialogService.ShowSaveFileDialogAsync(
                "Save Layered TIFF",
                Path.GetFileName(suggestedPath),
                "*.tif");
                
            if (string.IsNullOrEmpty(savePath)) return false;
            
            return _imageEditorCanvas.EditorCore.SaveLayeredTiff(savePath);
        };

        // Wire up zoom slider
        var zoomSlider = this.FindControl<Slider>("ZoomSlider");
        if (zoomSlider is not null)
        {
            zoomSlider.PropertyChanged += (_, args) =>
            {
                if (args.Property.Name == nameof(Slider.Value) && _imageEditorCanvas is not null)
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

    /// <summary>
    /// Applies the current drawing settings from the ViewModel to the drawing tool.
    /// </summary>
    private static void ApplyDrawingSettingsToTool(ImageEditorViewModel imageEditor, ImageEditor.DrawingTool drawingTool)
    {
        drawingTool.BrushColor = new SkiaSharp.SKColor(
            imageEditor.DrawingBrushRed,
            imageEditor.DrawingBrushGreen,
            imageEditor.DrawingBrushBlue);
        drawingTool.BrushSize = imageEditor.DrawingBrushSize;
        drawingTool.BrushShape = imageEditor.DrawingBrushShape;
    }
}
