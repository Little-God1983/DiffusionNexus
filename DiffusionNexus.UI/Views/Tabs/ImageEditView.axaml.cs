using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif"];
    
    private static int _instanceCounter;
    private readonly int _instanceId;
    private ImageEditorControl? _imageEditorCanvas;
    private Border? _imageDropZone;
    private Button? _openImageButton;
    private bool _eventsWired;
    private long _lastSyncedInpaintBaseVersion = -1;


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
        _imageDropZone = this.FindControl<Border>("ImageDropZone");
        _openImageButton = this.FindControl<Button>("OpenImageButton");
        
        // Set up drag-drop handlers for drop zone
        if (_imageDropZone is not null)
        {
            _imageDropZone.AddHandler(DragDrop.DropEvent, OnImageDrop);
            _imageDropZone.AddHandler(DragDrop.DragEnterEvent, OnImageDragEnter);
            _imageDropZone.AddHandler(DragDrop.DragLeaveEvent, OnImageDragLeave);
        }
        
        // Set up open image button click handler
        if (_openImageButton is not null)
        {
            _openImageButton.Click += OnOpenImageButtonClick;
        }
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
        
        _eventsWired = true;
        
        var imageEditor = vm.ImageEditor;
        
        FileLogger.Log($"[Instance #{_instanceId}] Wiring events for CurrentImagePath={imageEditor.CurrentImagePath ?? "(null)"}");

        WireCanvasEvents(imageEditor);
        WireCropEvents(imageEditor);
        WireZoomAndTransformEvents(imageEditor);
        WireColorToolEvents(imageEditor);
        WireBackgroundRemovalEvents(imageEditor);
        WireBackgroundFillEvents(imageEditor);
        WireUpscalingEvents(imageEditor);
        WireDrawingEvents(imageEditor);
        WireInpaintingEvents(imageEditor);
        WireSaveAndExportEvents(vm, imageEditor);
        WireLayerEvents(vm, imageEditor);
        WireZoomSlider();
    }

    private void WireCanvasEvents(ImageEditorViewModel imageEditor)
    {
        _imageEditorCanvas!.ImageChanged += (_, _) =>
        {
            imageEditor.UpdateDimensions(
                _imageEditorCanvas.ImageWidth,
                _imageEditorCanvas.ImageHeight);
            imageEditor.UpdateFileInfo(
                _imageEditorCanvas.ImageDpi,
                _imageEditorCanvas.FileSizeBytes);
            
            imageEditor.LayerPanel.IsLayerMode = _imageEditorCanvas.EditorCore.IsLayerMode;
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);

            var coreVersion = _imageEditorCanvas.EditorCore.InpaintBaseVersion;
            if (coreVersion != _lastSyncedInpaintBaseVersion)
            {
                _lastSyncedInpaintBaseVersion = coreVersion;
                imageEditor.Inpainting.UpdateBaseThumbnail(
                    _imageEditorCanvas.EditorCore.HasInpaintBase
                        ? CreateThumbnailFromEditorCore(_imageEditorCanvas.EditorCore)
                        : null);
            }
        };

        _imageEditorCanvas.ZoomChanged += (_, _) =>
        {
            imageEditor.UpdateZoomInfo(
                _imageEditorCanvas.ZoomPercentage,
                _imageEditorCanvas.IsFitMode);
        };

        _imageEditorCanvas.CropApplied += (_, _) =>
        {
            imageEditor.OnCropApplied();
        };
    }

    private void WireCropEvents(ImageEditorViewModel imageEditor)
    {
        imageEditor.ClearRequested += (_, _) => _imageEditorCanvas!.ClearImage();
        imageEditor.ResetRequested += (_, _) => _imageEditorCanvas!.ResetToOriginal();

        imageEditor.CropToolActivated += (_, _) => _imageEditorCanvas!.ActivateCropTool();
        imageEditor.CropToolDeactivated += (_, _) => _imageEditorCanvas!.DeactivateCropTool();

        imageEditor.ApplyCropRequested += (_, _) =>
        {
            if (_imageEditorCanvas!.ApplyCrop())
                imageEditor.OnCropApplied();
        };

        imageEditor.CancelCropRequested += (_, _) =>
        {
            _imageEditorCanvas!.EditorCore.CropTool.ClearCropRegion();
            _imageEditorCanvas.DeactivateCropTool();
        };

        imageEditor.FitCropRequested += (_, _) =>
        {
            _imageEditorCanvas!.EditorCore.CropTool.FitToImage();
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.FillCropRequested += (_, _) =>
        {
            _imageEditorCanvas!.EditorCore.CropTool.FillImage();
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.SetCropAspectRatioRequested += (_, ratio) =>
        {
            _imageEditorCanvas!.EditorCore.CropTool.SetAspectRatio(ratio.W, ratio.H);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.SwitchCropAspectRatioRequested += (_, _) =>
        {
            _imageEditorCanvas!.EditorCore.CropTool.SwitchAspectRatio();
            _imageEditorCanvas.InvalidateVisual();
        };

        _imageEditorCanvas!.EditorCore.CropTool.CropRegionChanged += (_, _) =>
        {
            var (w, h) = _imageEditorCanvas.EditorCore.CropTool.GetCropPixelDimensions();
            imageEditor.UpdateCropResolution(w, h);
        };
    }

    private void WireZoomAndTransformEvents(ImageEditorViewModel imageEditor)
    {
        imageEditor.ZoomInRequested += (_, _) => _imageEditorCanvas!.ZoomIn();
        imageEditor.ZoomOutRequested += (_, _) => _imageEditorCanvas!.ZoomOut();
        imageEditor.ZoomToFitRequested += (_, _) => _imageEditorCanvas!.ZoomToFit();
        imageEditor.ZoomToActualRequested += (_, _) => _imageEditorCanvas!.ZoomToActual();

        imageEditor.RotateLeftRequested += (_, _) => _imageEditorCanvas!.EditorCore.RotateLeft();
        imageEditor.RotateRightRequested += (_, _) => _imageEditorCanvas!.EditorCore.RotateRight();
        imageEditor.Rotate180Requested += (_, _) => _imageEditorCanvas!.EditorCore.Rotate180();
        imageEditor.FlipHorizontalRequested += (_, _) => _imageEditorCanvas!.EditorCore.FlipHorizontal();
        imageEditor.FlipVerticalRequested += (_, _) => _imageEditorCanvas!.EditorCore.FlipVertical();
    }

    private void WireColorToolEvents(ImageEditorViewModel imageEditor)
    {
        imageEditor.ApplyColorBalanceRequested += (_, settings) =>
        {
            _imageEditorCanvas!.EditorCore.ClearPreview();
            if (_imageEditorCanvas.EditorCore.ApplyColorBalance(settings))
                imageEditor.OnColorBalanceApplied();
            else
                imageEditor.StatusMessage = "Failed to apply color balance.";
        };

        imageEditor.ColorBalancePreviewRequested += (_, settings) =>
            _imageEditorCanvas!.EditorCore.SetColorBalancePreview(settings);

        imageEditor.CancelColorBalancePreviewRequested += (_, _) =>
            _imageEditorCanvas!.EditorCore.ClearPreview();

        imageEditor.ApplyBrightnessContrastRequested += (_, settings) =>
        {
            _imageEditorCanvas!.EditorCore.ClearPreview();
            if (_imageEditorCanvas.EditorCore.ApplyBrightnessContrast(settings))
                imageEditor.OnBrightnessContrastApplied();
            else
                imageEditor.StatusMessage = "Failed to apply brightness/contrast.";
        };

        imageEditor.BrightnessContrastPreviewRequested += (_, settings) =>
            _imageEditorCanvas!.EditorCore.SetBrightnessContrastPreview(settings);

        imageEditor.CancelBrightnessContrastPreviewRequested += (_, _) =>
            _imageEditorCanvas!.EditorCore.ClearPreview();
    }

    private void WireBackgroundRemovalEvents(ImageEditorViewModel imageEditor)
    {
        imageEditor.BackgroundRemoval.RemoveBackgroundRequested += async (_, _) =>
        {
            var imageData = _imageEditorCanvas!.EditorCore.GetWorkingBitmapData();
            if (imageData is null) { imageEditor.StatusMessage = "No image loaded"; return; }

            await imageEditor.BackgroundRemoval.ProcessBackgroundRemovalAsync(
                imageData.Value.Data, imageData.Value.Width, imageData.Value.Height);
        };

        imageEditor.BackgroundRemoval.BackgroundRemovalCompleted += (_, result) =>
        {
            if (result.Success && result.MaskData is not null)
            {
                if (_imageEditorCanvas!.EditorCore.ApplyBackgroundMask(result.MaskData, result.Width, result.Height))
                    imageEditor.BackgroundRemoval.OnBackgroundRemovalApplied();
                else
                    imageEditor.StatusMessage = "Failed to apply background removal mask";
            }
        };

        imageEditor.BackgroundRemoval.RemoveBackgroundToLayerRequested += async (_, _) =>
        {
            var imageData = _imageEditorCanvas!.EditorCore.GetWorkingBitmapData();
            if (imageData is null) { imageEditor.StatusMessage = "No image loaded"; return; }

            await imageEditor.BackgroundRemoval.ProcessBackgroundRemovalToLayerAsync(
                imageData.Value.Data, imageData.Value.Width, imageData.Value.Height);
        };

        imageEditor.BackgroundRemoval.BackgroundRemovalToLayerCompleted += (_, result) =>
        {
            if (result.Success && result.MaskData is not null)
            {
                if (_imageEditorCanvas!.EditorCore.ApplyBackgroundMaskWithLayers(result.MaskData, result.Width, result.Height))
                {
                    imageEditor.BackgroundRemoval.OnBackgroundRemovalToLayerApplied();
                    imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
                }
                else
                {
                    imageEditor.StatusMessage = "Failed to create layers from background removal mask";
                }
            }
        };
    }

    private void WireBackgroundFillEvents(ImageEditorViewModel imageEditor)
    {
        imageEditor.BackgroundFill.PreviewRequested += (_, settings) =>
            _imageEditorCanvas!.EditorCore.SetBackgroundFillPreview(settings);

        imageEditor.BackgroundFill.CancelPreviewRequested += (_, _) =>
            _imageEditorCanvas!.EditorCore.ClearPreview();

        imageEditor.BackgroundFill.ApplyRequested += (_, settings) =>
        {
            _imageEditorCanvas!.EditorCore.ClearPreview();
            if (_imageEditorCanvas.EditorCore.ApplyBackgroundFill(settings))
                imageEditor.BackgroundFill.OnFillApplied();
            else
                imageEditor.StatusMessage = "Failed to apply background fill";
        };
    }

    private void WireUpscalingEvents(ImageEditorViewModel imageEditor)
    {
        imageEditor.Upscaling.UpscaleRequested += async (_, _) =>
        {
            var imageData = _imageEditorCanvas!.EditorCore.GetWorkingBitmapData();
            if (imageData is null) { imageEditor.StatusMessage = "No image loaded"; return; }

            await imageEditor.Upscaling.ProcessUpscalingAsync(
                imageData.Value.Data, imageData.Value.Width, imageData.Value.Height);
        };

        imageEditor.Upscaling.UpscalingCompleted += (_, result) =>
        {
            if (result.Success && result.ImageData is not null)
            {
                if (_imageEditorCanvas!.EditorCore.LoadImage(result.ImageData))
                {
                    imageEditor.Upscaling.OnUpscalingApplied();
                    imageEditor.UpdateDimensions(
                        _imageEditorCanvas.ImageWidth, _imageEditorCanvas.ImageHeight);
                }
                else
                {
                    imageEditor.StatusMessage = "Failed to load upscaled image";
                }
            }
        };
    }

    private void WireDrawingEvents(ImageEditorViewModel imageEditor)
    {
        imageEditor.DrawingToolActivated += (_, isActive) =>
        {
            var drawingTool = _imageEditorCanvas!.EditorCore.DrawingTool;
            drawingTool.IsActive = isActive;
            if (isActive)
                ApplyDrawingSettingsToTool(imageEditor, drawingTool);
        };

        imageEditor.DrawingSettingsChanged += (_, _) =>
        {
            var drawingTool = _imageEditorCanvas!.EditorCore.DrawingTool;
            drawingTool.BrushColor = new SkiaSharp.SKColor(
                imageEditor.DrawingTools.DrawingBrushRed,
                imageEditor.DrawingTools.DrawingBrushGreen,
                imageEditor.DrawingTools.DrawingBrushBlue);
            drawingTool.BrushSize = imageEditor.DrawingTools.DrawingBrushSize;
            drawingTool.BrushShape = imageEditor.DrawingTools.DrawingBrushShape;
        };

        imageEditor.CommitPlacedShapeRequested += (_, _) => _imageEditorCanvas!.CommitPlacedShape();
        imageEditor.CancelPlacedShapeRequested += (_, _) => _imageEditorCanvas!.CancelPlacedShape();

        _imageEditorCanvas!.PlacedShapeStateChanged += (_, _) =>
        {
            imageEditor.DrawingTools.HasPlacedShape = _imageEditorCanvas.HasPlacedShape;
        };
    }

    private void WireInpaintingEvents(ImageEditorViewModel imageEditor)
    {
        imageEditor.Inpainting.SetBaseRequested += (_, _) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.SetInpaintBaseBitmap();
            _lastSyncedInpaintBaseVersion = _imageEditorCanvas.EditorCore.InpaintBaseVersion;
            imageEditor.Inpainting.UpdateBaseThumbnail(CreateThumbnailFromEditorCore(_imageEditorCanvas.EditorCore));
        };

        imageEditor.Inpainting.ToolActivated += (_, isActive) =>
            _imageEditorCanvas!.IsInpaintingToolActive = isActive;

        imageEditor.Inpainting.SettingsChanged += (_, _) =>
            _imageEditorCanvas!.InpaintBrushSize = imageEditor.Inpainting.BrushSize;

        _imageEditorCanvas!.InpaintBrushSizeChanged += (_, newSize) =>
            imageEditor.Inpainting.BrushSize = newSize;

        _imageEditorCanvas.InpaintGenerateRequested += (_, _) =>
        {
            if (imageEditor.Inpainting.GenerateCommand.CanExecute(null))
                imageEditor.Inpainting.GenerateCommand.Execute(null);
        };

        var inpaintPromptTextBox = this.FindControl<TextBox>("InpaintPromptTextBox");
        if (inpaintPromptTextBox is not null)
        {
            inpaintPromptTextBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    if (imageEditor.Inpainting.GenerateCommand.CanExecute(null))
                        imageEditor.Inpainting.GenerateCommand.Execute(null);
                    e.Handled = true;
                }
            };
        }

        imageEditor.Inpainting.ClearMaskRequested += (_, _) =>
        {
            _imageEditorCanvas.EditorCore.ClearInpaintMask();
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.Inpainting.GenerateRequested += async (_, _) =>
        {
            if (_imageEditorCanvas is null) return;

            var tempPath = string.Empty;
            try
            {
                var editorCore = _imageEditorCanvas.EditorCore;
                var versionBefore = editorCore.InpaintBaseVersion;

                var prepareResult = editorCore.PrepareInpaintMaskedImage(imageEditor.Inpainting.MaskFeather);

                if (editorCore.InpaintBaseVersion != versionBefore)
                {
                    _lastSyncedInpaintBaseVersion = editorCore.InpaintBaseVersion;
                    imageEditor.Inpainting.UpdateBaseThumbnail(CreateThumbnailFromEditorCore(editorCore));
                }

                if (!prepareResult.Success)
                {
                    imageEditor.StatusMessage = prepareResult.ErrorMessage;
                    return;
                }

                tempPath = Path.Combine(Path.GetTempPath(), $"diffnexus_inpaint_{Guid.NewGuid():N}.png");
                await File.WriteAllBytesAsync(tempPath, prepareResult.MaskedImagePng!);

                if (imageEditor.Inpainting.IsCompareModePending)
                {
                    var beforePng = editorCore.GetInpaintBaseAsPng();
                    if (beforePng is not null)
                    {
                        var beforePath = Path.Combine(Path.GetTempPath(), $"diffnexus_inpaint_before_{Guid.NewGuid():N}.png");
                        await File.WriteAllBytesAsync(beforePath, beforePng);
                        imageEditor.Inpainting.SetCompareBeforeImagePath(beforePath);
                    }
                }

                await imageEditor.Inpainting.ProcessInpaintAsync(tempPath);
            }
            catch (Exception ex)
            {
                imageEditor.StatusMessage = $"Inpainting failed: {ex.Message}";
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            }
        };

        imageEditor.Inpainting.ResultReady += (_, imageBytes) =>
        {
            if (_imageEditorCanvas is null) return;

            var editorCore = _imageEditorCanvas.EditorCore;
            var resultBitmap = SkiaSharp.SKBitmap.Decode(imageBytes);
            if (resultBitmap is null)
            {
                imageEditor.StatusMessage = "Failed to decode inpainting result.";
                return;
            }

            if (resultBitmap.Width != editorCore.Width || resultBitmap.Height != editorCore.Height)
            {
                var resized = new SkiaSharp.SKBitmap(editorCore.Width, editorCore.Height);
                using var canvas = new SkiaSharp.SKCanvas(resized);
                canvas.DrawBitmap(resultBitmap,
                    new SkiaSharp.SKRect(0, 0, editorCore.Width, editorCore.Height));
                resultBitmap.Dispose();
                resultBitmap = resized;
            }

            editorCore.AddLayerFromBitmap(resultBitmap, "Inpaint Result");
            imageEditor.LayerPanel.SyncLayers(editorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        _imageEditorCanvas.InpaintMaskChanged += (_, _) =>
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
    }

    private void WireSaveAndExportEvents(ImageEditTabViewModel vm, ImageEditorViewModel imageEditor)
    {
        // Provide the View's save capability to the ViewModel
        imageEditor.SaveImageFunc = path =>
            _imageEditorCanvas?.EditorCore.SaveImage(path) ?? false;

        imageEditor.ShowSaveFileDialogFunc = async (title, suggestedFileName, filter) =>
        {
            if (vm.DialogService is null) return null;
            return await vm.DialogService.ShowSaveFileDialogAsync(title, suggestedFileName, filter);
        };

        imageEditor.SaveAsDialogRequested += async () =>
        {
            if (vm.DialogService is null || imageEditor.CurrentImagePath is null)
                return SaveAsResult.Cancelled();

            return await vm.DialogService.ShowSaveAsDialogAsync(
                imageEditor.CurrentImagePath,
                vm.EditorDatasets.Where(d => !d.IsTemporary),
                vm.SelectedEditorDataset?.Name,
                vm.SelectedEditorVersion?.Version);
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
    }

    private void WireLayerEvents(ImageEditTabViewModel vm, ImageEditorViewModel imageEditor)
    {
        imageEditor.EnableLayerModeRequested += (_, enable) =>
        {
            if (_imageEditorCanvas is null) return;
            if (enable) _imageEditorCanvas.EditorCore.EnableLayerMode();
            else _imageEditorCanvas.EditorCore.DisableLayerMode();
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.AddLayerRequested += (_, _) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.AddLayer();
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.DeleteLayerRequested += (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.RemoveLayer(layer);
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.DuplicateLayerRequested += (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.DuplicateLayer(layer);
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.MoveLayerUpRequested += (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.MoveLayerUp(layer);
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.MoveLayerDownRequested += (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.MoveLayerDown(layer);
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.MergeLayerDownRequested += (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.MergeLayerDown(layer);
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.MergeVisibleLayersRequested += (_, _) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.MergeVisibleLayers();
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.FlattenLayersRequested += (_, _) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.FlattenAllLayers();
            imageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
            _imageEditorCanvas.InvalidateVisual();
        };

        imageEditor.LayerSelectionChanged += (_, layer) =>
        {
            if (_imageEditorCanvas is null) return;
            _imageEditorCanvas.EditorCore.ActiveLayer = layer;
        };

        imageEditor.SaveLayeredTiffRequested += async (suggestedPath) =>
        {
            if (_imageEditorCanvas is null || vm.DialogService is null) return false;
            
            var savePath = await vm.DialogService.ShowSaveFileDialogAsync(
                "Save Layered TIFF", Path.GetFileName(suggestedPath), "*.tif");
                
            if (string.IsNullOrEmpty(savePath)) return false;
            return _imageEditorCanvas.EditorCore.SaveLayeredTiff(savePath);
        };
    }

    private void WireZoomSlider()
    {
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
    /// Applies the current drawing settings from the ViewModel to the drawing tool.
    /// </summary>
    private static void ApplyDrawingSettingsToTool(ImageEditorViewModel imageEditor, ImageEditor.DrawingTool drawingTool)
    {
        drawingTool.BrushColor = new SkiaSharp.SKColor(
            imageEditor.DrawingTools.DrawingBrushRed,
            imageEditor.DrawingTools.DrawingBrushGreen,
            imageEditor.DrawingTools.DrawingBrushBlue);
        drawingTool.BrushSize = imageEditor.DrawingTools.DrawingBrushSize;
        drawingTool.BrushShape = imageEditor.DrawingTools.DrawingBrushShape;
    }

    /// <summary>
    /// Creates a small Avalonia thumbnail bitmap from the EditorCore's current inpaint base.
    /// </summary>
    private static Avalonia.Media.Imaging.Bitmap? CreateThumbnailFromEditorCore(ImageEditor.ImageEditorCore editorCore)
    {
        var baseBitmap = editorCore.GetInpaintBaseBitmap();
        if (baseBitmap is null) return null;

        try
        {
            using var image = SkiaSharp.SKImage.FromBitmap(baseBitmap);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 80);
            baseBitmap.Dispose();

            using var memoryStream = new MemoryStream();
            data.SaveTo(memoryStream);
            memoryStream.Position = 0;
            return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(memoryStream, 160);
        }
        catch
        {
            baseBitmap.Dispose();
            return null;
        }
    }

    #region Image Drop Zone Handlers

    private void OnImageDragEnter(object? sender, DragEventArgs e)
    {
        if (_imageDropZone is null) return;

        var hasValidImage = AnalyzeImageFilesInDrag(e);

        if (hasValidImage)
        {
            _imageDropZone.BorderBrush = Brushes.LimeGreen;
            _imageDropZone.BorderThickness = new Thickness(3);
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            _imageDropZone.BorderBrush = Brushes.Red;
            _imageDropZone.BorderThickness = new Thickness(3);
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnImageDragLeave(object? sender, DragEventArgs e)
    {
        if (_imageDropZone is not null)
        {
            _imageDropZone.BorderBrush = new SolidColorBrush(Color.Parse("#444"));
            _imageDropZone.BorderThickness = new Thickness(3);
        }
    }

    private void OnImageDrop(object? sender, DragEventArgs e)
    {
        // Reset border
        if (_imageDropZone is not null)
        {
            _imageDropZone.BorderBrush = new SolidColorBrush(Color.Parse("#444"));
            _imageDropZone.BorderThickness = new Thickness(3);
        }

        var files = e.Data.GetFiles();
        if (files is null) return;

        // Find the first valid image file
        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                var filePath = file.Path.LocalPath;
                if (IsImageFile(filePath))
                {
                    LoadDroppedImage(filePath);
                    return;
                }
            }
        }
    }

    private bool AnalyzeImageFilesInDrag(DragEventArgs e)
    {
        var files = e.Data.GetFiles();
        if (files is null) return false;

        foreach (var item in files)
        {
            if (item is IStorageFile file)
            {
                var filePath = file.Path.LocalPath;
                if (IsImageFile(filePath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsImageFile(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return ImageExtensions.Contains(extension);
    }

    private static bool IsTiffFile(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension is ".tiff" or ".tif";
    }

    private void LoadDroppedImage(string filePath)
    {
        if (_imageEditorCanvas is null || DataContext is not ImageEditTabViewModel vm)
            return;

        try
        {
            FileLogger.Log($"Loading dropped image: {filePath}");
            
            bool loaded;
            
            // TIFF files are loaded as layers to preserve multi-page structure
            if (IsTiffFile(filePath))
            {
                FileLogger.Log($"Detected TIFF file, loading as layers: {filePath}");
                loaded = _imageEditorCanvas.LoadLayeredTiff(filePath);
            }
            else
            {
                loaded = _imageEditorCanvas.LoadImage(filePath);
            }

            if (loaded)
            {
                vm.ImageEditor.CurrentImagePath = filePath;
                
                // Sync layer state with ViewModel
                vm.ImageEditor.LayerPanel.IsLayerMode = _imageEditorCanvas.EditorCore.IsLayerMode;
                vm.ImageEditor.LayerPanel.SyncLayers(_imageEditorCanvas.EditorCore.Layers);
                
                vm.ImageEditor.StatusMessage = IsTiffFile(filePath)
                    ? $"Loaded: {Path.GetFileName(filePath)} ({_imageEditorCanvas.EditorCore.Layers?.Count ?? 1} layers)"
                    : $"Loaded: {Path.GetFileName(filePath)}";
                FileLogger.Log($"Successfully loaded dropped image: {filePath}");
            }
            else
            {
                vm.ImageEditor.StatusMessage = "Failed to load image.";
                FileLogger.LogError($"Failed to load dropped image: {filePath}");
            }
        }
        catch (Exception ex)
        {
            vm.ImageEditor.StatusMessage = $"Error loading image: {ex.Message}";
            FileLogger.LogError($"Exception loading dropped image: {filePath}", ex);
        }
    }

    private async void OnOpenImageButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ImageEditTabViewModel vm)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Image Files")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif", "*.webp", "*.tiff", "*.tif"]
                },
                new FilePickerFileType("All Files")
                {
                    Patterns = ["*.*"]
                }
            ]
        });

        if (files.Count > 0)
        {
            var filePath = files[0].Path.LocalPath;
            LoadDroppedImage(filePath);
        }
    }

    #endregion
}
